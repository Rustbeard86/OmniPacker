//! Owned-app enumeration.
//!
//! Drives the DepotDownloader fork's `-list-owned` mode to fetch the logged-in
//! account's owned apps (games/applications, excluding free-to-play by default)
//! and returns them to the frontend so the user can pick an app + branch from a
//! library list instead of typing AppIDs by hand.
//!
//! Auth (QR / username+password / Steam Guard) reuses the same UX as downloads:
//! the process is spawned the same way, its console output is forwarded as
//! `enum:log` events (so the existing QR and Steam Guard modals work), and a
//! Steam Guard code can be submitted over stdin via
//! [`submit_enum_steam_guard_code`].

use std::{
    collections::HashMap,
    fs::File,
    io::{Read, Write},
    path::PathBuf,
    process::{Child, ChildStdin, Command, Stdio},
    sync::{Arc, Mutex},
    thread,
    time::Duration,
};

use chrono::{DateTime, Utc};

use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Emitter, State};

#[cfg(windows)]
use std::os::windows::process::CommandExt;

use crate::depot_runner::{
    decode_stream_bytes, persist_auth_cache, resolve_depotdownloader_path, restore_auth_cache,
};
use crate::output_dir::{resolve_credentials_dir, resolve_downloads_dir};

/// Marker prefix the fork prints (on stdout) before the single JSON blob.
const OWNED_APPS_JSON_MARKER: &str = "OMNIPACKER_OWNED_APPS_JSON";
/// Marker prefix the fork prints (on stderr) for coarse progress updates.
const ENUM_PROGRESS_MARKER: &str = "ENUM_PROGRESS";
/// Pseudo job id used only so the reused auth-cache helpers have something to log.
const ENUM_JOB_ID: &str = "__enumerate__";

/// Auth + options forwarded from the frontend for an enumeration run.
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EnumerateInput {
    #[serde(default)]
    pub username: String,
    #[serde(default)]
    pub password: String,
    #[serde(default)]
    pub qr_enabled: bool,
    /// Include free-to-play / no-cost apps. Defaults to false (excluded).
    #[serde(default)]
    pub include_free: bool,
    /// Force a fresh fetch from Steam, bypassing fresh cache (still cooldown-limited).
    #[serde(default)]
    pub force: bool,
}

/// How long a cached library is considered fresh (ownership changes rarely).
const CACHE_FRESH_SECS: i64 = 24 * 60 * 60;
/// Minimum spacing between forced refetches, to avoid hammering Steam's PICS.
const REFRESH_COOLDOWN_SECS: i64 = 30;

/// Result returned to the frontend: the apps plus cache provenance.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LibraryResult {
    pub apps: Vec<OwnedApp>,
    /// RFC3339 timestamp of when this set was actually fetched from Steam.
    pub fetched_at: String,
    /// True when served from the local cache without contacting Steam.
    pub from_cache: bool,
    /// True when the cached data is older than the freshness window.
    pub stale: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct CachedLibrary {
    fetched_at: String,
    include_free: bool,
    apps: Vec<OwnedApp>,
}

#[derive(Default, Serialize, Deserialize)]
struct LibraryCache {
    #[serde(default)]
    accounts: HashMap<String, CachedLibrary>,
}

fn cache_key(username: &str) -> String {
    let trimmed = username.trim().to_lowercase();
    if trimmed.is_empty() {
        "qr".to_string()
    } else {
        trimmed
    }
}

fn cache_path(app_handle: &AppHandle) -> Result<PathBuf, String> {
    let dir = resolve_downloads_dir(app_handle)?.join(".cache");
    std::fs::create_dir_all(&dir)
        .map_err(|e| format!("Failed to create cache directory: {e}"))?;
    Ok(dir.join("owned-apps.json"))
}

fn read_cache(path: &PathBuf) -> LibraryCache {
    std::fs::read_to_string(path)
        .ok()
        .and_then(|raw| serde_json::from_str(&raw).ok())
        .unwrap_or_default()
}

fn write_cache(path: &PathBuf, cache: &LibraryCache) {
    if let Ok(json) = serde_json::to_string(cache) {
        let _ = std::fs::write(path, json);
    }
}

