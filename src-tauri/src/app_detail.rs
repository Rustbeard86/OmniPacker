//! Per-game app detail (depots + oslist via the fork's `-app-detail` mode) and a
//! persisted per-game archive config (target OS list + branch) used by the
//! Library config panel, auto-archive, and add-to-queue.

use std::{
    collections::HashMap,
    io::Read,
    process::{Command, Stdio},
    sync::{Arc, Mutex},
    time::Duration,
};

use serde::{Deserialize, Serialize};
use tauri::{AppHandle, State};

#[cfg(windows)]
use std::os::windows::process::CommandExt;

use crate::depot_runner::resolve_depotdownloader_path;
use crate::output_dir::{resolve_credentials_dir, resolve_downloads_dir};
use crate::owned_apps::OwnedBranch;

const APP_DETAIL_MARKER: &str = "OMNIPACKER_APP_DETAIL";
const QUERY_TIMEOUT_SECS: u64 = 180;

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DepotDetail {
    pub depot_id: String,
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub oslist: Option<String>,
    #[serde(default)]
    pub size: Option<String>,
    #[serde(default)]
    pub manifest_id: Option<String>,
    #[serde(default)]
    pub dlc_app_id: Option<String>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppDetail {
    pub appid: u32,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub branches: Vec<OwnedBranch>,
    #[serde(default)]
    pub depots: Vec<DepotDetail>,
}

#[derive(Debug, Deserialize)]
struct AppDetailPayload {
    #[serde(default)]
    apps: Vec<AppDetail>,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DetailInput {
    pub appids: Vec<u32>,
    #[serde(default)]
    pub username: String,
}

/// Per-game archive preferences. Empty `os_targets` means "use the global OS
/// selector"; an empty `branch` means the default (public) branch.
#[derive(Clone, Debug, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct AppArchiveConfig {
    #[serde(default)]
    pub branch: String,
    #[serde(default)]
    pub os_targets: Vec<String>,
}

#[derive(Default, Serialize, Deserialize)]
struct AppConfigStore {
    #[serde(default)]
    apps: HashMap<String, AppArchiveConfig>,
}

fn config_path(app_handle: &AppHandle) -> Result<std::path::PathBuf, String> {
    let dir = resolve_downloads_dir(app_handle)?.join(".cache");
    std::fs::create_dir_all(&dir).map_err(|e| format!("Failed to create cache dir: {e}"))?;
    Ok(dir.join("app-config.json"))
}

fn read_store(app_handle: &AppHandle) -> AppConfigStore {
    config_path(app_handle)
        .ok()
        .and_then(|p| std::fs::read_to_string(p).ok())
        .and_then(|raw| serde_json::from_str(&raw).ok())
        .unwrap_or_default()
}

fn write_store(app_handle: &AppHandle, store: &AppConfigStore) {
    if let Ok(path) = config_path(app_handle) {
        if let Ok(json) = serde_json::to_string_pretty(store) {
            let _ = std::fs::write(path, json);
        }
    }
}

fn query_detail_blocking(
    app_handle: &AppHandle,
    input: &DetailInput,
) -> Result<Vec<AppDetail>, String> {
    if input.appids.is_empty() {
        return Ok(Vec::new());
    }
    if input.username.trim().is_empty() {
        return Err("Sign in via the Library first (no account available).".to_string());
    }

    let path = resolve_depotdownloader_path(app_handle)?;
    let work_dir = resolve_downloads_dir(app_handle)?.join(".watch");
    std::fs::create_dir_all(&work_dir).map_err(|e| format!("Failed to create work dir: {e}"))?;
    let credentials = resolve_credentials_dir(app_handle)?;

    let mut args = vec!["-app-detail".to_string()];
    for id in &input.appids {
        args.push(id.to_string());
    }
    args.push("-username".to_string());
    args.push(input.username.trim().to_string());
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
        .recv_timeout(Duration::from_secs(QUERY_TIMEOUT_SECS))
        .unwrap_or_default();
    let _ = child.kill();
    let _ = child.wait();

    let json = captured
        .lines()
        .find_map(|line| line.strip_prefix(APP_DETAIL_MARKER))
        .map(|rest| rest.trim().to_string())
        .ok_or_else(|| {
            "DepotDownloader returned no detail (login may need refreshing via the Library tab)."
                .to_string()
        })?;

    let payload: AppDetailPayload =
        serde_json::from_str(&json).map_err(|e| format!("Failed to parse app detail: {e}"))?;
    Ok(payload.apps)
}

/// Per-game state managed by Tauri. The lock only guards on-disk writes; the
/// config is small and read/written rarely.
#[derive(Clone)]
pub struct AppConfigState {
    lock: Arc<Mutex<()>>,
}

impl AppConfigState {
    pub fn new() -> Self {
        Self {
            lock: Arc::new(Mutex::new(())),
        }
    }
}

#[tauri::command]
pub async fn get_app_detail(
    app_handle: AppHandle,
    input: DetailInput,
) -> Result<Vec<AppDetail>, String> {
    tauri::async_runtime::spawn_blocking(move || query_detail_blocking(&app_handle, &input))
        .await
        .map_err(|e| format!("App-detail task failed to run: {e}"))?
}

#[tauri::command]
pub fn get_app_config(app_handle: AppHandle, appid: u32) -> AppArchiveConfig {
    read_store(&app_handle)
        .apps
        .get(&appid.to_string())
        .cloned()
        .unwrap_or_default()
}

#[tauri::command]
pub fn set_app_config(
    app_handle: AppHandle,
    state: State<'_, AppConfigState>,
    appid: u32,
    config: AppArchiveConfig,
) -> Result<(), String> {
    let _guard = state
        .lock
        .lock()
        .map_err(|_| "Failed to lock app-config state".to_string())?;
    let mut store = read_store(&app_handle);
    let key = appid.to_string();
    let is_default = config.branch.trim().is_empty() && config.os_targets.is_empty();
    if is_default {
        store.apps.remove(&key);
    } else {
        store.apps.insert(key, config);
    }
    write_store(&app_handle, &store);
    Ok(())
}
