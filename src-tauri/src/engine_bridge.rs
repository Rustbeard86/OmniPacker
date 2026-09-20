//! Tauri-side bridge to the OmniPacker engine daemon.
//!
//! Thin adapter over the `engine-client` crate: lazily spawns the daemon, drives
//! it via the frozen IPC contract (docs/IPC-CONTRACT.md), forwards its events to
//! the webview on a single `engine-event` channel, and shuts it down on exit.
//! The frontend calls one command, `engine_request(method, params)`, so the UI
//! is bound to the contract, not to individual Rust functions.

use std::sync::Mutex;
use std::time::Duration;

use engine_client::EngineClient;
use engine_ipc::Message;
use serde_json::Value;
use tauri::{AppHandle, Emitter, State};

/// Default per-request timeout. Enumeration/login can be slow, so it is generous;
/// the daemon time-boxes its own long operations more tightly.
const REQUEST_TIMEOUT: Duration = Duration::from_secs(180);

/// Managed state holding the running daemon (spawned on first use).
pub struct EngineState {
    client: Mutex<Option<EngineClient>>,
}

impl EngineState {
    pub fn new() -> Self {
        Self { client: Mutex::new(None) }
    }

    /// Spawn the daemon and start forwarding its events, if not already running.
    fn ensure_started(&self, app: &AppHandle) -> Result<(), String> {
        let mut guard = self.client.lock().unwrap();
        if guard.is_some() {
            return Ok(());
        }

        let (program, args) = resolve_engine_command()?;
        let (client, events) =
            EngineClient::spawn(&program, &args, &[]).map_err(|e| format!("failed to start engine daemon: {e}"))?;

        // Re-emit every engine event to the webview as `engine-event`
        // ({event, data}); the frontend switches on payload.event.
        let app = app.clone();
        std::thread::spawn(move || {
            while let Ok(msg) = events.recv() {
                if let Message::Event { event, data } = msg {
                    let _ = app.emit("engine-event", serde_json::json!({ "event": event, "data": data }));
                }
            }
        });

        *guard = Some(client);
        Ok(())
    }

    /// Send one request to the daemon and return its result.
    fn request(&self, app: &AppHandle, method: &str, params: Option<Value>) -> Result<Value, String> {
        self.ensure_started(app)?;
        let guard = self.client.lock().unwrap();
        let client = guard.as_ref().ok_or("engine daemon not started")?;
        client
            .request(method, params, REQUEST_TIMEOUT)
            .map_err(|e| e.to_string())
    }

    /// Stop the daemon (dropping the client shuts it down). Called on app exit.
    pub fn kill_child(&self) {
        let mut guard = self.client.lock().unwrap();
        *guard = None;
    }
}

impl Default for EngineState {
    fn default() -> Self {
        Self::new()
    }
}

/// Resolve how to launch the daemon. Dev-first; a bundled sidecar comes later.
/// - OMNIPACKER_ENGINE_CMD: an explicit program to run (no args).
/// - OMNIPACKER_ENGINE_DLL: run `dotnet <dll>`.
/// - otherwise guess the dev build output relative to the working directory.
fn resolve_engine_command() -> Result<(String, Vec<String>), String> {
    if let Ok(cmd) = std::env::var("OMNIPACKER_ENGINE_CMD") {
        if !cmd.trim().is_empty() {
            return Ok((cmd, Vec::new()));
        }
    }
    if let Ok(dll) = std::env::var("OMNIPACKER_ENGINE_DLL") {
        if !dll.trim().is_empty() {
            return Ok(("dotnet".to_string(), vec![dll]));
        }
    }
    for guess in [
        "../engine/src/OmniPacker.EngineHost/bin/Debug/net10.0/OmniPacker.EngineHost.dll",
        "../engine/src/OmniPacker.EngineHost/bin/Release/net10.0/OmniPacker.EngineHost.dll",
        "engine/src/OmniPacker.EngineHost/bin/Debug/net10.0/OmniPacker.EngineHost.dll",
    ] {
        if std::path::Path::new(guess).exists() {
            return Ok(("dotnet".to_string(), vec![guess.to_string()]));
        }
    }
    Err("engine daemon not found; set OMNIPACKER_ENGINE_DLL to OmniPacker.EngineHost.dll (or build engine/)".to_string())
}

/// Frontend entry point: forward one method call to the engine over the contract.
#[tauri::command]
pub fn engine_request(
    app: AppHandle,
    state: State<'_, EngineState>,
    method: String,
    params: Option<Value>,
) -> Result<Value, String> {
    state.request(&app, &method, params)
}
