//! Spawns and drives the OmniPacker engine daemon over the line-JSON IPC
//! protocol (see docs/IPC-CONTRACT.md).
//!
//! Blocking, std-only (no async runtime): a background reader thread parses the
//! daemon's stdout, resolves each response to the request that is waiting on its
//! id, and forwards unsolicited events onto a channel the caller drains (the
//! Tauri layer re-emits them to the webview). No Tauri dependency, so this is
//! testable by spawning the real daemon.

use std::collections::HashMap;
use std::io::{BufRead, BufReader, Write};
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc::{channel, Receiver, RecvTimeoutError, Sender};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::Duration;

use engine_ipc::{Message, RpcError};
use serde_json::Value;

/// What can go wrong driving the daemon.
#[derive(Debug)]
pub enum ClientError {
    /// The engine returned a structured error response.
    Rpc(RpcError),
    /// No response arrived within the timeout.
    Timeout,
    /// The daemon stream closed before responding.
    Disconnected,
    /// I/O error writing to the daemon.
    Io(std::io::Error),
}

impl std::fmt::Display for ClientError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ClientError::Rpc(e) => write!(f, "engine error [{}]: {}", e.code, e.message),
            ClientError::Timeout => write!(f, "engine request timed out"),
            ClientError::Disconnected => write!(f, "engine daemon disconnected"),
            ClientError::Io(e) => write!(f, "engine io error: {e}"),
        }
    }
}
impl std::error::Error for ClientError {}

type Pending = Arc<Mutex<HashMap<u64, Sender<Result<Value, RpcError>>>>>;

/// A running engine daemon the host drives.
pub struct EngineClient {
    child: Child,
    stdin: Mutex<ChildStdin>,
    next_id: AtomicU64,
    pending: Pending,
    reader: Option<JoinHandle<()>>,
}

impl EngineClient {
    /// Spawn `program args...` as the daemon. Returns the client and a receiver
    /// of unsolicited events (`ready`, `log`, `auth.status`, ...). `env` entries
    /// are set on the child (e.g. OMNIPACKER_ENGINE_DATA).
    pub fn spawn(
        program: &str,
        args: &[String],
        env: &[(String, String)],
    ) -> std::io::Result<(EngineClient, Receiver<Message>)> {
        let mut cmd = Command::new(program);
        cmd.args(args)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::inherit());
        for (k, v) in env {
            cmd.env(k, v);
        }
        let mut child = cmd.spawn()?;

        let stdin = child.stdin.take().expect("piped stdin");
        let stdout = child.stdout.take().expect("piped stdout");

        let pending: Pending = Arc::new(Mutex::new(HashMap::new()));
        let (events_tx, events_rx) = channel::<Message>();

        let reader_pending = Arc::clone(&pending);
        let reader = std::thread::spawn(move || {
            let mut lines = BufReader::new(stdout).lines();
            while let Some(Ok(line)) = lines.next() {
                if line.trim().is_empty() {
                    continue;
                }
                match Message::parse(&line) {
                    Ok(Message::Response { id, ok, result, error }) => {
                        if let Some(tx) = reader_pending.lock().unwrap().remove(&id) {
                            let outcome = if ok {
                                Ok(result.unwrap_or(Value::Null))
                            } else {
                                Err(error.unwrap_or_else(|| RpcError {
                                    code: engine_ipc::error_code::INTERNAL.to_string(),
                                    message: "engine returned a failed response with no error".to_string(),
                                    retriable: false,
                                }))
                            };
                            let _ = tx.send(outcome);
                        }
                    }
                    Ok(msg @ Message::Event { .. }) => {
                        let _ = events_tx.send(msg);
                    }
                    // The host does not receive requests; ignore malformed lines
                    // rather than tearing down the connection.
                    Ok(Message::Request { .. }) | Err(_) => {}
                }
            }
            // Stream closed: fail every waiter so no request hangs forever.
            for (_, tx) in reader_pending.lock().unwrap().drain() {
                let _ = tx.send(Err(RpcError {
                    code: engine_ipc::error_code::INTERNAL.to_string(),
                    message: "engine daemon stream closed".to_string(),
                    retriable: false,
                }));
            }
        });

