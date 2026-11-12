//! Shared per-application rate limiter - ported semantics from the
//! previous project's `SharedRateLimiter` / `TrafficPacer` (the user's
//! own code): ONE shared limit per application (executable path) across
//! all its PIDs and connections, with independent download and upload
//! token buckets. Rates are binary KB/s (1 KB = 1024 bytes).
//!
//! This is the pacing core only: it does not intercept OS traffic. The
//! WinDivert packet loop (phase 2) will ask `schedule()` / `try_consume()`
//! before reinjecting each packet.

use std::collections::HashMap;

#[derive(Debug, Clone)]
struct Bucket {
    tokens: f64,
    rate: f64, // bytes per second
    updated_at: f64,
}

impl Bucket {
    fn new(rate: f64, now: f64) -> Self {
        Self { tokens: rate, rate, updated_at: now }
    }
    fn refill(&mut self, now: f64) {
        let now = now.max(self.updated_at);
        // capacity == one second of the (current) rate
        self.tokens = (self.tokens + (now - self.updated_at) * self.rate).min(self.rate);
        self.updated_at = now;
    }
}

#[derive(Debug, Default)]
pub struct SharedRateLimiter {
    buckets: HashMap<String, Bucket>,
}

pub fn direction_key(scope: &str, download: bool) -> String {
    format!("{}:{}", if download { "down" } else { "up" }, scope.trim())
}

impl SharedRateLimiter {
    pub fn new() -> Self {
        Self::default()
    }

    fn bucket(&mut self, key: &str, limit_kbps: u32, now: f64) -> &mut Bucket {
        let rate = limit_kbps as f64 * 1024.0;
        let b = self
            .buckets
            .entry(key.to_string())
            .or_insert_with(|| Bucket::new(rate, now));
        if b.rate != rate {
            b.rate = rate;
            b.tokens = b.tokens.min(rate);
        }
        b
    }

    /// Try to spend `bytes` now; true if the bucket allows it.
    pub fn try_consume(&mut self, scope: &str, download: bool, limit_kbps: u32,
                       bytes: usize, now: f64) -> bool {
        if limit_kbps == 0 {
            return false;
        }
        let key = direction_key(scope, download);
        let b = self.bucket(&key, limit_kbps, now);
        b.refill(now);
        if bytes as f64 > b.tokens {
            return false;
        }
        b.tokens -= bytes as f64;
        true
    }

    /// Earliest monotonic time (seconds) at which `bytes` may be sent -
    /// the TrafficPacer.Schedule semantics, for the packet loop's queue.
    pub fn schedule(&mut self, scope: &str, download: bool, limit_kbps: u32,
                    bytes: usize, now: f64) -> f64 {
        let rate = limit_kbps as f64 * 1024.0;
        let key = direction_key(scope, download);
        let b = self.bucket(&key, limit_kbps, now);
        b.refill(now);
        b.tokens -= bytes as f64;
        if b.tokens >= 0.0 {
            b.updated_at
        } else {
            b.updated_at + (-b.tokens / rate)
        }
    }

    pub fn clear(&mut self, scope: &str) {
        self.buckets.remove(&direction_key(scope, true));
        self.buckets.remove(&direction_key(scope, false));
    }
}
