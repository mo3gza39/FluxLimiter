//! Formatting helpers.
//!
//! The user's standing decision from the previous (C#) project: speeds
//! and limits are shown in **KB/s** (binary, 1 KB = 1024 bytes). Totals
//! use B/KB/MB/GB. A bits-per-second display can be enabled in settings.

pub fn format_bytes(n: u64) -> String {
    const UNITS: [&str; 6] = ["B", "KB", "MB", "GB", "TB", "PB"];
    let mut v = n as f64;
    let mut i = 0;
    while v >= 1024.0 && i < UNITS.len() - 1 {
        v /= 1024.0;
        i += 1;
    }
    if i == 0 {
        format!("{} {}", n, UNITS[i])
    } else if v < 10.0 {
        let s = format!("{v:.2}");
        let s = s.trim_end_matches('0').trim_end_matches('.');
        format!("{s} {}", UNITS[i])
    } else {
        format!("{v:.1} {}", UNITS[i])
    }
}

/// Format a byte rate as KB/s (default) or Mbps (when `bits` is set).
pub fn format_speed(bytes_per_sec: f64, bits: bool) -> String {
    if bits {
        let mut v = bytes_per_sec.max(0.0) * 8.0;
        const U: [&str; 4] = ["bps", "Kbps", "Mbps", "Gbps"];
        let mut i = 0;
        while v >= 1000.0 && i < U.len() - 1 {
            v /= 1000.0;
            i += 1;
        }
        format!("{v:.1} {}", U[i])
    } else {
        let kb = bytes_per_sec.max(0.0) / 1024.0;
        if kb < 1000.0 {
            format!("{kb:.1} KB/s")
        } else {
            format!("{:.2} MB/s", kb / 1024.0)
        }
    }
}

/// Parse a limit preset label. Limits are entered in KB/s (binary),
/// matching the previous project: `None` means unlimited.
pub fn parse_limit_kbps(text: &str) -> Option<u32> {
    let t = text.trim();
    if t.eq_ignore_ascii_case("unlimited") || t == "—" || t == "-" || t.is_empty() {
        return None;
    }
    let num: String = t.chars().take_while(|c| c.is_ascii_digit() || *c == '.').collect();
    let v: f64 = num.parse().ok()?;
    let lower = t.to_ascii_lowercase();
    let kbps = if lower.contains("mb") {
        v * 1024.0
    } else if lower.contains("gb") {
        v * 1024.0 * 1024.0
    } else {
        v // KB/s by default
    };
    Some(kbps.round() as u32)
}

pub const LIMIT_PRESETS_KBPS: [&str; 9] = [
    "Unlimited", "64 KB/s", "128 KB/s", "256 KB/s", "512 KB/s",
    "1 MB/s", "2 MB/s", "5 MB/s", "10 MB/s",
];

pub fn limit_label(limit_kbps: Option<u32>) -> String {
    match limit_kbps {
        None => "Unlimited".to_string(),
        Some(v) if v >= 1024 && v % 1024 == 0 => format!("{} MB/s", v / 1024),
        Some(v) => format!("{v} KB/s"),
    }
}
