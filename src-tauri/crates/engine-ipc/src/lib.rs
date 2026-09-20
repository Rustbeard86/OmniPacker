//! OmniPacker engine IPC protocol (host side).
//!
//! Mirrors the frozen v1 envelope in `docs/IPC-CONTRACT.md` that the C# engine
//! daemon (`engine/src/OmniPacker.EngineHost`) implements. This crate has no
//! Tauri dependency so the cross-language contract tests run without a webview
//! toolchain. Both this crate and the C# `Codec` are tested against the same
//! golden fixtures in `contracts/fixtures/`.

use serde::{Deserialize, Serialize};
use serde_json::Value;

/// Envelope protocol version. Bumped only for a breaking envelope change.
pub const PROTOCOL_VERSION: u32 = 1;

/// A single IPC message. Serialized as one newline-free JSON line.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "t")]
pub enum Message {
    /// Host -> engine call.
    #[serde(rename = "req")]
    Request {
        id: u64,
        method: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        params: Option<Value>,
    },
    /// Engine -> host reply (one per request id).
    #[serde(rename = "res")]
    Response {
        id: u64,
        ok: bool,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        result: Option<Value>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        error: Option<RpcError>,
    },
    /// Engine -> host unsolicited event (no id).
    #[serde(rename = "evt")]
    Event {
        event: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        data: Option<Value>,
    },
}

/// Structured error payload for a failed response.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct RpcError {
    pub code: String,
    pub message: String,
    pub retriable: bool,
}

/// Stable error codes (see docs/IPC-CONTRACT.md).
pub mod error_code {
    pub const BAD_REQUEST: &str = "bad_request";
    pub const UNKNOWN_METHOD: &str = "unknown_method";
    pub const UNAUTHENTICATED: &str = "unauthenticated";
    pub const NO_OWNING_ACCOUNT: &str = "no_owning_account";
    pub const DEPOT_ACCESS_DENIED: &str = "depot_access_denied";
    pub const TRANSIENT: &str = "transient";
    pub const CANCELLED: &str = "cancelled";
    pub const INTERNAL: &str = "internal";
}

impl Message {
    /// Build a request. `params` is any JSON value (typically an object) or None.
    pub fn request(id: u64, method: impl Into<String>, params: Option<Value>) -> Self {
        Message::Request { id, method: method.into(), params }
    }

    /// Build an event.
    pub fn event(event: impl Into<String>, data: Option<Value>) -> Self {
        Message::Event { event: event.into(), data }
    }

    /// Parse one JSON line into a message.
    pub fn parse(line: &str) -> Result<Message, serde_json::Error> {
        serde_json::from_str(line)
    }

