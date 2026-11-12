//! Windows: processes owning TCP/UDP connections, via the IP Helper
//! extended tables (the same source NetLimiter-class tools use for the
//! connection -> PID map). Byte counts per connection are NOT available
//! here; per-app accounting is ETW's job.

use crate::monitor::AppTraffic;
use std::collections::HashMap;
use sysinfo::{ProcessesToUpdate, System};
use windows::Win32::Foundation::ERROR_INSUFFICIENT_BUFFER;
use windows::Win32::NetworkManagement::IpHelper::{
    GetExtendedTcpTable, GetExtendedUdpTable, MIB_TCPROW_OWNER_PID, MIB_UDPROW_OWNER_PID,
    TCP_TABLE_OWNER_PID_ALL, UDP_TABLE_OWNER_PID,
};
use windows::Win32::Networking::WinSock::AF_INET;

fn table_bytes(kind_tcp: bool) -> Option<Vec<u8>> {
    let mut size: u32 = 0;
    // First call asks for the required buffer size.
    let rc = unsafe {
        if kind_tcp {
            GetExtendedTcpTable(None, &mut size, false, AF_INET.0 as u32,
                                TCP_TABLE_OWNER_PID_ALL, 0)
        } else {
            GetExtendedUdpTable(None, &mut size, false, AF_INET.0 as u32,
                                UDP_TABLE_OWNER_PID, 0)
        }
    };
    if rc != ERROR_INSUFFICIENT_BUFFER.0 && size == 0 {
        return None;
    }
    let mut buf = vec![0u8; size as usize];
    let rc = unsafe {
        if kind_tcp {
            GetExtendedTcpTable(Some(buf.as_mut_ptr() as *mut _), &mut size, false,
                                AF_INET.0 as u32, TCP_TABLE_OWNER_PID_ALL, 0)
        } else {
            GetExtendedUdpTable(Some(buf.as_mut_ptr() as *mut _), &mut size, false,
                                AF_INET.0 as u32, UDP_TABLE_OWNER_PID, 0)
        }
    };
    if rc != 0 {
        return None;
    }
    Some(buf)
}

fn count_connections() -> HashMap<u32, u32> {
    let mut counts: HashMap<u32, u32> = HashMap::new();
    if let Some(buf) = table_bytes(true) {
        if buf.len() >= 4 {
            let n = u32::from_ne_bytes(buf[0..4].try_into().unwrap()) as usize;
            let stride = std::mem::size_of::<MIB_TCPROW_OWNER_PID>();
            for i in 0..n {
                let off = 4 + i * stride;
                if off + stride > buf.len() {
                    break;
                }
                let row = unsafe {
                    std::ptr::read_unaligned(buf.as_ptr().add(off) as *const MIB_TCPROW_OWNER_PID)
                };
                // skip CLOSED (1) and TIME_WAIT (11)
                if row.dwState != 1 && row.dwState != 11 && row.dwOwningPid != 0 {
                    *counts.entry(row.dwOwningPid).or_default() += 1;
                }
            }
        }
    }
    if let Some(buf) = table_bytes(false) {
        if buf.len() >= 4 {
            let n = u32::from_ne_bytes(buf[0..4].try_into().unwrap()) as usize;
            let stride = std::mem::size_of::<MIB_UDPROW_OWNER_PID>();
            for i in 0..n {
                let off = 4 + i * stride;
                if off + stride > buf.len() {
                    break;
                }
                let row = unsafe {
                    std::ptr::read_unaligned(buf.as_ptr().add(off) as *const MIB_UDPROW_OWNER_PID)
                };
                if row.dwOwningPid != 0 {
                    *counts.entry(row.dwOwningPid).or_default() += 1;
                }
            }
        }
    }
    counts
}

pub fn active_apps() -> Vec<AppTraffic> {
    let counts = count_connections();
    if counts.is_empty() {
        return Vec::new();
    }
    let mut sys = System::new();
    sys.refresh_processes(ProcessesToUpdate::All, true);
    let mut apps: Vec<AppTraffic> = counts
        .into_iter()
        .map(|(pid, connections)| {
            let (name, exe_path) = sys
                .process(sysinfo::Pid::from_u32(pid))
                .map(|p| {
                    (
                        p.name().to_string_lossy().into_owned(),
                        p.exe().map(|e| e.to_string_lossy().into_owned()).unwrap_or_default(),
                    )
                })
                .unwrap_or_else(|| (format!("PID {pid}"), String::new()));
            AppTraffic { pid, name, exe_path, connections }
        })
        .collect();
    // Most connections first; name as a stable tiebreak so rows
    // do not swap places every second when counts are equal.
    apps.sort_by(|a, b| {
        b.connections
            .cmp(&a.connections)
            .then_with(|| a.name.to_lowercase().cmp(&b.name.to_lowercase()))
    });
    apps
}
