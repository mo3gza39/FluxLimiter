//! Network monitor.
//!
//! * Total speeds: OS interface counters (sysinfo's Networks, which on
//!   Windows reads the IP Helper statistics). Rates are computed on the
//!   monitor's own 1-second tick - a lesson carried from the previous
//!   project, where rates measured inside the 5 s grid reload jumped.
//! * Active applications: processes owning TCP/UDP connections. On
//!   Windows this comes from GetExtendedTcpTable / GetExtendedUdpTable
//!   (see `win_tables`, phase 1b); elsewhere the list is honestly empty
//!   rather than filled with invented rows.
//! * Per-app byte accounting is the ETW collector's job (measurement
//!   only), ported separately; its numbers stay hidden until validated
//!   against a known-size transfer, as in the previous project.

use std::time::Instant;
use sysinfo::Networks;

#[derive(Debug, Clone, Default)]
pub struct AppTraffic {
    pub pid: u32,
    pub name: String,
    pub exe_path: String,
    pub connections: u32,
}

#[derive(Debug, Clone, Default)]
pub struct Snapshot {
    pub down_bps: f64,
    pub up_bps: f64,
    pub down_delta: u64,
    pub up_delta: u64,
    pub apps: Vec<AppTraffic>,
}

pub struct NetworkMonitor {
    networks: Networks,
    last_recv: u64,
    last_sent: u64,
    last_time: Instant,
    pub down_history: Vec<f64>,
    pub up_history: Vec<f64>,
}

impl Default for NetworkMonitor {
    fn default() -> Self {
        Self::new()
    }
}

impl NetworkMonitor {
    pub fn new() -> Self {
        let networks = Networks::new_with_refreshed_list();
        let (r, s) = totals(&networks);
        Self {
            networks,
            last_recv: r,
            last_sent: s,
            last_time: Instant::now(),
            down_history: Vec::new(),
            up_history: Vec::new(),
        }
    }

    pub fn poll(&mut self) -> Snapshot {
        self.networks.refresh();
        let (recv, sent) = totals(&self.networks);
        let dt = self.last_time.elapsed().as_secs_f64().max(0.001);
        let d_recv = recv.saturating_sub(self.last_recv);
        let d_sent = sent.saturating_sub(self.last_sent);
        self.last_recv = recv;
        self.last_sent = sent;
        self.last_time = Instant::now();

        let snap = Snapshot {
            down_bps: d_recv as f64 / dt,
            up_bps: d_sent as f64 / dt,
            down_delta: d_recv,
            up_delta: d_sent,
            apps: active_apps(),
        };
        self.down_history.push(snap.down_bps);
        self.up_history.push(snap.up_bps);
        if self.down_history.len() > 60 {
            self.down_history.remove(0);
            self.up_history.remove(0);
        }
        snap
    }
}

fn totals(networks: &Networks) -> (u64, u64) {
    networks
        .values()
        .fold((0u64, 0u64), |(r, s), n| (r + n.total_received(), s + n.total_transmitted()))
}

/// Processes with active network connections.
#[cfg(windows)]
fn active_apps() -> Vec<AppTraffic> {
    crate::win_tables::active_apps()
}

#[cfg(not(windows))]
fn active_apps() -> Vec<AppTraffic> {
    Vec::new()
}
