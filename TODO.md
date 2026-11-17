# Monitor App Short Term Tasks

Read `AGENTS.md` and the complete documentation set before changing this file or starting a task. This list contains active and near-term work only. Durable completed history belongs in `docs/CHANGELOG.md` and `docs/DECISIONS.md`.

## Current milestone Functional foundation

Goal: prove a durable controller-to-agent telemetry path with meaningful tests before advanced visual work.

### Blockers and decisions

- [x] Select the executable implementation stack for controller, shared contracts, agents, Android, persistence, and root build orchestration.
  - T006 accepts .NET 10 LTS, C#, ASP.NET Core gRPC with Protocol Buffers, a shared .NET worker agent, and Avalonia for the later client UI.
- [x] Select the first persistence engine.
  - T008 accepts file-backed SQLite in WAL mode for the personal-scale slice with real persistence tests and a benchmark revisit condition.
- [x] Select the first supported controller deployment path.
  - T007 accepts Docker Compose for the controller while agents remain native packages.
- [ ] Pin the initial Headscale version for integration testing and document its compatibility assumptions.

### Repository foundation

- [x] Create the root build and verification entry points.
  - `MonitorApp.sln` and `eng/verify.ps1` now perform the available locked restore, Release build, domain unit tests, and formatting verification.
- [x] Add reproducible dependency locking and ignored local-secret configuration.
  - The .NET 10.0.400 feature band, centralized package versions, project lock files, and local-secret ignore rules are in place.
- [x] Create the initial versioned shared contracts for agent identity, platform capabilities, typed telemetry, quality, units, events, enrollment, protocol errors, and the bidirectional agent session.
  - Generated C# contracts and semantic validation live in `MonitorApp.Contracts`; Headscale and controller behavior remain outside this boundary.
  - Enrollment is hardened with P-256 device-key possession, no reusable raw agent credential, short-lived device challenges, and six unary lifecycle RPCs.
- [x] Replace the placeholder test-command section in `docs/TESTING.md` with exact canonical commands.
  - Commands remain unverified until the solution, projects, Compose harness, and `eng/verify.ps1` exist.
- [x] Add automated formatting, warnings-as-errors, recommended static analysis, and initial unit-test execution.
- [ ] Add automated secret scanning appropriate to the selected stack.
- [x] Establish signed, integrity-checked release manifests and CI/release automation.
  - Production verification and release tooling exist; the protected workflow remains unverified until the private repository has release keys and runs its first immutable-tag release.
- [x] Restore platform-neutral dependency locks and prevent release packaging from rewriting them.
  - Runtime-specific restores now use ignored staging lockfiles, packaging leaves every tracked file unchanged, and the release workflow enforces that invariant.

### Controller vertical slice

- [ ] Implement health and version negotiation.
- [x] Implement durable application enrollment with expiration and single-use enforcement.
  - File-backed SQLite WAL storage and the production gRPC service implement create, status, cancel, complete, challenge, and proof verification.
- [x] Generate device and enrollment challenges with a cryptographically secure source and atomically consume them once during proof verification.
- [ ] Persist enrolled P-256 public keys and fingerprints, bind successful proof to the application session, and add device-key revocation and rotation behavior.
  - [x] Persist canonical P-256 public keys and fingerprints.
  - [x] Bind a fresh proof to each authenticated AgentSession without treating the separate proof response as a session credential.
  - [ ] Add device-key revocation and rotation behavior.
- [x] Implement application device identity separate from network membership.
- [x] Implement capability registration through the authenticated hello and durable stream state.
- [x] Implement validated telemetry ingestion with source time, ingest time, unit, quality, and sequence handling.
- [ ] Persist nodes, enrollments, metric samples, and events across controller restart.
  - [x] Persist nodes, enrollments, device challenges, and redacted security audit events across controller restart.
  - [x] Persist metric samples.
  - [ ] Persist operational events beyond metric envelopes and security audit records.
- [ ] Add the SQLite WAL reference-load benchmark and record the workload and acceptance thresholds before interpreting its result.
- [ ] Prove SQLite migration, restart durability, retention, backup, and restore with the production provider and file-backed configuration.
- [ ] Implement current-state and bounded historical query endpoints.
  - [x] Owner-gated inventory and latest-metric diagnostic endpoints.
  - [ ] Bounded historical range queries.
- [ ] Implement the first snapshot and deterministic reconstruction path.

### Endpoint agent vertical slice

- [ ] Implement the shared outbound agent session and reconnect behavior.
  - [x] Coordinate hello, controller limits, ordered batches, cumulative acknowledgements, and identical retry through a transport boundary.
  - [x] Implement the concrete authenticated gRPC transport with a fresh proof bound to the exact stream.
- [ ] Implement bounded offline buffering and duplicate-safe resume.
  - [x] File-backed SQLite WAL spool preserves monotonic sequences across restart, refuses silent eviction at capacity, and deletes only acknowledged prefixes.
  - [ ] Prove resume and no-duplicate history against the real controller process.
- [x] Implement truthful host identity and collector health reporting.
  - Windows behavior is platform-verified; Fedora and Ubuntu host behavior remains unverified.
- [x] Implement initial CPU, memory, storage, and network collectors for Windows.
- [ ] Implement initial CPU, memory, storage, and network collectors for Fedora.
  - Linux `/proc` parsing and shared storage/network collection exist; Fedora-local evidence is still required.
