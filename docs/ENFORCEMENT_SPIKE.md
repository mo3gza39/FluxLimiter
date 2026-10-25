# Bandwidth enforcement feasibility spike — 2026-10-21

## Delivered
- `SharedRateLimiter`: deterministic, synchronized, shared token-bucket **decision logic** in bytes, configured using binary KB/s.
- `tests/FluxLimiter.CoreTests`: standalone smoke tests for refill, burst, shared identity, cap changes and group resolution.
- GitHub Actions now invokes these tests on Windows after compiling the WinForms application.

## Evidence and verification boundaries
The code and test files have been fetched back from GitHub after upload. This confirms repository persistence, **not test execution**. This environment has no installed dotnet SDK. The available GitHub connector's commit workflow lookup only reports PR-triggered runs, and returned none for the latest commit; therefore CI success/failure is **unknown**. No Windows networking driver, packet interception, TCP/UDP pacing or genuine download/upload throttling has been built.

## Important engineering constraints
1. The scheduler currently uses one-second bucket capacity. Any single request larger than the entire second's allowance is denied indefinitely unless a caller fragments it into smaller units. A real traffic queue must split/pace packets appropriately and avoid head-of-line blocking.
2. Tokens are consumed only when the caller invokes the method; OS traffic is not intercepted.
3. A group must map all member executable paths to one stable bucket identity. The current test passes a shared string; integration is not built.
4. The prototype does not distinguish download from upload in its scheduler API. Production integration needs separate direction keys.
5. Accurate incoming download limiting may require packet handling or transport-aware shaping and cannot be assumed from WFP classification alone.
6. Driver signing, administrator permissions, crash-safe bypass, service lifecycle, process attribution, UDP, IPv6 and VPN handling are unimplemented.

## Next acceptance gate
- Obtain successful Windows build and smoke-test logs.
- Build an explicit test-only traffic adapter to exercise queueing and separate directional budgets.
- Validate architecture for OS enforcement, including driver feasibility and security before claiming working throttling.