        Ok((
            EngineClient {
                child,
                stdin: Mutex::new(stdin),
                next_id: AtomicU64::new(1),
                pending,
                reader: Some(reader),
            },
            events_rx,
        ))
    }

    /// Send a request and block until the matching response (or timeout).
    pub fn request(
        &self,
        method: &str,
        params: Option<Value>,
        timeout: Duration,
    ) -> Result<Value, ClientError> {
        let id = self.next_id.fetch_add(1, Ordering::Relaxed);
        let (tx, rx) = channel();
        self.pending.lock().unwrap().insert(id, tx);

        let line = Message::request(id, method, params).encode();
        {
            let mut stdin = self.stdin.lock().unwrap();
            stdin
                .write_all(line.as_bytes())
                .and_then(|_| stdin.write_all(b"\n"))
                .and_then(|_| stdin.flush())
                .map_err(ClientError::Io)?;
        }

        match rx.recv_timeout(timeout) {
            Ok(Ok(value)) => Ok(value),
            Ok(Err(rpc)) => Err(ClientError::Rpc(rpc)),
            Err(RecvTimeoutError::Timeout) => {
                self.pending.lock().unwrap().remove(&id);
                Err(ClientError::Timeout)
            }
            Err(RecvTimeoutError::Disconnected) => Err(ClientError::Disconnected),
        }
    }

    /// Ask the daemon to stop, then wait for the process to exit.
    pub fn shutdown(&mut self) {
        let _ = self.request("shutdown", None, Duration::from_secs(5));
        let _ = self.child.wait();
    }
}

impl Drop for EngineClient {
    fn drop(&mut self) {
        // Best-effort: stop the child so we never orphan a daemon.
        let _ = self.request("shutdown", None, Duration::from_secs(2));
        let _ = self.child.kill();
        let _ = self.child.wait();
        if let Some(reader) = self.reader.take() {
            let _ = reader.join();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Locate the built daemon dll so we can drive the REAL process. Returns None
    /// (test skips) if it has not been built, so `cargo test` never fails just
    /// because the .NET side is absent; CI builds it first.
    fn daemon_dll() -> Option<String> {
        if let Ok(p) = std::env::var("ENGINE_DLL") {
            return Some(p);
        }
        let manifest = env!("CARGO_MANIFEST_DIR");
        for cfg in ["Debug", "Release"] {
            let p = format!(
                "{manifest}/../../../engine/src/OmniPacker.EngineHost/bin/{cfg}/net10.0/OmniPacker.EngineHost.dll"
            );
            if std::path::Path::new(&p).exists() {
                return Some(p);
            }
        }
        None
    }

    /// A throwaway data dir so the test never touches the real token store.
    fn temp_data() -> String {
        let dir = std::env::temp_dir().join(format!("op-engine-client-test-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        dir.to_string_lossy().into_owned()
    }

    #[test]
    fn drives_the_real_daemon_ping_hello_shutdown() {
        let Some(dll) = daemon_dll() else {
            eprintln!("skipping: engine daemon dll not built (set ENGINE_DLL or build engine/)");
            return;
        };
        let env = vec![("OMNIPACKER_ENGINE_DATA".to_string(), temp_data())];
        let (client, events) = EngineClient::spawn("dotnet", &[dll], &env).expect("spawn daemon");

        // First event should be `ready` with the protocol version.
        let ready = events
            .recv_timeout(Duration::from_secs(30))
            .expect("ready event");
        match ready {
            Message::Event { event, data } => {
                assert_eq!(event, "ready");
                assert_eq!(data.unwrap()["protocol"], engine_ipc::PROTOCOL_VERSION);
            }
            other => panic!("expected ready event, got {other:?}"),
        }

        // ping echoes the nonce.
        let pong = client
            .request("ping", Some(serde_json::json!({ "nonce": "rt" })), Duration::from_secs(10))
            .expect("ping");
        assert_eq!(pong["pong"], true);
        assert_eq!(pong["nonce"], "rt");

        // hello reports the contract version + capabilities.
        let hello = client.request("hello", None, Duration::from_secs(10)).expect("hello");
        assert_eq!(hello["protocol"], engine_ipc::PROTOCOL_VERSION);
        let caps: Vec<&str> = hello["capabilities"].as_array().unwrap()
            .iter().map(|v| v.as_str().unwrap()).collect();
        assert!(caps.contains(&"auth.status"));

        // unknown method -> structured error.
        let err = client.request("nope.nope", None, Duration::from_secs(10)).unwrap_err();
        match err {
            ClientError::Rpc(e) => assert_eq!(e.code, engine_ipc::error_code::UNKNOWN_METHOD),
            other => panic!("expected rpc error, got {other}"),
        }

        // auth.status works offline and reports Disconnected in a fresh data dir.
        let status = client.request("auth.status", None, Duration::from_secs(10)).expect("auth.status");
        assert_eq!(status["state"], "Disconnected");
    }
}
