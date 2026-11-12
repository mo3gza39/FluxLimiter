//! FluxLimiter service — the headless half of the app.
//!
//! The GUI (`src/main.rs`) owns the dashboard; this service owns the
//! shared state and speaks protocol v1 (`src/protocol.rs`) so the GUI
//! — and later, the enforcement engine — can talk to one process that
//! holds the rules, the live snapshot and the daily stats.
//!
//! Transport is deliberately separate from dispatch: `Service` only
//! turns a `Request` into a `Response`. The binary (`src/bin/
//! fluxlimiter-service.rs`) owns the transport — a Windows named pipe
//! on Windows, length-prefixed frames on stdin/stdout elsewhere (so
//! the dispatch logic can be exercised on any platform).
//!
//! Honesty notes, carried from the C# audit:
//! * Limits are stored, NOT enforced — there is still no driver
//!   engine. `ping`/`getStatus` report `"enforcement": "none"`.
//! * `syncFirewall` is an honest stub: it returns an error until the
//!   Windows Firewall sync is implemented. It never claims success.
//! * A `blocked` rule for anything under `C:\Windows` (or the service
//!   / GUI executables themselves) is refused — the C# policy was
//!   "blocking Windows or the service itself is refused".

use crate::monitor::NetworkMonitor;
use crate::protocol::{
    decode_request, encode_frame, encode_request, FrameDecoder, Request, Response,
    PROTOCOL_VERSION,
};
use crate::rules::{Rule, RulesStore};
use crate::stats::StatsStore;
use serde_json::{json, Value};

pub struct Service {
    rules: RulesStore,
    monitor: NetworkMonitor,
    stats: StatsStore,
}

impl Service {
    pub fn new() -> Self {
        Self {
            rules: RulesStore::load_default(),
            monitor: NetworkMonitor::new(),
            stats: StatsStore::load_default(),
        }
    }

    /// Test / embedding constructor: caller supplies the stores.
    pub fn with_stores(rules: RulesStore, monitor: NetworkMonitor, stats: StatsStore) -> Self {
        Self { rules, monitor, stats }
    }

    pub fn handle(&mut self, req: &Request) -> Response {
        let err = |msg: &str| Response {
            id: req.id,
            ok: false,
            error: Some(msg.to_string()),
            body: Value::Null,
        };
        if req.version != PROTOCOL_VERSION {
            return err("unsupported protocol version");
        }
        match req.command.as_str() {
            "ping" => Response {
                id: req.id,
                ok: true,
                error: None,
                body: json!({
                    "service": "fluxlimiter-service",
                    "version": PROTOCOL_VERSION,
                    "enforcement": "none",
                }),
            },
            "getStatus" => {
                let snap = self.monitor.poll();
                let (today_down, today_up) = self.stats.today();
                Response {
                    id: req.id,
                    ok: true,
                    error: None,
                    body: json!({
                        "down_bps": snap.down_bps,
                        "up_bps": snap.up_bps,
                        "today_down_bytes": today_down,
                        "today_up_bytes": today_up,
                        "rules_count": self.rules.all().len(),
                        "enforcement": "none",
                        "version": PROTOCOL_VERSION,
                    }),
                }
            }
            "getSnapshot" => {
                let snap = self.monitor.poll();
                let apps: Vec<Value> = snap
                    .apps
                    .iter()
                    .map(|a| {
                        json!({
                            "pid": a.pid,
                            "name": a.name,
                            "exe_path": a.exe_path,
                            "connections": a.connections,
                        })
                    })
                    .collect();
                Response {
                    id: req.id,
                    ok: true,
                    error: None,
                    body: json!({
                        "down_bps": snap.down_bps,
                        "up_bps": snap.up_bps,
                        "apps": apps,
                    }),
                }
            }
            "getRules" => Response {
                id: req.id,
                ok: true,
                error: None,
                body: json!({ "rules": self.rules.all() }),
            },
            "setRule" => match serde_json::from_value::<Rule>(req.body.clone()) {
                Ok(rule) => match self.validate_rule(&rule) {
                    Some(msg) => err(&msg),
                    None => {
                        self.rules.set(rule.clone());
                        Response {
                            id: req.id,
                            ok: true,
                            error: None,
                            body: json!({ "rule": rule }),
                        }
                    }
                },
                Err(_) => err("setRule body must be a Rule object"),
            },
            "deleteRule" => {
                let exe = req
                    .body
                    .get("exe_path")
                    .and_then(Value::as_str)
                    .unwrap_or("")
                    .to_string();
                if exe.trim().is_empty() {
                    return err("deleteRule needs exe_path");
                }
                let existed = self
                    .rules
                    .all()
                    .iter()
                    .any(|r| r.exe_path.eq_ignore_ascii_case(&exe));
                self.rules.set(Rule {
                    exe_path: exe,
                    ..Default::default()
                });
                Response {
                    id: req.id,
                    ok: true,
                    error: None,
                    body: json!({ "deleted": existed }),
                }
            }
            "syncFirewall" => err(
                "syncFirewall is not implemented yet (Windows Firewall sync is a later step)",
            ),
            other => err(&format!("unknown command: {other}")),
        }
    }

    /// Refuse rules that must never be set. Returns the error message,
    /// or `None` when the rule is acceptable.
    fn validate_rule(&self, rule: &Rule) -> Option<String> {
        if rule.exe_path.trim().is_empty() {
            return Some("exe_path must not be empty".into());
        }
        for limit in [rule.limit_down_kbps, rule.limit_up_kbps] {
            if limit == Some(0) {
                return Some("a limit of 0 KB/s is not allowed; use null for unlimited".into());
            }
        }
        if rule.blocked {
            let lower = rule.exe_path.to_ascii_lowercase();
            if lower.starts_with(r"c:\windows")
                || lower.ends_with("fluxlimiter.exe")
                || lower.ends_with("fluxlimiter-service.exe")
            {
                return Some(
                    "refusing to block a Windows system path or FluxLimiter itself".into(),
                );
            }
        }
        None
    }