/// Age in seconds of an RFC3339 timestamp, or `None` if unparseable.
fn age_secs(fetched_at: &str) -> Option<i64> {
    let then = DateTime::parse_from_rfc3339(fetched_at).ok()?;
    Some((Utc::now() - then.with_timezone(&Utc)).num_seconds())
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OwnedBranch {
    pub name: String,
    #[serde(default)]
    pub build_id: Option<String>,
    #[serde(default)]
    pub time_updated: Option<String>,
    #[serde(default)]
    pub pwd_required: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OwnedApp {
    pub appid: u32,
    pub name: String,
    #[serde(rename = "type", default)]
    pub app_type: String,
    #[serde(default)]
    pub branches: Vec<OwnedBranch>,
}

#[derive(Debug, Deserialize)]
struct OwnedAppsPayload {
    #[serde(rename = "ownedApps", default)]
    owned_apps: Vec<OwnedApp>,
}

struct EnumRunningState {
    child: Option<Child>,
    stdin: Option<ChildStdin>,
}

#[derive(Clone)]
pub struct EnumRunnerState {
    inner: Arc<Mutex<EnumRunningState>>,
}

impl EnumRunnerState {
    pub fn new() -> Self {
        Self {
            inner: Arc::new(Mutex::new(EnumRunningState {
                child: None,
                stdin: None,
            })),
        }
    }

    /// Kills the running enumeration child (if any). Called on app exit so a
    /// half-finished login process is not orphaned, mirroring DepotRunnerState.
    pub fn kill_child(&self) {
        if let Ok(mut guard) = self.inner.lock() {
            if let Some(ref mut child) = guard.child {
                let _ = child.kill();
            }
            guard.child = None;
            guard.stdin = None;
        }
    }
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct EnumLogPayload {
    stream: String,
    line: String,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct EnumProgressPayload {
    message: String,
}

fn emit_enum_log(app_handle: &AppHandle, stream: &str, line: &str) {
    let _ = app_handle.emit(
        "enum:log",
        EnumLogPayload {
            stream: stream.to_string(),
            line: line.to_string(),
        },
    );
}

fn emit_enum_progress(app_handle: &AppHandle, message: &str) {
    let _ = app_handle.emit(
        "enum:progress",
        EnumProgressPayload {
            message: message.to_string(),
        },
    );
}

fn build_enum_args(input: &EnumerateInput) -> Vec<String> {
    let mut args = vec!["-list-owned".to_string()];

    if input.include_free {
        args.push("-include-free".to_string());
    }

    let username = input.username.trim();
    let mut authed = false;
    if !username.is_empty() {
        // Username (with `-remember-password`) makes DepotDownloader reuse the
        // saved refresh token and log in silently. A QR flag can ride alongside:
        // the token is used when valid, and QR only appears when there's no
        // usable token — so it doubles as the recovery path on expiry.
        args.push("-username".to_string());
        args.push(username.to_string());
        if !input.password.is_empty() {
            args.push("-password".to_string());
            args.push(input.password.clone());
        }
        authed = true;
    }
    if input.qr_enabled {
        args.push("-qr".to_string());
        authed = true;
    }
    if authed {
        args.push("-remember-password".to_string());
    }
    // Empty username + no QR => anonymous, which the fork rejects with a clear
    // message (anonymous accounts have no license list).

    args
}

/// Redacts the value following `-password` so it never reaches a log file.
fn redact_args(args: &[String]) -> String {
    let mut out = Vec::with_capacity(args.len());
    let mut redact_next = false;
    for arg in args {
        if redact_next {
            out.push("***".to_string());
            redact_next = false;
            continue;
        }
        if arg == "-password" {
            redact_next = true;
        }
        out.push(arg.clone());
    }
    out.join(" ")
}

/// Creates a timestamped enumeration log file under the system temp dir. Always
/// on (independent of `--debug`) so a failed login can be diagnosed from the
/// file. Returns the path and a shared handle, or `None` if it can't be created.
fn open_enum_log() -> Option<(PathBuf, Arc<Mutex<File>>)> {
    let dir = std::env::temp_dir().join("OmniPacker").join("enum-logs");
    std::fs::create_dir_all(&dir).ok()?;
    let path = dir.join(format!("enum-{}.log", Utc::now().format("%Y%m%d-%H%M%S")));
    let file = File::create(&path).ok()?;
    Some((path, Arc::new(Mutex::new(file))))
}

fn log_line(log: &Option<Arc<Mutex<File>>>, text: &str) {
    if let Some(handle) = log {
        if let Ok(mut file) = handle.lock() {
            let _ = writeln!(file, "{text}");
            let _ = file.flush();
        }
    }
}

/// Reads a child stream line-by-line, capturing the JSON blob, forwarding
/// progress markers as `enum:progress`, and everything else as `enum:log`.
///
/// Uses the same chunked decoding as depot_runner's reader so the partial
/// (newline-less) Steam Guard email prompt is still surfaced.
fn spawn_enum_reader(
    app_handle: AppHandle,
    stream: impl Read + Send + 'static,
    tag: &str,
    json_capture: Arc<Mutex<Option<String>>>,
    tail: Arc<Mutex<Vec<String>>>,
    log: Option<Arc<Mutex<File>>>,
) -> thread::JoinHandle<()> {
    let stream_name = tag.to_string();
    const EMAIL_PROMPT: &str = "STEAM GUARD! Please enter the auth code sent to the email at";

    thread::spawn(move || {
        use std::io::BufReader;

        let handle_line = |app_handle: &AppHandle, line: String| {
            log_line(&log, &format!("[{stream_name}] {line}"));
            if let Some(rest) = line.strip_prefix(OWNED_APPS_JSON_MARKER) {
                if let Ok(mut slot) = json_capture.lock() {
                    *slot = Some(rest.trim().to_string());
                }
                return;
            }
            if let Some(rest) = line.strip_prefix(ENUM_PROGRESS_MARKER) {
                emit_enum_progress(app_handle, rest.trim());
                return;
            }
            // Keep the last few non-empty lines so a failure can report why.
            let trimmed = line.trim();
            if !trimmed.is_empty() {
                if let Ok(mut buf) = tail.lock() {
                    buf.push(trimmed.to_string());
                    let len = buf.len();
                    if len > 8 {
                        buf.drain(0..len - 8);
                    }
                }
            }
            emit_enum_log(app_handle, &stream_name, &line);
        };

        let mut reader = BufReader::new(stream);
        let mut buffer = [0u8; 1024];
        let mut pending: Vec<u8> = Vec::new();
        let mut prompt_emitted = false;

        loop {
            let n = match reader.read(&mut buffer) {
                Ok(0) => break,
                Ok(n) => n,
                Err(_) => break,
            };

            pending.extend_from_slice(&buffer[..n]);

            while let Some(pos) = pending.iter().position(|&byte| byte == b'\n') {
                let mut line_bytes: Vec<u8> = pending.drain(..=pos).collect();
                if let Some(b'\n') = line_bytes.last() {
                    line_bytes.pop();
                }
                if let Some(b'\r') = line_bytes.last() {
                    line_bytes.pop();
                }
                handle_line(&app_handle, decode_stream_bytes(&line_bytes));
            }

            if !prompt_emitted
                && pending
                    .windows(EMAIL_PROMPT.len())
                    .any(|window| window == EMAIL_PROMPT.as_bytes())
            {
                let line_bytes = std::mem::take(&mut pending);
                handle_line(&app_handle, decode_stream_bytes(&line_bytes));
                prompt_emitted = true;
            }
        }

        if !pending.is_empty() {
            let mut line_bytes = std::mem::take(&mut pending);
            if let Some(b'\r') = line_bytes.last() {
                line_bytes.pop();
            }
            handle_line(&app_handle, decode_stream_bytes(&line_bytes));
        }
    })
}

/// Enumerates the logged-in account's owned apps via the DepotDownloader fork.
///
/// Blocks until the process exits (login may involve QR/Steam Guard handled
/// concurrently through `enum:log` events and [`submit_enum_steam_guard_code`]),
/// then returns the parsed app list. Cancel mid-run with [`cancel_enumeration`].
#[tauri::command]
pub async fn enumerate_owned_apps(
    app_handle: AppHandle,
    state: State<'_, EnumRunnerState>,
    input: EnumerateInput,
) -> Result<LibraryResult, String> {
    let key = cache_key(&input.username);
    let include_free = input.include_free;
    let force = input.force;

    // Serve from the local cache when possible so we don't re-walk Steam's PICS
    // (and re-trigger a login) on every open. A forced refresh still respects a
    // short cooldown to avoid hammering Steam.
    let cache_file = cache_path(&app_handle).ok();
    if let Some(ref path) = cache_file {
        let cache = read_cache(path);
        if let Some(entry) = cache.accounts.get(&key) {
            if entry.include_free == include_free {
                let age = age_secs(&entry.fetched_at).unwrap_or(i64::MAX);
                let serve_cached = if force {
                    age < REFRESH_COOLDOWN_SECS // too soon to refetch again
                } else {
                    true // any cached entry is fine; mark stale if old
                };
                if serve_cached {
                    return Ok(LibraryResult {
                        apps: entry.apps.clone(),
                        fetched_at: entry.fetched_at.clone(),
                        from_cache: true,
                        stale: age >= CACHE_FRESH_SECS,
                    });
                }
            }
        }
    }

    // Cache miss / forced refresh: fetch from Steam. The work blocks (spawns
    // DepotDownloader and waits, which can take minutes during QR / Steam Guard
    // login). Sync Tauri commands run on the main thread, so doing it inline
    // would freeze the UI and stop QR / Steam Guard events from reaching the
    // webview. Run it on a blocking thread and await the result.
    let inner = state.inner.clone();
    let app_handle_for_task = app_handle.clone();
    let apps =
        tauri::async_runtime::spawn_blocking(move || run_enumeration(app_handle_for_task, inner, input))
            .await
            .map_err(|e| format!("Enumeration task failed to run: {e}"))??;

    let fetched_at = Utc::now().to_rfc3339();
    if let Some(ref path) = cache_file {
        let mut cache = read_cache(path);
        cache.accounts.insert(
            key,
            CachedLibrary {
                fetched_at: fetched_at.clone(),
                include_free,
                apps: apps.clone(),
            },
        );
        write_cache(path, &cache);
    }

    Ok(LibraryResult {
        apps,
        fetched_at,
        from_cache: false,
        stale: false,
    })
}

fn run_enumeration(
    app_handle: AppHandle,
    inner: Arc<Mutex<EnumRunningState>>,
    input: EnumerateInput,
) -> Result<Vec<OwnedApp>, String> {
    {
        let guard = inner
            .lock()
            .map_err(|_| "Failed to lock enumeration state".to_string())?;
        if guard.child.is_some() {
            return Err("An enumeration is already in progress.".to_string());
        }
    }

    let path = resolve_depotdownloader_path(&app_handle)?;

    // Stable working dir so DepotDownloader's own login token persists between
    // enumerations; restore the username-keyed auth cache that downloads also use
    // so a single login is shared across both flows.
    let downloads_dir = resolve_downloads_dir(&app_handle)?;
    let work_dir = downloads_dir.join(".enum");
    std::fs::create_dir_all(&work_dir)
        .map_err(|e| format!("Failed to create enumeration work directory: {e}"))?;

    let username = input.username.trim().to_string();
    if !username.is_empty() {
        let _ = restore_auth_cache(&app_handle, &username, &work_dir, ENUM_JOB_ID);
    }

    let args = build_enum_args(&input);

    let enum_log = open_enum_log();
    let log_path = enum_log.as_ref().map(|(path, _)| path.clone());
    let log_handle = enum_log.map(|(_, handle)| handle);
    log_line(
        &log_handle,
        &format!(
            "OmniPacker enumeration {}\nargs: {}\nwork_dir: {}\n---",
            Utc::now().to_rfc3339(),
            redact_args(&args),
            work_dir.display()
        ),
    );
    if let Some(ref path) = log_path {
        emit_enum_log(&app_handle, "system", &format!("Enumeration log: {}", path.display()));
    }

    let mut command = Command::new(&path);
    command.args(&args);
    command.current_dir(&work_dir);
    if let Ok(cred) = resolve_credentials_dir(&app_handle) {
        command.env("DEPOTDOWNLOADER_CONFIG_DIR", cred);
    }
    command.stdout(Stdio::piped());
    command.stderr(Stdio::piped());
    command.stdin(Stdio::piped());

    #[cfg(windows)]
    {
        if !crate::debug_console::debug_console_enabled_static(&app_handle) {
            command.creation_flags(0x08000000); // CREATE_NO_WINDOW
        }
    }

    let mut child = command
        .spawn()
        .map_err(|e| format!("Failed to spawn DepotDownloader: {e}"))?;

    let stdout = child.stdout.take();
    let stderr = child.stderr.take();
    let stdin = child.stdin.take();

    {
        let mut guard = inner
            .lock()
            .map_err(|_| "Failed to lock enumeration state".to_string())?;
        guard.child = Some(child);
        guard.stdin = stdin;
    }

    let json_capture: Arc<Mutex<Option<String>>> = Arc::new(Mutex::new(None));
    let tail: Arc<Mutex<Vec<String>>> = Arc::new(Mutex::new(Vec::new()));
    let stdout_handle = stdout.map(|s| {
        spawn_enum_reader(
            app_handle.clone(),
            s,
            "stdout",
            json_capture.clone(),
            tail.clone(),
            log_handle.clone(),
        )
    });
    let stderr_handle = stderr.map(|s| {
        spawn_enum_reader(
            app_handle.clone(),
            s,
            "stderr",
            json_capture.clone(),
            tail.clone(),
            log_handle.clone(),
        )
    });

    // Poll for exit. The lock is released between polls so the Steam Guard and
    // cancel commands can act on the shared child handle while we wait.
    let mut cancelled = false;
    let exit_status = loop {
        let status = {
            let mut guard = match inner.lock() {
                Ok(guard) => guard,
                Err(_) => return Err("Enumeration state lock poisoned".to_string()),
            };
            let Some(child) = guard.child.as_mut() else {
                cancelled = true;
                break None;
            };
            match child.try_wait() {
                Ok(Some(status)) => {
                    guard.child = None;
                    Some(status)
                }
                Ok(None) => None,
                Err(e) => {
                    guard.child = None;
                    return Err(format!("Failed to wait on DepotDownloader: {e}"));
                }
            }
        };

        match status {
            Some(status) => break Some(status),
            None => thread::sleep(Duration::from_millis(150)),
        }
    };

    if let Some(handle) = stdout_handle {
        let _ = handle.join();
    }
    if let Some(handle) = stderr_handle {
        let _ = handle.join();
    }

    if let Ok(mut guard) = inner.lock() {
        guard.child = None;
        guard.stdin = None;
    }

    if !username.is_empty() {
        let _ = persist_auth_cache(&app_handle, &username, &work_dir, ENUM_JOB_ID);
    }

    let code = exit_status.as_ref().and_then(|s| s.code());
    let _ = app_handle.emit(
        "enum:status",
        serde_json::json!({ "status": "exited", "code": code }),
    );

    let log_suffix = log_path
        .as_ref()
        .map(|p| format!(" See log: {}", p.display()))
        .unwrap_or_default();

    if cancelled {
        log_line(&log_handle, "--- result: cancelled");
        return Err("Enumeration was cancelled.".to_string());
    }

    if code != Some(0) {
        let detail = tail
            .lock()
            .ok()
            .map(|buf| {
                let len = buf.len();
                buf[len.saturating_sub(4)..].join(" | ")
            })
            .filter(|s| !s.is_empty())
            .map(|s| format!(" Last output: {s}"))
            .unwrap_or_default();
        log_line(&log_handle, &format!("--- result: exit code {code:?}"));
        return Err(format!(
            "DepotDownloader exited with code {code:?} before producing an app list.{detail}{log_suffix}"
        ));
    }

    let json = json_capture.lock().ok().and_then(|slot| slot.clone());
    let Some(json) = json else {
        log_line(&log_handle, "--- result: exit 0 but no JSON marker captured");
        return Err(format!(
            "DepotDownloader did not return an owned-apps list. Make sure you are signed in.{log_suffix}"
        ));
    };

    let payload: OwnedAppsPayload = serde_json::from_str(&json)
        .map_err(|e| format!("Failed to parse owned-apps JSON: {e}{log_suffix}"))?;

    log_line(
        &log_handle,
        &format!("--- result: {} apps", payload.owned_apps.len()),
    );
    Ok(payload.owned_apps)
}

/// Submits a Steam Guard code (mobile or email) to a running enumeration's stdin.
#[tauri::command]
pub fn submit_enum_steam_guard_code(
    code: String,
    state: State<'_, EnumRunnerState>,
) -> Result<(), String> {
    let trimmed = code.trim();
    if trimmed.is_empty() {
        return Err("Steam Guard code is empty".to_string());
    }

    let mut guard = state
        .inner
        .lock()
        .map_err(|_| "Failed to lock enumeration state".to_string())?;

    if guard.child.is_none() {
        return Err("Enumeration is not running".to_string());
    }

    let Some(stdin) = guard.stdin.as_mut() else {
        return Err("DepotDownloader stdin is unavailable".to_string());
    };

    stdin
        .write_all(trimmed.as_bytes())
        .map_err(|err| format!("Failed to write Steam Guard code: {err}"))?;
    stdin
        .write_all(b"\n")
        .map_err(|err| format!("Failed to submit Steam Guard code: {err}"))?;
    stdin
        .flush()
        .map_err(|err| format!("Failed to flush Steam Guard code: {err}"))?;

    Ok(())
}

/// Cancels a running enumeration by killing the child process. The blocked
/// [`enumerate_owned_apps`] call then reaps it and returns an error.
#[tauri::command]
pub fn cancel_enumeration(state: State<'_, EnumRunnerState>) -> Result<(), String> {
    let mut guard = state
        .inner
        .lock()
        .map_err(|_| "Failed to lock enumeration state".to_string())?;

    if let Some(child) = guard.child.as_mut() {
        let _ = child.kill();
    }
    guard.stdin = None;
    Ok(())
}
