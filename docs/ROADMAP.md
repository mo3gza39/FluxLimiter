# FluxLimiter roadmap

## Scope
Private, independent Windows bandwidth-control application. No AI, no NetLimiter modification.

## Milestone 0 — scaffold (current)
- .NET 8 Windows Forms dashboard, dark theme
- Running process inventory and simple user/system filtering (heuristic based on Windows folder)
- Per-executable rule editor for aggregate download/upload limits and blocking
- JSON persistence in LocalAppData; CSV export of visible process rows
- Windows GitHub Actions build and publish artifact

**Important:** No network counters, bandwidth throttling, blocking, service, SQLite database, startup registration, weekly/monthly statistics, or Excel export are implemented yet. Rule editor is storage-only. UI closing stops process enumeration. The workflow build result must be verified in Actions.

## Milestone 1 — traffic measurement
- Prototype process attribution via supported Windows APIs (ETW/WFP)
- Investigate accounting correctness for shared processes, IPv4/IPv6, UDP, loopback
- Store durable per-process daily/monthly usage in SQLite
- Background Windows service and IPC to UI

## Milestone 2 — enforcement
- Prototype Windows Filtering Platform or a suitable signed driver
- Aggregate rate limiting per executable, independent upload/download, fair queuing
- Explicit block rules; persist and restore after reboot
- Benchmarks and network-failure safety tests

## Milestone 3 — product
- Weekly/yearly usage and total device statistics
- Reset statistics and export CSV/XLSX
- Windows startup installer/service registration
- Light theme, UI polish, integration tests, releases

## Build
`dotnet build src/FluxLimiter/FluxLimiter.csproj -c Release` on Windows with .NET 8 SDK.
