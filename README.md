# FluxLimiter

Windows network-control prototype under active validation.

## Current source status

Implemented: WinForms dashboard/process inventory, atomic/versioned rule persistence, Windows Firewall application block synchronization, ETW-based experimental per-process traffic measurement, device-level adapter sampling, deterministic in-memory limiter/pacer models, Windows Service scaffold, versioned named-pipe status IPC, and CI builds/tests.

Not implemented or not yet proven: real download/upload throttling, WFP callout driver, driver IOCTL protocol/handshake, authoritative driver counters, persistent daily/monthly accounting, startup installer/registration, and Windows runtime certification of ETW attribution or firewall behavior.

**A successful CI build is not proof of network enforcement.** The UI intentionally labels saved speed limits as unenforced until a verified WFP driver exists.

## Validation gate

Before ETW figures are promoted to authoritative usage data, validate on Windows 11 with a known-size transfer and confirm event schema/PID attribution. Before any service IPC mutation operations are added, restrict write authorization to administrators and define the service-owned rule store.
