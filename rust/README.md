# FluxLimiter — Rust edition

The all-Rust rebuild of FluxLimiter (a NetLimiter-like traffic monitor
and limiter for Windows). It supersedes two earlier codebases, whose
useful work was audited before a line of Rust was written:

* the **C# / .NET 8 WinForms project** — the user's repo
  <https://github.com/mo3gza39/FluxLimiter> (main @ `02965cf`, Windows
  CI green) and its `FluxLimiter-FINAL` zip snapshot (Round 5);
* the **Python + PySide6 prototype** built in chat — now only a design
  reference.

## Screenshots

The dashboard of the running app, under real traffic (live download):

![FluxLimiter dashboard](docs/screenshots/dashboard.png)

Captured from the actual running build (the UI is egui, so it renders
identically on Windows). The active-applications table is empty in
this capture because per-app connection tables are Windows-only
(IP Helper); on Windows it fills in. Nothing in the shot is mocked:
the speeds and the 162 MB daily total are the app's own live counters.

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
  Hovering it shows today's and this month's usage; right-clicking
  it opens a menu (Open FluxLimiter, bits toggle, Hide, Exit).
* ✅ Application icon: the blue bars mark from the project's
  reference design, set on the window, embedded into the .exe
  files by `build.rs`, and shown as the real icon texture in the
  sidebar (the icon travels as base85 text in `assets/` because
  this project's sync channel is text-only).
* ✅ Right-click menu on application rows (table and Top
  Applications): Open file location, Properties, Copy path,
  Block/Unblock (`src/shell_actions.rs`, Windows Explorer verbs).
* ✅ Active applications on Windows from the IP Helper extended
  TCP/UDP tables (`src/win_tables.rs`); honestly empty elsewhere.
* ✅ Service binary speaking protocol v1 (`src/service.rs` +
  `src/bin/fluxlimiter-service.rs`): dispatch for ping, getStatus,
  getSnapshot, getRules, setRule, deleteRule; refuses `blocked`
  rules for `C:\Windows` / FluxLimiter itself and a 0 KB/s limit;
  `syncFirewall` is an honest not-implemented error. Named-pipe
  transport on Windows (`\\.\pipe\FluxLimiter.Control`) with the
  spec's pipe security applied (SDDL: network SID denied, SYSTEM /
  administrators full control, users read-write; first-instance
  ownership), framed stdin/stdout elsewhere so the logic is
  testable; verified live over stdio (ping → setRule → getRules →
  getStatus all OK) and cross-built for Windows (GUI-subsystem GUI
  exe + console-subsystem service exe). Limits are still stored,
  not enforced — the driver engine remains the open milestone.
  The GUI does not talk to the service yet; wiring it in is the
  next step.
  
  One divergence to know about: the C# service that was actually
  implemented speaks a smaller dialect (pipe `FluxLimiter.Service.v1`,
  64 KiB frames, only `ping` / `status.get` / `driver.status`).
  This Rust line follows the project's written v1 spec instead —
  the richer rules/snapshot command set on
  `\\.\pipe\FluxLimiter.Control` with 1 MiB frames — so the Rust
  service is not a drop-in pipe replacement for the C# GUI.
* ⏳ ETW per-app accounting (phase 1b, `ferrisetw`).
* ⏳ Enforcement engine (phase 2): WinDivert packet loop paced by
  `rate_limiter.rs`, or WFP driver — decision gate in the C# repo's
  enforcement spike applies unchanged.

## Build

```bat
cargo build --release
cargo test
```

On Windows this produces `fluxlimiter.exe` (the GUI, a few MB, no
runtime to install) and `fluxlimiter-service.exe` (the headless
service). Run the GUI as administrator for the full connection
tables.
