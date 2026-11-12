//! Core tests - the Rust counterparts of the previous project's
//! CoreTests / IpcTests semantics.

use fluxlimiter::format::{format_bytes, format_speed, limit_label, parse_limit_kbps};
use fluxlimiter::protocol::{decode_request, encode_frame, encode_request, FrameDecoder, Request};
use fluxlimiter::rate_limiter::SharedRateLimiter;
use fluxlimiter::rules::{Rule, RulesStore};
use fluxlimiter::stats::StatsStore;

#[test]
fn formatting_and_units() {
    assert_eq!(format_bytes(0), "0 B");
    assert_eq!(format_bytes(1536), "1.5 KB");
    assert_eq!(format_bytes(3_500_000_000), "3.26 GB");
    // default display unit is KB/s (binary), per the user's decision
    assert_eq!(format_speed(102_400.0, false), "100.0 KB/s");
    assert_eq!(format_speed(1_572_864.0, false), "1.50 MB/s");
    assert!(format_speed(1_550_000.0, true).ends_with("Mbps"));
    assert_eq!(parse_limit_kbps("Unlimited"), None);
    assert_eq!(parse_limit_kbps("512 KB/s"), Some(512));
    assert_eq!(parse_limit_kbps("2 MB/s"), Some(2048));
    assert_eq!(limit_label(Some(512)), "512 KB/s");
    assert_eq!(limit_label(Some(2048)), "2 MB/s");
    assert_eq!(limit_label(None), "Unlimited");
}

#[test]
fn token_bucket_shared_identity_and_directions() {
    let mut lim = SharedRateLimiter::new();
    // 100 KB/s bucket starts full (102_400 bytes)
    assert!(lim.try_consume("C:\\Apps\\chrome.exe", true, 100, 60_000, 0.0));
    // second PID of the same app shares the same bucket (same identity)
    assert!(!lim.try_consume("C:\\Apps\\chrome.exe", true, 100, 60_000, 0.0));
    // upload direction has its own bucket
    assert!(lim.try_consume("C:\\Apps\\chrome.exe", false, 100, 60_000, 0.0));
    // after 1 s the download bucket has refilled
    assert!(lim.try_consume("C:\\Apps\\chrome.exe", true, 100, 60_000, 1.0));
}

#[test]
fn pacer_schedules_beyond_capacity() {
    let mut lim = SharedRateLimiter::new();
    // schedule 150_000 bytes at 100 KB/s: the part beyond the full
    // bucket waits (150000-102400)/102400 = ~0.465 s
    let t = lim.schedule("app", true, 100, 150_000, 0.0);
    assert!(t > 0.40 && t < 0.53, "scheduled at {t}");
}

#[test]
fn protocol_framing_roundtrip() {
    let req = Request { version: 1, id: 42, command: "getSnapshot".into(), body: serde_json::json!({}) };
    let bytes = encode_request(&req);
    let mut dec = FrameDecoder::new();
    // feed in two chunks - the decoder must reassemble
    let half = bytes.len() / 2;
    assert!(dec.feed(&bytes[..half]).unwrap().is_empty());
    let frames = dec.feed(&bytes[half..]).unwrap();
    assert_eq!(frames.len(), 1);
    assert_eq!(decode_request(&frames[0]), Some(req));
    // raw encode_frame matches the v1 framing
    let f = encode_frame("\"pong\"");
    assert_eq!(&f[0..4], &6i32.to_le_bytes());
}

#[test]
fn rules_store_roundtrip_and_delete_on_empty() {
    let dir = std::env::temp_dir().join(format!("flux-test-{}", std::process::id()));
    let _ = std::fs::create_dir_all(&dir);
    let path = dir.join("rules.json");
    let mut store = RulesStore::load(path.clone());
    let mut rule = Rule { exe_path: "C:\\Apps\\chrome.exe".into(), name: "Google Chrome".into(), ..Default::default() };
    rule.limit_down_kbps = Some(512);
    store.set(rule.clone());
    let store2 = RulesStore::load(path.clone());
    assert_eq!(store2.get_or_default("c:\\apps\\CHROME.exe", "x").limit_down_kbps, Some(512));
    // setting an empty rule removes it
    let mut store3 = RulesStore::load(path.clone());
    store3.set(Rule { exe_path: rule.exe_path.clone(), name: rule.name.clone(), ..Default::default() });
    assert!(store3.all().is_empty());
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn stats_store_days_and_month() {
    let dir = std::env::temp_dir().join(format!("flux-test-stats-{}", std::process::id()));
    let _ = std::fs::create_dir_all(&dir);
    let path = dir.join("daily.json");
    let mut s = StatsStore::load(path.clone());
    s.add_on("2026-10-05", 1000, 500);
    s.add_on("2026-10-05", 2000, 500);
    s.add_on("2026-10-20", 7000, 3000);
    s.add_on("2026-09-30", 999, 999);
    assert_eq!(s.day("2026-10-05"), (3000, 1000));
    assert_eq!(s.month("2026-10"), (10_000, 4_000));
    s.flush();
    let s2 = StatsStore::load(path);
    assert_eq!(s2.month("2026-10"), (10_000, 4_000));
    let _ = std::fs::remove_dir_all(&dir);
}
