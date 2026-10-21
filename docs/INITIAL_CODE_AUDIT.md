# FluxLimiter — Initial Source Audit
Date: 2026-10-21
Branch: review/initial-audit
Scope: seven source/configuration files copied from dev/initial-scaffold. Static inspection only; no Windows build, runtime benchmark, or network enforcement test performed.

## Status
**Scaffold / prototype, NOT a functional bandwidth limiter.** Current UI enumerates running processes, edits JSON rules, and exports visible rows to CSV. It does not monitor per-process network traffic or enforce speed/blocking rules.

## Reviewed files
- src/FluxLimiter/FluxLimiter.csproj — .NET 8 WinForms project.
- src/FluxLimiter/Program.cs — WinForms entry point; no service lifecycle.
- src/FluxLimiter/Models.cs — per-path rules and startup flag only.
- src/FluxLimiter/SettingsStore.cs — JSON persistence.
- src/FluxLimiter/MainForm.cs — process list, editor, CSV export.
- .github/workflows/windows-build.yml — restore/build/publish artifact definition.
- docs/ROADMAP.md — acknowledges missing implementation.

## Findings by priority
### P0 — core requirements absent
1. **No enforcement engine.** DownloadLimitKbps, UploadLimitKbps and Blocked are stored/displayed but never enforced. Never market as a functioning limiter.
2. **No traffic attribution or counters.** No reliable per-executable upload/download measurement; therefore no per-app speed, usage, monthly reports or threshold notifications.
3. **No persistent Windows service.** Closing the UI ends the process inventory timer; startup setting exists but has no registration logic. Service must apply saved rules independently of UI.
4. **No aggregate multi-process group limiting.** Current rule key is ExecutablePath only; no executable group model or shared budget scheduler. A working implementation requires careful validation of Windows traffic interception capabilities and any driver signing/deployment constraints before UI promises.

### P1 — correctness and resilience
5. **Process enumeration on UI thread.** MainForm.Reload() calls Process.GetProcesses(), sorts, reads MainModule, clears and repopulates DataGridView synchronously every 2.5 seconds. Risk of stutter and selection loss; use background snapshots/diff updates with bounded refresh and cancellation.
6. **Weak system/user classification.** 'System' means path starts with Windows folder. Windows components elsewhere, inaccessible paths, and user executables in Windows directories are misclassified. Unknown path currently treated as user process.
7. **Unavailable process paths.** MainModule access errors are silently converted to empty path; no robust path identity resolution or clear 'unavailable' status. Editing then fails.
8. **Settings error handling incomplete.** Load catches JsonException and IOException but not access/permission errors; Save catches IOException only in UI, not UnauthorizedAccessException or other failures. Need backup, validation, recovery, atomicity/durability testing, schema versioning, and migration.
9. **Ambiguous rule units.** Model fields use Kbps while UI says KB/s. 'Kbps' commonly means kilobits per second; KB/s means kilobytes per second. Standardize explicit byte-based internal units and validate conversions, including MB/s and Unlimited.
10. **No settings enforcement/validation across reload.** Deserialized rules are not validated/deduplicated; case-insensitive path comparisons may need normalization, handling of reparse points and relocation. A single path rule is not a grouped-executable rule.
11. **CSV export robustness.** Writes synchronously without error handling; fields are quoted correctly for CSV syntax, but spreadsheet formula injection remains possible when opening arbitrary process names/paths in Excel. Explicit export sanitization and error feedback needed.
12. **Double-click rule editor UX.** The app uses a blocking modal editor rather than approved inline editing/right-click context menu. No Open File Location, Properties, Copy Path, Usage Statistics, View Process Details.
13. **No telemetry persistence.** JSON rules are not an appropriate substitute for an indexed time-series usage store; define rollover at local calendar month boundary, clock changes, crash recovery and aggregation semantics.
14. **No automatic adapter selection or double-count protection.** Requires definition and verification for Wi-Fi, Ethernet, VPN, virtual adapters, loopback and simultaneous interfaces.
15. **No user-visible differentiation between unavailable and zero.** Current view lacks actual speed/usage fields entirely; design must not display invented zero measurements.

### P2 — product completeness
16. Dashboard, Daily/Monthly Usage pages, Top Applications, taskbar indicator, hover tooltip, light/dark Glass Lite, solid fallback and automatic theme are not implemented.
17. GitHub Releases update checks and cumulative 50 MB one-time notifications are not implemented. Threshold should apply only to applications without user-defined management rules, with durable per-executable notification state.
18. No executable grouping UI, no real block control, no taskbar Explorer-restart/DPI/multi-monitor handling.
19. GitHub Actions workflow exists, but this audit has not verified any successful run. Publishing a single-file self-contained executable does not prove the application works or is safe.
20. No tests, performance budgets, benchmarks, or Windows integration tests found among the reviewed files.

## Implementation gates (do not claim completion without evidence)
G0: Confirm Windows build succeeds, test clean install and startup.
G1: Prototype real per-executable traffic accounting for TCP/UDP, IPv4/IPv6, multiple processes, service permissions, network adapters, and VPN.
G2: Prototype enforceable aggregate per-executable/group download and upload caps; benchmark throughput, CPU/RAM, latency and failure handling; document whether signed kernel component or other Windows-supported mechanism is required.
G3: Service and secure IPC, atomic/versioned settings, restart-safe rule application, calendar-month persistence.
G4: UI/Taskbar/Glass Lite, notifications, CSV/XLSX, recovery and usability.
G5: Reproducible CI builds, regression tests, security review, and measured performance.

## Accepted product behavior (specification, not implementation)
- Speed limits only; no data-volume cut-off.
- Aggregate cap per executable path; optional group of executable paths shares one cap.
- Independent download/upload; rules persist across restarts.
- Monitoring across network adapters, manual override; avoid double counting.
- Monthly accounting resets calendar day 1; history retained.
- One notification when an unmanaged executable first exceeds cumulative 50 MB; no auto-block or auto-limit.
- GitHub Releases notification links out; no auto-install.
- Background service persists when UI closes; Taskbar monitor lightweight.
- Glass Lite dark/light/system, solid fallback, no unnecessary animations.
- No assistant credit/signature without owner approval.

## Limitations
This is a static review of the seven known scaffold files, not an exhaustive repository tree scan, build verification, dynamic security audit, or performance certification. All untested behavior remains unverified.
