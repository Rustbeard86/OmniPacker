//! Update watcher.
//!
//! Polls Steam (via the fork's `-app-build` mode) on a timer for the build ids of
//! a user-selected watch list, and emits an `update:available` event when an
//! app's build id changes. Relies on the durable credential store (see
//! [`crate::output_dir::resolve_credentials_dir`]) so the background poll logs in
//! silently without a Steam Guard prompt.

use std::{
    collections::HashMap,
    io::Read,
    process::{Command, Stdio},
    sync::{Arc, Mutex},
    time::Duration,
};

use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Emitter, State};

#[cfg(windows)]
use std::os::windows::process::CommandExt;

use crate::depot_runner::resolve_depotdownloader_path;
use crate::output_dir::{resolve_credentials_dir, resolve_downloads_dir};
use crate::owned_apps::OwnedApp;

const APP_BUILDS_MARKER: &str = "OMNIPACKER_APP_BUILDS";
/// How often the background loop wakes to see if a check is due.
const TICK_SECS: u64 = 30;
/// Hard cap on a single poll's runtime so a hung sidecar can't wedge the loop.
const POLL_TIMEOUT_SECS: u64 = 180;

fn default_interval() -> u64 {
    60
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WatchEntry {
    pub name: String,
    #[serde(default = "default_public_branch")]
    pub branch: String,
    /// Last build id we've observed for `branch`; `None` until the first poll.
    #[serde(default)]
    pub last_build_id: Option<String>,
}

fn default_public_branch() -> String {
    "public".to_string()
}

#[derive(Clone, Debug, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct WatchConfig {
    #[serde(default)]
    pub enabled: bool,
    #[serde(default = "default_interval")]
    pub interval_minutes: u64,
    /// Account used for the silent background login (token reuse).
    #[serde(default)]
    pub username: String,
    /// appid (as string) -> entry.
    #[serde(default)]
    pub apps: HashMap<String, WatchEntry>,
}

#[derive(Clone)]
pub struct WatcherState {
    config: Arc<Mutex<WatchConfig>>,
}

impl WatcherState {
    pub fn new() -> Self {
        Self {
            config: Arc::new(Mutex::new(WatchConfig {
                interval_minutes: default_interval(),
                ..Default::default()
            })),
        }
    }

    fn snapshot(&self) -> WatchConfig {
        self.config
            .lock()
            .map(|guard| guard.clone())
            .unwrap_or_default()
    }
}

#[derive(Debug, Deserialize)]
struct AppBuildsPayload {
    #[serde(default)]
    apps: Vec<OwnedApp>,
}

fn config_path(app_handle: &AppHandle) -> Result<std::path::PathBuf, String> {
    let dir = resolve_downloads_dir(app_handle)?.join(".cache");
    std::fs::create_dir_all(&dir).map_err(|e| format!("Failed to create cache dir: {e}"))?;
    Ok(dir.join("watch.json"))
}

fn load_config(app_handle: &AppHandle) -> WatchConfig {
    config_path(app_handle)
        .ok()
        .and_then(|p| std::fs::read_to_string(p).ok())
        .and_then(|raw| serde_json::from_str(&raw).ok())
        .unwrap_or_else(|| WatchConfig {
            interval_minutes: default_interval(),
            ..Default::default()
        })
}

fn save_config(app_handle: &AppHandle, config: &WatchConfig) {
    if let Ok(path) = config_path(app_handle) {
        if let Ok(json) = serde_json::to_string_pretty(config) {
            let _ = std::fs::write(path, json);
        }
    }
}

/// Spawns the fork's `-app-build` mode for the given apps and returns the parsed
/// build ids. Background-safe: stdin is null so a missing token fails fast
/// instead of blocking on a password prompt.
fn query_builds_blocking(
    app_handle: &AppHandle,
    username: &str,
    appids: &[u32],
) -> Result<Vec<OwnedApp>, String> {
    if appids.is_empty() {
        return Ok(Vec::new());
    }
    if username.trim().is_empty() {
        return Err("No account configured for the update watcher.".to_string());
    }

    let path = resolve_depotdownloader_path(app_handle)?;
    let work_dir = resolve_downloads_dir(app_handle)?.join(".watch");
    std::fs::create_dir_all(&work_dir)
        .map_err(|e| format!("Failed to create watch work dir: {e}"))?;
    let credentials = resolve_credentials_dir(app_handle)?;

    let mut args = vec!["-app-build".to_string()];
    for id in appids {
        args.push(id.to_string());
    }
    args.push("-username".to_string());
    args.push(username.trim().to_string());
    args.push("-remember-password".to_string());

    let mut command = Command::new(&path);
    command.args(&args);
    command.current_dir(&work_dir);
    command.env("DEPOTDOWNLOADER_CONFIG_DIR", &credentials);
    command.stdin(Stdio::null());
    command.stdout(Stdio::piped());
    command.stderr(Stdio::null());
    #[cfg(windows)]
    {
        command.creation_flags(0x08000000); // CREATE_NO_WINDOW
    }

    let mut child = command
        .spawn()
        .map_err(|e| format!("Failed to spawn DepotDownloader: {e}"))?;

    let mut stdout = child
        .stdout
        .take()
        .ok_or_else(|| "No stdout from DepotDownloader".to_string())?;

    let (tx, rx) = std::sync::mpsc::channel();
    std::thread::spawn(move || {
        let mut buf = String::new();
        let _ = stdout.read_to_string(&mut buf);
        let _ = tx.send(buf);
    });

    let captured = rx
        .recv_timeout(Duration::from_secs(POLL_TIMEOUT_SECS))
        .unwrap_or_default();
    let _ = child.kill();
    let _ = child.wait();

    let json = captured
        .lines()
        .find_map(|line| line.strip_prefix(APP_BUILDS_MARKER))
        .map(|rest| rest.trim().to_string())
        .ok_or_else(|| {
            "DepotDownloader returned no build data (login may need refreshing via the Library tab)."
                .to_string()
        })?;

    let payload: AppBuildsPayload =
        serde_json::from_str(&json).map_err(|e| format!("Failed to parse build data: {e}"))?;
    Ok(payload.apps)
}

