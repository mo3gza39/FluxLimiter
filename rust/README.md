# FluxLimiter — Rust edition

The all-Rust rebuild of FluxLimiter (a NetLimiter-like traffic monitor
and limiter for Windows). It supersedes two earlier codebases, whose
useful work was audited before a line of Rust was written:

* the **C# / .NET 8 WinForms project** — the user's repo
  <https://github.com/mo3gza39/FluxLimiter> (main @ `02965cf`, Windows
  CI green) and its `FluxLimiter-FINAL` zip snapshot (Round 5);
* the **Python + PySide6 prototype** built in chat — now only a design
  reference.

## What was carried over from the C# project (and what was not)

Carried over (designs, semantics and verified logic — reimplemented in Rust):

| From C# | Rust module | Notes |
|---|---|---|
| `SharedRateLimiter` + `TrafficPacer` | `src/rate_limiter.rs` | One shared token bucket per app (exe path) across all PIDs, separate down/up buckets, binary KB/s. Tests ported too. |
| Service pipe protocol v1 | `src/protocol.rs` | Length-prefixed JSON framing, same commands and security requirements (deny-network ACL, first-instance pipe, timeout, admin check for mutations, no leaked exception text). |
| `DailyUsageStore` | `src/stats.rs` | Device-level daily totals in JSON, atomic writes. Labelled device totals, never per-app. |
| `SettingsStore` / rules JSON | `src/rules.rs` | Atomic write + corrupt-file quarantine; an all-empty rule deletes itself. |
| Rules + `FirewallEnforcer` policy | (service, next) | Block rules named `FluxLimiter Block …`; blocking `C:\Windows` or the service itself is refused. |
| `TrafficCollector` (ETW) approach | (phase 1b) | Per-PID measurement via `Microsoft-Windows-Kernel-Network`; in Rust via `ferrisetw`. Its event IDs/fields were never validated in C# either — numbers stay hidden until a known-size download test passes. |
| UI contract + review lessons | `src/main.rs` | Speeds in **KB/s** (the user's decision), rates on their own 1 s tick, totals accumulated per executable (not per PID), loopback filtered, no invented numbers anywhere. |
| `DriverBridge` | — | It was only a stub probing `\\.\FluxLimiter`; there is no driver in either codebase. Enforcement is still the open milestone (WinDivert user-mode engine or a WFP callout driver). |

Not carried over: all WinForms code (the UI is egui now), the C#
patches in `2_FIXES_TO_APPLY/` (their *security principles* were
ported into `protocol.rs`'s requirements instead), and any claim that
was marked unverified in the repo's own status docs.

## Status of this Rust build

* ✅ Core library: formatting/units, shared rate limiter + pacer,
  rules store, daily stats store, protocol v1 framing — with tests.
* ✅ GUI (egui): dashboard matching the approved design, KB/s speeds,
  daily/monthly usage, 7-day chart, active-applications table with
  limit dropdowns and block toggle (rules saved; **not enforced yet**,
  and the UI says so).
* ✅ Taskbar speed strip: a plain always-on-top egui viewport next to
  the tray — two lines (↓/↑), no themes, per the user's request.
* ✅ Active applications on Windows from the IP Helper extended
  TCP/UDP tables (`src/win_tables.rs`); honestly empty elsewhere.
* ⏳ Service binary speaking protocol v1 (pipe server) — `protocol.rs`
  is ready, the Windows service host is next.
* ⏳ ETW per-app accounting (phase 1b, `ferrisetw`).
* ⏳ Enforcement engine (phase 2): WinDivert packet loop paced by
  `rate_limiter.rs`, or WFP driver — decision gate in the C# repo's
  enforcement spike applies unchanged.

## Build

```bat
cargo build --release
cargo test
```

On Windows this produces a single `fluxlimiter.exe` (a few MB, no
runtime to install). Run it as administrator for the full connection
tables.
