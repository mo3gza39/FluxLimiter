# FluxLimiter prototype status — 2026-10-21

This branch is a **development prototype**, not a usable bandwidth limiter.

## Implemented in source (build/runtime not verified)
- Consistent limit units: **KB/s = 1024 bytes per second**, not kilobits. UI and model now use `KBps` (capital B). Old `Kbps` JSON fields are accepted for migration and are not written back.
- Per-executable full-path rule resolution, plus a group data model that can resolve multiple executables to a shared group rule. **No group editor or enforcement scheduler yet**.
- Settings save with temp file and backup on replacement, basic validation/deduplication on load.
- Lightweight adapter-counter sample displayed in the main window approximately once a second; **device-wide only**. VPN/virtual adapters can cause double counting; not suitable for accurate billing or per-process measurement.
- Seven context menu items; some are explicitly placeholders. 'Block Internet' edits a stored rule but **does not block traffic**.
- Existing CSV export and process list.

## Not implemented
Actual throttling or blocking; network interception; per-app accounting; service; taskbar two-line monitor; accurate daily/monthly statistics; application grouping editor; smart async process enumeration; Glass Lite/theme fallback; notifications; Windows startup registration.

## Build and validation
The GitHub Actions workflow is present. Its result has not been verified in this session. Source changes require a successful Windows build and runtime test before release. There is no claim of performance or traffic-limit correctness.

## Known issues to resolve before release
- Process enumeration is still synchronous and may stutter.
- Path-based rule matching cannot attribute traffic without an OS networking component.
- Current device adapter counter includes virtual interfaces; no robust adapter selector.
- Windows Explorer integration for the Properties menu must be tested.
- Rule-group membership has no UI yet; group model is not enforcement.
- Settings backup recovery and schema migration need automated tests.
- CSV needs spreadsheet-formula hardening before handling arbitrary process names.
- Driver/service architecture and deployment/signing must be proven.

## Acceptance criteria for next milestone
1. Successful Windows CI build with downloadable artifact.
2. Test the preview on Windows 11 and log startup, process enumeration, menu behavior, CPU/RAM.
3. Independently validate process traffic attribution, then aggregate throttling under parallel TCP/UDP workloads.
4. Only then connect the UI controls to a verified enforcement service.
