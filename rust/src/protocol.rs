//! Service control protocol v1 - carried over from the previous
//! project's specification (it was well designed, language-neutral):
//!
//! * Transport: named pipe `\\.\pipe\FluxLimiter.Control`, local only.
//! * Framing: `[i32 little-endian length][UTF-8 JSON]`, max 1 MiB.
//! * Request:  `{ "version": 1, "id": 42, "command": "getSnapshot", "body": { ... } }`
//! * Response: `{ "id": 42, "ok": true, "error": null, "body": { ... } }`
//! * Commands: ping, getStatus, getSnapshot, getRules, setRule,
//!   deleteRule, syncFirewall.
//! * A limit of `null` means unlimited; limits are stored but NOT
//!   enforced until the driver engine exists; only `blocked` is
//!   applied today (Windows Firewall, owned by the service).
//! * Pipe security requirements for the Rust service: deny network SID,
//!   first-instance ownership, per-request timeout, admin check for
//!   mutating commands, and error replies must not leak exception text.

use serde::{Deserialize, Serialize};
use serde_json::Value;

pub const PIPE_NAME: &str = r"\\.\pipe\FluxLimiter.Control";
pub const MAX_FRAME_BYTES: usize = 1 << 20; // 1 MiB
pub const PROTOCOL_VERSION: u32 = 1;

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct Request {
    pub version: u32,
    pub id: u64,
    pub command: String,
    #[serde(default)]
    pub body: Value,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct Response {
    pub id: u64,
    pub ok: bool,
    pub error: Option<String>,
    #[serde(default)]
    pub body: Value,
}

/// Encode one frame: little-endian i32 length prefix + JSON payload.
pub fn encode_frame(json: &str) -> Vec<u8> {
    let payload = json.as_bytes();
    let mut out = Vec::with_capacity(4 + payload.len());
    out.extend_from_slice(&(payload.len() as i32).to_le_bytes());
    out.extend_from_slice(payload);
    out
}

/// Incremental frame decoder: feed bytes, get back complete payloads.
#[derive(Debug, Default)]
pub struct FrameDecoder {
    buf: Vec<u8>,
}

impl FrameDecoder {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn feed(&mut self, data: &[u8]) -> Result<Vec<Vec<u8>>, ProtocolError> {
        self.buf.extend_from_slice(data);
        let mut frames = Vec::new();
        loop {
            if self.buf.len() < 4 {
                break;
            }
            let len = i32::from_le_bytes(self.buf[0..4].try_into().unwrap());
            if len < 0 || len as usize > MAX_FRAME_BYTES {
                return Err(ProtocolError::FrameTooLarge(len));
            }
            let len = len as usize;
            if self.buf.len() < 4 + len {
                break;
            }
            frames.push(self.buf[4..4 + len].to_vec());
            self.buf.drain(..4 + len);
        }
        Ok(frames)
    }
}

#[derive(Debug, PartialEq)]
pub enum ProtocolError {
    FrameTooLarge(i32),
}

impl std::fmt::Display for ProtocolError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ProtocolError::FrameTooLarge(n) => write!(f, "invalid frame length {n}"),
        }
    }
}
impl std::error::Error for ProtocolError {}

pub fn encode_request(req: &Request) -> Vec<u8> {
    encode_frame(&serde_json::to_string(req).unwrap_or_default())
}

pub fn decode_request(payload: &[u8]) -> Option<Request> {
    serde_json::from_slice(payload).ok()
}

pub fn decode_response(payload: &[u8]) -> Option<Response> {
    serde_json::from_slice(payload).ok()
}