- [ ] Implement initial CPU, memory, storage, and network collectors for Ubuntu.
  - Linux `/proc` parsing and shared storage/network collection exist; Ubuntu-local evidence is still required.
- [ ] Report unsupported or permission-denied capabilities as unavailable rather than zero.
  - Shared mappings exist; platform-local permission-denial tests remain required.

### Android vertical slice

- [x] Create the Android application foundation after the stack decision.
  - The native .NET Android foreground alpha compiles as a development-signed Android API 36 Release APK; the platform-independent core and tests run in the root solution.
- [x] Implement application enrollment and controller connectivity.
  - HTTPS controller health and P-256 Android Keystore enrollment completion exist; authenticated telemetry upload remains part of the controller-to-agent slice.
- [x] Implement device identity, battery, charging, network type, VPN state where available, and freshness.
  - Values are foreground snapshots with explicit UTC source time. Background collection is not implied.
- [ ] Validate background behavior on an emulator.
- [ ] Define and later run a physical-device smoke test before claiming Android support.

### Functional client

- [ ] Provide a minimal functional client or diagnostic interface for enrollment state, inventory, current telemetry, freshness, and history.
  - [x] Provide owner-gated inventory and current-metric HTTP/2 diagnostic reads.
  - [ ] Add enrollment status, derived freshness, and bounded history to a usable client.
- [ ] Keep the interface plain and accessible; do not begin final visual language or animation work in this milestone.

### Tests required for this milestone

- [ ] Unit tests for enrollment expiration, freshness, capability negotiation, sequence handling, and reconstruction.
  - [x] Freshness state, quality, numeric-zero, and threshold boundary tests.
    - Unknown and unavailable observations retain optional last-contact evidence, Offline has explicit precedence after expiration, and configurable future skew is covered at and beyond the accepted boundary.
  - [x] Agent spool sequence allocation, restart continuation, cumulative acknowledgement, capacity, and retry tests.
  - [x] Enrollment expiration and telemetry sequence/idempotency handling tests.
  - [ ] Capability negotiation and reconstruction tests.
- [ ] Contract compatibility tests for every initial message and supported upgrade fixture.
  - [x] Initial round-trip, unknown-field preservation, Android platform, typed-zero, bidirectional-session descriptor, and semantic validation tests.
  - [x] Enrollment-service shape, exact cryptographic field sizes, real P-256 signing round trips, wrong-curve rejection, identity-binding, and safe-error redaction tests.
  - [ ] Add frozen prior-version fixtures when the first supported upgrade window is declared.
- [ ] Real persistence integration tests including restart durability.
  - [x] Enrollment, node identity, challenge replay, concurrent single-use completion, and restart durability use real file-backed SQLite WAL.
  - [x] Agent telemetry buffering and acknowledgement use real file-backed SQLite WAL across restart.
  - [x] Metric ingestion, current-state reads, idempotent retry, and restart durability.
  - [ ] Event history, migration upgrades, retention, backup, and restore remain.
- [ ] Cross-process controller and agent integration tests.
- [ ] End-to-end disconnect, buffer, reconnect, and no-duplicate history test.
- [x] Invalid, expired, cancelled, reused, wrong-platform, wrong-name, and wrong-signature enrollment rejection tests.
- [ ] Platform collector integration tests on Windows, Fedora, and Ubuntu environments.
  - [x] Windows-local CPU, memory, storage, network-quality, host identity, and boot evidence.
  - [ ] Fedora-local collector and service lifecycle evidence.
  - [ ] Ubuntu-local collector and service lifecycle evidence.
- [ ] Android instrumentation tests for the implemented collector boundary.
  - [x] Platform-independent Android core tests cover truthful battery/network normalization, HTTPS origin policy, one-time enrollment parsing, canonical P-256 request construction, and DER-to-P1363 conversion.
  - [ ] Android Keystore, device APIs, lifecycle, network permission, installation, and controller connectivity require emulator/physical-device tests.

## Next milestone Headscale and installation

- [ ] Implement the Headscale adapter against the pinned test version.
- [ ] Add a real Headscale integration suite for create, join, expire, reuse rejection, and revoke.
- [ ] Integrate Headscale enrollment without weakening Monitor App application identity.
- [ ] Design clean install, upgrade, restart, and uninstall flows for Windows service, Fedora RPM, Ubuntu DEB, and Android.
- [ ] Add platform packaging tests before claiming platform support.
- [ ] Configure the private repository's protected `release` environment, signing key, trusted public key, and key id; execute and inspect the first immutable-tag alpha release.
- [ ] Implement verified installer apply, atomic service replacement, rollback, and recovery for Windows, Fedora, and Ubuntu; current self-contained ZIP files are not installed-service packages.
- [ ] Add the signed Android APK/AAB release job only after the Android project, protected Android signing material, emulator suite, and physical-device smoke test exist.

## Deferred until the functional foundation works

- Final living-schematic rendering technology.
- Exploded-architecture visual implementation.
- Game-like physical interactions and cinematic transitions.
- Visual concept comparison prototypes.
- Operational device-control actions.
- Syncthing's final purpose-built inspector.
- Remote notification services.
- Multi-user tenancy.
