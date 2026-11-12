//! Daily usage totals (device level), ported from the previous
//! project's `DailyUsageStore`: a JSON file with one entry per day,
//! flushed periodically. Device totals are labelled as device totals -
//! never presented as per-app numbers.

use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::path::PathBuf;

use crate::rules::user_data_dir;

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct DayUsage {
    pub date: String, // yyyy-mm-dd
    pub received: u64,
    pub sent: u64,
}

#[derive(Debug, Default, Serialize, Deserialize)]
struct DailyFile {
    days: BTreeMap<String, DayUsage>,
}

pub struct StatsStore {
    path: PathBuf,
    file: DailyFile,
}

fn today_string() -> String {
    // Days are local calendar days; the OS date is taken through the
    // system clock converted by the `date` of the host at runtime.
    // (Kept dependency-free: on Windows the service supplies local time,
    // here we use the UTC date as a fallback in the library tests.)
    let secs = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    civil_from_days((secs / 86_400) as i64)
}

/// Howard Hinnant's civil-from-days algorithm (public domain math).
fn civil_from_days(z: i64) -> String {
    let z = z + 719_468;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let doe = (z - era * 146_097) as u64;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe as i64 + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let y = if m <= 2 { y + 1 } else { y };
    format!("{y:04}-{m:02}-{d:02}")
}

impl StatsStore {
    pub fn load_default() -> Self {
        Self::load(user_data_dir().join("daily_usage.json"))
    }

    pub fn load(path: PathBuf) -> Self {
        let file = std::fs::read_to_string(&path)
            .ok()
            .and_then(|t| serde_json::from_str(&t).ok())
            .unwrap_or_default();
        Self { path, file }
    }

    pub fn add(&mut self, received_delta: u64, sent_delta: u64) {
        self.add_on(&today_string(), received_delta, sent_delta);
    }

    pub fn add_on(&mut self, date: &str, received_delta: u64, sent_delta: u64) {
        if received_delta == 0 && sent_delta == 0 {
            return;
        }
        let day = self.file.days.entry(date.to_string()).or_insert_with(|| DayUsage {
            date: date.to_string(),
            ..Default::default()
        });
        day.received += received_delta;
        day.sent += sent_delta;
    }

    pub fn flush(&self) {
        let tmp = self.path.with_extension("json.tmp");
        if let Ok(text) = serde_json::to_string_pretty(&self.file) {
            if std::fs::write(&tmp, text).is_ok() {
                let _ = std::fs::rename(&tmp, &self.path);
            }
        }
    }

    pub fn day(&self, date: &str) -> (u64, u64) {
        self.file
            .days
            .get(date)
            .map(|d| (d.received, d.sent))
            .unwrap_or((0, 0))
    }

    pub fn today(&self) -> (u64, u64) {
        self.day(&today_string())
    }

    /// Sum of all days whose date starts with `yyyy-mm`.
    pub fn month(&self, yyyy_mm: &str) -> (u64, u64) {
        let (mut r, mut s) = (0u64, 0u64);
        for (date, d) in &self.file.days {
            if date.starts_with(yyyy_mm) {
                r += d.received;
                s += d.sent;
            }
        }
        (r, s)
    }

    pub fn current_month(&self) -> (u64, u64) {
        let t = today_string();
        self.month(&t[..7])
    }

    /// The last `n` days ending today, oldest first: (date, received, sent).
    pub fn last_days(&self, n: usize) -> Vec<(String, u64, u64)> {
        let secs = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_secs())
            .unwrap_or(0);
        let today_days = (secs / 86_400) as i64;
        (0..n)
            .rev()
            .map(|i| {
                let date = civil_from_days(today_days - i as i64);
                let (r, s) = self.day(&date);
                (date, r, s)
            })
            .collect()
    }
}