    /// Handle one length-prefixed frame (as produced by
    /// `protocol::encode_request`); returns the encoded response frame,
    /// or `None` when the bytes hold no complete/decodable request.
    pub fn handle_frame(&mut self, bytes: &[u8]) -> Option<Vec<u8>> {
        let mut dec = FrameDecoder::new();
        let frames = dec.feed(bytes).ok()?;
        let payload = frames.into_iter().next()?;
        let req = decode_request(&payload)?;
        let resp = self.handle(&req);
        Some(encode_frame(
            &serde_json::to_string(&resp).unwrap_or_default(),
        ))
    }
}

impl Default for Service {
    fn default() -> Self {
        Self::new()
    }
}

/// Convenience used by the binary and by tests: encode a request,
/// dispatch it through a fresh `Service`, decode the response.
pub fn roundtrip(service: &mut Service, req: &Request) -> Option<Response> {
    let bytes = encode_request(req);
    let out = service.handle_frame(&bytes)?;
    let mut dec = FrameDecoder::new();
    let frames = dec.feed(&out).ok()?;
    let payload = frames.into_iter().next()?;
    crate::protocol::decode_response(&payload)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::protocol::Request;

    fn test_service(_name: &str) -> Service {
        // A fresh dir per test: the tests run in parallel in one
        // process, so a shared dir would leak rules between them.
        static COUNTER: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
        let n = COUNTER.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        let dir = std::env::temp_dir()
            .join(format!("flux-service-test-{}-{n}", std::process::id()));
        let _ = std::fs::create_dir_all(&dir);
        let rules = RulesStore::load(dir.join("rules.json"));
        let stats = StatsStore::load(dir.join("stats.json"));
        Service::with_stores(rules, NetworkMonitor::new(), stats)
    }

    fn req(id: u64, command: &str, body: Value) -> Request {
        Request { version: 1, id, command: command.into(), body }
    }

    #[test]
    fn ping_reports_no_enforcement() {
        let mut svc = test_service("t");
        let resp = svc.handle(&req(1, "ping", json!({})));
        assert!(resp.ok);
        assert_eq!(resp.body["service"], "fluxlimiter-service");
        assert_eq!(resp.body["enforcement"], "none");
    }

    #[test]
    fn wrong_version_is_rejected() {
        let mut svc = test_service("t");
        let bad = Request { version: 99, id: 2, command: "ping".into(), body: json!({}) };
        let resp = svc.handle(&bad);
        assert!(!resp.ok);
    }

    #[test]
    fn set_get_delete_rule_roundtrip() {
        let mut svc = test_service("t");
        let rule = json!({
            "exe_path": "C:\\Apps\\chrome.exe",
            "name": "Google Chrome",
            "limit_down_kbps": 512,
            "limit_up_kbps": null,
            "blocked": false,
        });
        let set = svc.handle(&req(3, "setRule", rule));
        assert!(set.ok, "{:?}", set.error);
        let rules = svc.handle(&req(4, "getRules", json!({})));
        assert_eq!(rules.body["rules"].as_array().unwrap().len(), 1);
        assert_eq!(rules.body["rules"][0]["limit_down_kbps"], 512);
        let del = svc.handle(&req(5, "deleteRule", json!({ "exe_path": "C:\\Apps\\chrome.exe" })));
        assert!(del.ok);
        assert_eq!(del.body["deleted"], true);
        let rules = svc.handle(&req(6, "getRules", json!({})));
        assert!(rules.body["rules"].as_array().unwrap().is_empty());
    }

    #[test]
    fn blocking_windows_or_self_is_refused() {
        let mut svc = test_service("t");
        let rule = json!({
            "exe_path": "C:\\Windows\\System32\\svchost.exe",
            "name": "svchost",
            "limit_down_kbps": null,
            "limit_up_kbps": null,
            "blocked": true,
        });
        let resp = svc.handle(&req(7, "setRule", rule));
        assert!(!resp.ok);
        // ...but a plain limit on the same path is fine
        let rule = json!({
            "exe_path": "C:\\Windows\\System32\\svchost.exe",
            "name": "svchost",
            "limit_down_kbps": 256,
            "limit_up_kbps": null,
            "blocked": false,
        });
        assert!(svc.handle(&req(8, "setRule", rule)).ok);
    }

    #[test]
    fn zero_limit_and_unknown_command_are_rejected() {
        let mut svc = test_service("t");
        let rule = json!({
            "exe_path": "C:\\Apps\\x.exe", "name": "x",
            "limit_down_kbps": 0, "limit_up_kbps": null, "blocked": false,
        });
        assert!(!svc.handle(&req(9, "setRule", rule)).ok);
        assert!(!svc.handle(&req(10, "explode", json!({}))).ok);
        // syncFirewall is an honest stub, not a fake success
        let resp = svc.handle(&req(11, "syncFirewall", json!({})));
        assert!(!resp.ok);
    }

    #[test]
    fn frame_roundtrip_through_service() {
        let mut svc = test_service("t");
        let resp = roundtrip(&mut svc, &req(12, "ping", json!({}))).expect("response");
        assert!(resp.ok);
        assert_eq!(resp.id, 12);
    }

    #[test]
    fn snapshot_shape_is_honest() {
        let mut svc = test_service("t");
        let resp = svc.handle(&req(13, "getSnapshot", json!({})));
        assert!(resp.ok);
        assert!(resp.body["down_bps"].is_number());
        assert!(resp.body["apps"].is_array());
    }
}