fn branch_build(app: &OwnedApp, branch: &str) -> Option<String> {
    app.branches
        .iter()
        .find(|b| b.name == branch)
        .and_then(|b| b.build_id.clone())
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct UpdatePayload {
    appid: u32,
    name: String,
    branch: String,
    previous_build_id: String,
    build_id: String,
}

/// Runs one poll: queries build ids for all watched apps, compares to the stored
/// baseline, emits `update:available` for any that changed, and persists.
fn run_poll(app_handle: &AppHandle, state: &WatcherState) {
    let config = state.snapshot();
    let appids: Vec<u32> = config.apps.keys().filter_map(|k| k.parse().ok()).collect();
    if appids.is_empty() {
        return;
    }

    let builds = match query_builds_blocking(app_handle, &config.username, &appids) {
        Ok(builds) => builds,
        Err(err) => {
            let _ = app_handle.emit("watch:error", err);
            return;
        }
    };

    let mut updates: Vec<UpdatePayload> = Vec::new();
    if let Ok(mut guard) = state.config.lock() {
        for app in &builds {
            let key = app.appid.to_string();
            let Some(entry) = guard.apps.get_mut(&key) else {
                continue;
            };
            let Some(current) = branch_build(app, &entry.branch) else {
                continue;
            };
            match &entry.last_build_id {
                Some(prev) if prev != &current => {
                    updates.push(UpdatePayload {
                        appid: app.appid,
                        name: entry.name.clone(),
                        branch: entry.branch.clone(),
                        previous_build_id: prev.clone(),
                        build_id: current.clone(),
                    });
                    entry.last_build_id = Some(current);
                }
                None => {
                    // First observation: record a baseline without notifying.
                    entry.last_build_id = Some(current);
                }
                _ => {}
            }
        }
        save_config(app_handle, &guard);
    }

    for update in &updates {
        let _ = app_handle.emit("update:available", update.clone());
    }
    let _ = app_handle.emit("watch:checked", updates.len() as u64);
}

/// Loads persisted config into state and starts the background polling thread.
pub fn start(app_handle: AppHandle, state: WatcherState) {
    if let Ok(mut guard) = state.config.lock() {
        *guard = load_config(&app_handle);
    }

    std::thread::spawn(move || {
        let mut elapsed: u64 = 0;
        loop {
            std::thread::sleep(Duration::from_secs(TICK_SECS));
            let config = state.snapshot();
            if !config.enabled || config.apps.is_empty() {
                elapsed = 0;
                continue;
            }
            elapsed += TICK_SECS;
            if elapsed < config.interval_minutes.max(1) * 60 {
                continue;
            }
            elapsed = 0;
            run_poll(&app_handle, &state);
        }
    });
}

#[tauri::command]
pub fn get_watch_config(state: State<'_, WatcherState>) -> WatchConfig {
    state.snapshot()
}

#[tauri::command]
pub fn set_watch_settings(
    app_handle: AppHandle,
    state: State<'_, WatcherState>,
    enabled: bool,
    interval_minutes: u64,
    username: String,
) -> Result<(), String> {
    let mut guard = state
        .config
        .lock()
        .map_err(|_| "Failed to lock watch state".to_string())?;
    guard.enabled = enabled;
    guard.interval_minutes = interval_minutes.max(1);
    if !username.trim().is_empty() {
        guard.username = username.trim().to_string();
    }
    save_config(&app_handle, &guard);
    Ok(())
}

#[tauri::command]
pub fn set_app_watch(
    app_handle: AppHandle,
    state: State<'_, WatcherState>,
    appid: u32,
    name: String,
    branch: String,
    enabled: bool,
) -> Result<(), String> {
    let mut guard = state
        .config
        .lock()
        .map_err(|_| "Failed to lock watch state".to_string())?;
    let key = appid.to_string();
    if enabled {
        let entry = guard.apps.entry(key).or_insert_with(|| WatchEntry {
            name: name.clone(),
            branch: if branch.is_empty() {
                default_public_branch()
            } else {
                branch.clone()
            },
            last_build_id: None,
        });
        entry.name = name;
        if !branch.is_empty() {
            // Changing the watched branch resets the baseline.
            if entry.branch != branch {
                entry.last_build_id = None;
            }
            entry.branch = branch;
        }
    } else {
        guard.apps.remove(&key);
    }
    save_config(&app_handle, &guard);
    Ok(())
}

/// Triggers an immediate poll on a background thread (non-blocking for the UI).
#[tauri::command]
pub fn check_updates_now(app_handle: AppHandle, state: State<'_, WatcherState>) -> Result<(), String> {
    let state = WatcherState {
        config: state.config.clone(),
    };
    std::thread::spawn(move || run_poll(&app_handle, &state));
    Ok(())
}
