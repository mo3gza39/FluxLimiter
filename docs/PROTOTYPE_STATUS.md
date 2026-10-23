# FluxLimiter prototype status — 2026-10-23

This branch is a development prototype, not yet a production bandwidth limiter.

## Implemented and CI-verified
- KB/s uses 1024 bytes; legacy ambiguous JSON keys are read-only migration inputs.
- Atomic settings persistence with backup/recovery and normalized rule lookup.
- Background process snapshots with incremental buffered grid updates.
- Device-level IP statistics sampler.
- Experimental ETW collector with payload-PID preference, retry, and stale-PID pruning.
- Windows Firewall application block rules synchronized incrementally by FluxLimiter-owned stable IDs.
- Deterministic shared limiter/pacer models for future driver scheduling; they do not intercept traffic.
- Windows Service scaffold, named-pipe protocol v1, explicit local ACL, and driver-device detection.
- Driver detection does **not** report enforcement capability; enforcement remains false until a real protocol handshake exists.
- Single UI instance.
- Windows CI builds/tests and publishes UI/service folders with native dependencies kept explicit.

## Still unverified on Windows runtime
- ETW event schema, PID attribution and byte accuracy against a known-size transfer.
- Firewall behavior for existing vs new connections and non-admin execution.
- Named-pipe connection from a normal non-admin GUI to the service.
- Performance/CPU/RAM under sustained traffic.

## Not implemented
- WFP callout driver and IOCTL enforcement.
- Real download/upload shaping.
- Authoritative service/driver traffic counters.
- Persistent daily/monthly accounting and history.
- Service-owned rule persistence / privileged mutation IPC.
- Startup installer/registration and remaining product pages/tray polish.

Do not describe saved speed limits as enforced until the WFP driver milestone passes Windows integration tests.