    /// Encode to a single newline-free JSON line.
    pub fn encode(&self) -> String {
        // Serialization of these types cannot fail.
        serde_json::to_string(self).expect("message serialization is infallible")
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fixture(name: &str) -> String {
        let path = format!("{}/../../../contracts/fixtures/{}", env!("CARGO_MANIFEST_DIR"), name);
        std::fs::read_to_string(&path)
            .unwrap_or_else(|e| panic!("read fixture {path}: {e}"))
            .trim()
            .to_string()
    }

    #[test]
    fn parses_ping_request_fixture() {
        match Message::parse(&fixture("req_ping.json")).unwrap() {
            Message::Request { id, method, params } => {
                assert_eq!(id, 1);
                assert_eq!(method, "ping");
                assert_eq!(params.unwrap()["nonce"], "abc123");
            }
            other => panic!("expected request, got {other:?}"),
        }
    }

    #[test]
    fn parses_hello_request_with_null_params() {
        match Message::parse(&fixture("req_hello.json")).unwrap() {
            Message::Request { method, params, .. } => {
                assert_eq!(method, "hello");
                assert!(params.is_none(), "explicit null params should deserialize to None");
            }
            other => panic!("expected request, got {other:?}"),
        }
    }

    #[test]
    fn parses_ok_response_fixture() {
        match Message::parse(&fixture("res_ping_ok.json")).unwrap() {
            Message::Response { id, ok, result, error } => {
                assert_eq!(id, 1);
                assert!(ok);
                assert!(error.is_none());
                let r = result.unwrap();
                assert_eq!(r["pong"], true);
                assert_eq!(r["nonce"], "abc123");
            }
            other => panic!("expected response, got {other:?}"),
        }
    }

    #[test]
    fn parses_error_response_fixture() {
        match Message::parse(&fixture("res_error.json")).unwrap() {
            Message::Response { ok, error, .. } => {
                assert!(!ok);
                let e = error.unwrap();
                assert_eq!(e.code, error_code::UNKNOWN_METHOD);
                assert!(!e.retriable);
            }
            other => panic!("expected response, got {other:?}"),
        }
    }

    #[test]
    fn parses_transient_error_as_retriable() {
        match Message::parse(&fixture("res_error_transient.json")).unwrap() {
            Message::Response { error, .. } => {
                let e = error.unwrap();
                assert_eq!(e.code, error_code::TRANSIENT);
                assert!(e.retriable);
            }
            other => panic!("expected response, got {other:?}"),
        }
    }

    #[test]
    fn parses_ready_event_fixture() {
        match Message::parse(&fixture("evt_ready.json")).unwrap() {
            Message::Event { event, data } => {
                assert_eq!(event, "ready");
                let d = data.unwrap();
                assert_eq!(d["protocol"], PROTOCOL_VERSION);
                let caps: Vec<&str> = d["capabilities"].as_array().unwrap()
                    .iter().map(|v| v.as_str().unwrap()).collect();
                assert!(caps.contains(&"ping"));
                assert!(caps.contains(&"hello"));
            }
            other => panic!("expected event, got {other:?}"),
        }
    }

    #[test]
    fn parses_log_event_fixture() {
        match Message::parse(&fixture("evt_log.json")).unwrap() {
            Message::Event { event, data } => {
                assert_eq!(event, "log");
                let d = data.unwrap();
                assert_eq!(d["level"], "info");
                assert_eq!(d["source"], "engine");
            }
            other => panic!("expected event, got {other:?}"),
        }
    }

    #[test]
    fn parses_auth_status_response_fixture() {
        match Message::parse(&fixture("res_auth_status.json")).unwrap() {
            Message::Response { ok, result, .. } => {
                assert!(ok);
                let r = result.unwrap();
                assert_eq!(r["state"], "LoggedOn");
                assert_eq!(r["accountName"], "archiver");
            }
            other => panic!("expected response, got {other:?}"),
        }
    }

    #[test]
    fn parses_auth_status_event_fixture() {
        match Message::parse(&fixture("evt_auth_status.json")).unwrap() {
            Message::Event { event, data } => {
                assert_eq!(event, "auth.status");
                assert_eq!(data.unwrap()["state"], "Disconnected");
            }
            other => panic!("expected event, got {other:?}"),
        }
    }

    #[test]
    fn parses_account_list_response_fixture() {
        match Message::parse(&fixture("res_account_list.json")).unwrap() {
            Message::Response { ok, result, .. } => {
                assert!(ok);
                let accounts = result.unwrap()["accounts"].as_array().unwrap().clone();
                assert_eq!(accounts.len(), 1);
                assert_eq!(accounts[0]["account"], "archiver");
            }
            other => panic!("expected response, got {other:?}"),
        }
    }

    #[test]
    fn roundtrips_a_constructed_response() {
        let original = Message::Response {
            id: 42,
            ok: true,
            result: Some(serde_json::json!({ "pong": true, "nonce": "z" })),
            error: None,
        };
        let reparsed = Message::parse(&original.encode()).unwrap();
        assert_eq!(original, reparsed);
    }

    #[test]
    fn encoded_message_has_no_embedded_newline() {
        let msg = Message::event("log", Some(serde_json::json!({ "message": "line one\nline two" })));
        assert!(!msg.encode().contains('\n'));
    }

    #[test]
    fn rejects_malformed_lines() {
        for bad in [
            "not json at all",
            "[]",
            "{\"id\":1,\"method\":\"ping\"}",     // missing t
            "{\"t\":\"bogus\"}",                   // unknown discriminator
            "{\"t\":\"req\",\"id\":1}",            // missing method
            "{\"t\":\"req\",\"method\":\"ping\"}", // missing id
        ] {
            assert!(Message::parse(bad).is_err(), "should reject: {bad}");
        }
    }
}
