# Testing Strategy

## Purpose

Tests exist to protect behavior required for Monitor App to enroll devices, collect truthful telemetry, preserve history, survive failure, and operate on Android, Windows, Fedora, and Ubuntu. Test count and coverage percentage are secondary to operational value.

The confirmed target is now an equal-peer mesh. Controller/enrollment tests below remain evidence for reusable legacy components, not proof of the target architecture. New work must prioritize the peer gates in the next section.

## Decentralized peer migration gates

- **Pairing contract:** two peers introduced by address complete bilateral application-key proof; address/tailnet membership alone fails; wrong key, substitution, replay, expiration, revocation, and incompatible versions fail closed.
- **Real two-peer integration:** start two production peer servers with separate file-backed SQLite stores, pair them, exchange origin-owned observations, disconnect, restart both, reconnect, and prove eventual convergence, stable identity, ordering, and no duplicate/conflicting sequence acceptance.
- **Local query:** each peer reads its own and replicated records with explicit origin, source time, ingest time, unit, quality, and freshness; offline cache remains distinguishable from live state.
- **Observed devices:** adapter tests preserve stable peripheral identity across host reassignment, record typed attachment/collection relationships, and expose adapter capability, health, and freshness. Failover remains unavailable until the second collector is explicitly provisioned.
- **LAN topology:** reconcile reports from multiple peers using strong switch identity rather than IP; retain source/evidence/quality; distinguish reported from inferred links; keep physical LAN and Tailscale overlay separate.
- **Capability compatibility:** versioned namespaced modules register entities, observations, relationships, sampling/retention, categories, privacy/cost, and inspector descriptors. Unknown compatible modules receive a truthful generated fallback; incompatible modules fail explicitly.
- **Privacy enforcement:** registration rejects prohibited packet/flow/identity/content observations while permitting declared aggregate operational network metrics. Adapter credentials never serialize into replicated records.
- **Defensive security:** the capability cannot start without separate enablement; findings require evidence/confidence; deeper flow/packet-derived modes require their own permission and bounded retention.

No platform is supported until its peer service lifecycle, protected identity storage, pairing/reconnect, local persistence/query, update, and required collector behavior pass on that named platform.

## Expanded scope gates (planned, not current suites)

- **Actual versus design state:** schema/API/UI tests prove design-only objects, quantities, costs, loads, connections, and warnings never appear as actual inventory or running telemetry. Explicit promotion links an actual instance to its plan/candidate/offer history without mutating the historical plan.
- **Universal identity:** property and persistence tests distinguish product definition, seller offer, physical instance, observed peer/device, and design placeholder; revisions preserve provenance and typed relationship validity.
- **Cable model:** round trips preserve two connector ends, directionality/reversibility, exact standard, nominal/measured unit-bearing length, ratings, condition, location/custody, actual connection, reservation, and planned assignment. Matching tests reject wrong direction, insufficient routed length/slack, and incompatible power/data/audio ratings. Manual connector-pair and length templates require no QR or scanner hardware.
- **Constraint modules:** unit conversion fixtures and reviewed domain cases cover cable, audio, RF, electrical, thermal, dimensional, and mounting rules. Missing/stale inputs produce unresolved findings, not safe/compatible. Findings preserve rule version, inputs, assumptions, plane, evidence, and confidence.
- **Sourcing and price history:** recorded provider fixtures exercise normalization, currency/source time, offer-versus-product identity, blocked/rate-limited/stale states, and prevention of false zero-price/out-of-stock values. Live provider smoke tests are separately permissioned and never replace deterministic fixtures.
- **Automation:** fake-clock plus real persistence tests cover restart, schedules, duration thresholds, debounce, cooldown, deduplication, quiet hours, escalation, expiration, idempotency, rule versioning, and durable outbox retry. A two-peer test proves replicated events produce one owned evaluation/action, including ownership reassignment. Historical simulation dispatches nothing.
- **Notifications:** each adapter tests secret redaction, metadata minimization, offline retry, acknowledgement where supported, invalid destination, provider failure, duplicate suppression, and Android lifecycle limits. A compiled client is not delivery evidence.
- **Local AI:** tests enforce model/tool capability grants, actual/design context labeling, source/model provenance, input-size limits, prompt-injection resistance at tool boundaries, no silent fact overwrite, and no action-permission bypass. Deterministic behavior is tested without an AI dependency.
- **History and experience:** reconstruction aligns objects, relationships, observations, automation events, and actual-state time. Plan revisions use design revision history rather than masquerading as observed time. Accessibility tests expose the same plane, values, units, provenance, and findings as the visual scene.

These gates become canonical commands only when their production projects exist. Until then, documentation is not executable evidence.

## Nonnegotiable quality rules

- Every test must execute in the documented command or be reported as unverified.
- A test must fail for a relevant defect in the behavior it claims to protect.
- Tests call production code through the narrowest meaningful public boundary.
- Do not duplicate the production algorithm in the assertion.
- Do not mock the subject under test.
- Do not use unconditional passes, assertions against constants, or snapshots with no semantic checks.
- Avoid arbitrary sleeps. Use bounded polling, fake clocks, or explicit synchronization.
- Integration-test names must identify the real dependency they exercise.
- A skipped test must contain a visible reason and tracking item.
- Flaky tests are defects. Quarantine requires a todo item, owner, and reason.

## Test levels

### Unit tests

Use for deterministic domain behavior such as:

- Contract validation.
- Freshness-state calculation.
- Capability negotiation.
- Enrollment expiration.
- Sequence and duplicate handling.
- Retention selection.
- Snapshot plus event reconstruction.
- Permission and action-expiration rules when actions are introduced.

Unit tests may use fake time and in-memory values. They should not be used to claim that a database, network, installer, service manager, or Android lifecycle works.

### Contract tests

Every serialized request, response, event, and telemetry envelope needs compatibility tests that cover:

- Required fields.
- Unknown fields according to the compatibility rule.
- Schema or protocol version.
- Invalid identity.
- Invalid timestamps and units.
- Platform capability differences.
- Backward-compatible decoding for the supported upgrade window.
- Exact enrollment-secret, challenge, P-256 signature, and public-key-fingerprint sizes.
- Real ECDSA P-256/SHA-256 signing and verification across a serialization round trip.
- Proof failure when a bound enrollment id, agent id, challenge id, challenge, or public key changes.
- Service method names and unary or streaming shape.
- Safe validation errors that never echo enrollment secrets, challenges, or signatures.

### Persistence integration tests

Run against the actual selected persistence engine. Required cases include:

- Schema creation and migration.
- Enrollment durability across controller restart.
- Metric and event insertion.
- Duplicate rejection or idempotent acceptance.
- Ordered time-window query.
- Snapshot reconstruction.
- Retention deletion boundaries.
- Backup and restore before release support is claimed.

The first production engine is file-backed SQLite in WAL mode. Persistence integration tests must use that provider and configuration. The EF Core in-memory provider does not satisfy this level, and SQLite's transient in-memory mode does not prove restart durability, file locking, WAL recovery, backup, or restore.

### Protocol integration tests

Start real controller and agent processes or their production network servers. Required cases include:

- Health and version negotiation.
- Enrollment success.
- Expired and reused enrollment rejection.
- Agent identity mismatch rejection.
- Capability registration.
- Telemetry acceptance and validation failure.
- Disconnect, bounded buffering, resume, and no duplicate history.
- Controller restart and agent reconnect.
- Explicit incompatibility error for unsupported protocol versions.

### Superseded Headscale integration tests

This suite is no longer a target requirement. Do not add automatic Headscale provisioning. Tailscale is user-managed reachability; test the real peer protocol over loopback for deterministic integration and over a Tailscale path in platform verification.

Use a pinned Headscale container or supported real service. Do not mock Headscale and call the result an integration test. Required cases include:

- One-time registration material is created and expires.
- A device can join the test tailnet.
- Reuse is rejected.
- Revocation prevents the expected future access.
- Monitor App application identity is still required after network membership.
- Headscale outage is distinguished from controller and agent failure.

These tests may run in a slower, explicitly named suite if the local environment cannot always supply the required privileges.

### Platform collector tests

Each platform collector needs:

- Parser and normalization unit tests with edge cases.
- A platform-local integration test that compares collected identity and resource values with reliable operating-system evidence.
- Unsupported-capability tests that return unavailable rather than zero.
- Permission-denied behavior.
- Service lifecycle test where the platform permits automation.

Required target environments:

- Windows runner or clean Windows virtual machine.
- Fedora runner or container/VM appropriate to the tested boundary.
- Ubuntu runner or container/VM appropriate to the tested boundary.
- Android emulator for repeatable automated tests.
- Android physical device for a release smoke test of background and VPN-sensitive behavior.

A Linux container can test portable Linux collection code but does not prove RPM installation, SELinux behavior, `systemd`, or a Fedora release claim. The same distinction applies to Ubuntu packaging and Windows services.

### Packaging tests

Before platform support is claimed, test a clean install, upgrade, restart, and uninstall for:

- Windows service package.
- Fedora RPM.
- Ubuntu DEB.
- Android APK or application bundle path.
- Peer service and local data package on every applicable platform.

Installation tests must confirm that uninstall removes the application while preserving or removing data according to an explicit user choice.

### End-to-end functional test

The first required target end-to-end test is the real two-peer scenario in **Decentralized peer migration gates**: bilateral pairing, local collection/query, offline collection, restart, direct reconnect, and duplicate-safe convergence using separate production SQLite stores. Controller/agent end-to-end tests are legacy component evidence only.

## Security tests

Required security-focused behavior includes:

- Enrollment token is not stored or logged in plaintext.
- Expired, reused, malformed, and wrong-device credentials fail closed.
- Network membership without application identity is insufficient.
- An unprivileged agent cannot invoke a future control operation.
- Diagnostic output redacts secrets.
- Input size and rate limits protect ingest boundaries.
- Enrollment completion proves possession of the submitted P-256 public key.
- Device authentication challenges expire, are single use, and reject replay or identity substitution.

## Test commands

The accepted stack is .NET 10. The repository currently contains the domain and generated-contract libraries, legacy controller components, and peer core, persistence, application, replication, and gRPC-host projects with tests. Commands for projects that do not exist yet remain canonical future targets and are identified below.

### Documentation-only validation

When only prose documentation or todo/decision/changelog text changes, and no command, contract, configuration, package, schema, or executable behavior changes, run:

```powershell
git diff --check
```

Also search for directly affected terminology or links to catch contradictions. The full `eng/verify.ps1` suite is not required for that case. If executable commands, platform claims, generated artifacts, or code-facing configuration changed, run the relevant executable checks and the broader suite in proportion to risk.

If the task is read-only or leaves no tracked repository diff, run no verification command.

Restore and compile:

```powershell
dotnet restore MonitorApp.sln --locked-mode
dotnet build MonitorApp.sln -c Release --no-restore -warnaserror
dotnet format MonitorApp.sln --verify-no-changes --no-restore
```

Current contract tests:

```powershell
dotnet test tests/MonitorApp.Contracts.Tests/MonitorApp.Contracts.Tests.csproj -c Release --no-build --filter "Category=Unit|Category=Contract"
```

Current domain unit tests:

```powershell
dotnet test tests/MonitorApp.Domain.Tests/MonitorApp.Domain.Tests.csproj -c Release --no-build --filter "Category=Unit"
```

Combined current unit and contract tests:

```powershell
dotnet test MonitorApp.sln -c Release --no-build --filter "Category=Unit|Category=Contract"
```

Controller enrollment boundary and real SQLite WAL persistence integration tests:

```powershell
dotnet test tests/MonitorApp.Controller.IntegrationTests/MonitorApp.Controller.IntegrationTests.csproj -c Release --no-build
dotnet test tests/MonitorApp.Persistence.Tests/MonitorApp.Persistence.Tests.csproj -c Release --no-build --filter "Category=SQLite"
```

Agent collection and durable offline-spool tests:

```powershell
dotnet test tests/MonitorApp.Agent.Core.Tests/MonitorApp.Agent.Core.Tests.csproj -c Release --no-build --filter "Category=Unit"
dotnet test tests/MonitorApp.Agent.Persistence.Tests/MonitorApp.Agent.Persistence.Tests.csproj -c Release --no-build --filter "Category=SQLite"
```

Peer contract, identity/pairing, persistence, replication, and network-host tests:

```powershell
dotnet test tests/MonitorApp.Contracts.Tests/MonitorApp.Contracts.Tests.csproj -c Release --no-build --filter "Category=Unit|Category=Contract"
dotnet test tests/MonitorApp.Peer.Core.Tests/MonitorApp.Peer.Core.Tests.csproj -c Release --no-build --filter "Category=Unit"
dotnet test tests/MonitorApp.Peer.Persistence.Tests/MonitorApp.Peer.Persistence.Tests.csproj -c Release --no-build --filter "Category=Unit|Category=SQLite"
dotnet test tests/MonitorApp.Peer.Application.Tests/MonitorApp.Peer.Application.Tests.csproj -c Release --no-build
dotnet test tests/MonitorApp.Peer.Replication.Tests/MonitorApp.Peer.Replication.Tests.csproj -c Release --no-build
dotnet test tests/MonitorApp.Peer.Service.Tests/MonitorApp.Peer.Service.Tests.csproj -c Release --no-build
dotnet test tests/MonitorApp.Peer.Transport.Tests/MonitorApp.Peer.Transport.Tests.csproj -c Release --no-build
dotnet test tests/MonitorApp.Peer.LocalApi.Tests/MonitorApp.Peer.LocalApi.Tests.csproj -c Release --no-build
# Windows only:
dotnet test tests/MonitorApp.Peer.Node.Tests/MonitorApp.Peer.Node.Tests.csproj -c Release --no-build
```

Signed update-manifest policy and artifact-integrity tests:

```powershell
dotnet test tests/MonitorApp.Update.Tests/MonitorApp.Update.Tests.csproj -c Release --no-build --filter "Category=Unit"
dotnet build eng/release/MonitorApp.ReleaseTool/MonitorApp.ReleaseTool.csproj -c Release --no-restore -warnaserror
```

The current Windows-local collector test runs only on Windows:

```powershell
dotnet test tests/MonitorApp.Agent.Core.Tests/MonitorApp.Agent.Core.Tests.csproj -c Release --no-build --filter "Category=Windows"
```

Cross-process functional end-to-end tests:

```powershell
dotnet test tests/MonitorApp.EndToEndTests/MonitorApp.EndToEndTests.csproj -c Release --no-build
```

Legacy Headscale integration commands (superseded; not a release gate):

```powershell
docker compose -f tests/e2e/headscale/compose.yml up --build --abort-on-container-exit --exit-code-from e2e
docker compose -f tests/e2e/headscale/compose.yml down --volumes
```

Android core and workload-specific build:

```powershell
dotnet test tests/MonitorApp.Android.Core.Tests/MonitorApp.Android.Core.Tests.csproj -c Release --filter "Category=Unit"
dotnet restore src/MonitorApp.Android/MonitorApp.Android.csproj --locked-mode -p:AndroidSdkDirectory="$env:ANDROID_SDK_ROOT" -p:JavaSdkDirectory="$env:JAVA_HOME"
dotnet build src/MonitorApp.Android/MonitorApp.Android.csproj -c Release --no-restore -warnaserror -p:AndroidSdkDirectory="$env:ANDROID_SDK_ROOT" -p:JavaSdkDirectory="$env:JAVA_HOME"
```

The Android build requires the .NET Android workload, Android API 36, and a supported JDK 21. The platform-independent Android core and its tests are in `MonitorApp.sln`; the workload-specific application project is intentionally outside the root solution so ordinary Windows and Ubuntu CI do not silently acquire mobile toolchains. The current Release APK was compiled on Windows with .NET SDK 10.0.400, .NET Android workload 36.1.69, Android API 36, and Temurin JDK 21.0.13. It is development-signed build evidence, not a production-signed release.

No emulator/AVD or Android instrumentation project is currently available. The future emulator command remains:

```powershell
dotnet test tests/MonitorApp.AppiumTests/MonitorApp.AppiumTests.csproj -c Release --no-build --filter "Category=Android"
```

Platform-local collector and service tests must run on the named operating system:

```powershell
dotnet test tests/MonitorApp.PlatformTests/MonitorApp.PlatformTests.csproj -c Release --no-build --filter "Category=Windows"
dotnet test tests/MonitorApp.PlatformTests/MonitorApp.PlatformTests.csproj -c Release --no-build --filter "Category=Fedora"
dotnet test tests/MonitorApp.PlatformTests/MonitorApp.PlatformTests.csproj -c Release --no-build --filter "Category=Ubuntu"
```

The root project provides one discoverable verification entry point at `eng/verify.ps1`. The canonical command is:

```powershell
pwsh ./eng/verify.ps1
```

The script currently restores locked dependencies, builds with warnings as errors, runs every existing solution test tagged `Unit` or `Contract`, runs the real SQLite enrollment/controller telemetry, agent-spool, peer-store, pairing-application, and peer-replication suites, runs controller and peer-host/outbound-transport real loopback HTTP/2 gRPC tests, runs the local administration HTTP suite, and verifies formatting. On Windows it also runs the collector plus the composed node/DPAPI tests. It explicitly reports the fresh-pairing/two-peer end-to-end path, cross-process legacy path, Headscale, Android device/instrumentation, Windows service lifecycle, Fedora, and Ubuntu suites as unavailable. The workload-specific Android APK command above remains a separate platform build. Add each missing suite when its project is created; verification must never silently skip a required suite.

The peer foundation suites currently cover generated contract shape and validation; real P-256 bilateral proof and transcript substitution/replay/expiry behavior; compatibility and duplicate-session selection; durable approval, challenge, and replay state; a real file-backed SQLite peer store; signed bounded replication; real Windows-loopback inbound/outbound gRPC; loopback-only local administration with CSRF/redaction/private-address validation; and a composed Windows node with restart-stable DPAPI identity. The composed-node suite starts two fresh nodes, submits an invitation through one local web API, approves it through the other, completes bilateral proof over production gRPC, and verifies active trust in both separate SQLite databases. These tests do not prove packaging/service lifecycle, real TLS/Tailscale deployment, non-Windows protected key storage, collection through the peer node, scheduled reconnect, or two-peer history convergence after outage.

The SQLite enrollment suite uses a real temporary database file with WAL enabled. It covers restart durability, expiration, cancellation, wrong-device intent, wrong signature, replay rejection, concurrent completion from separate store instances, and absence of raw, hexadecimal, or base64 enrollment secrets in database/WAL files. The telemetry case proves stream-challenge replay rejection, durable sequence acknowledgement, identical retry, conflicting retry rejection, latest-metric reads, and restart. The controller suite includes the enrollment service boundary plus a production Kestrel loopback HTTP/2 gRPC path covering authenticated spool upload, owner-gated reads, controller restart, duplicate retry, wrong-key rejection, cross-stream proof rejection, and safe error redaction. It is a real socket/protocol test in the test process; separate controller/agent process testing remains required.

The agent-spool suite uses a real temporary SQLite WAL file and production serialization. It covers restart durability, monotonic sequence continuation, cumulative prefix deletion, capacity refusal without eviction, refusal to violate controller batch size, and identical retry after upload failure. The Windows collector test calls production operating-system counters and verifies CPU, memory, storage, network-quality semantics, host identity, and boot evidence. Fedora and Ubuntu parser unit tests do not replace platform-local integration or packaging tests.

The update suite signs real manifest bytes with an ephemeral P-256 test key and calls the production verifier and artifact reader. It covers trusted-signature acceptance, wrong-key rejection, downgrade and reinstall rejection, compatibility floors, platform and architecture mismatch, canonical version parsing, and file size and SHA-256 mismatch. The GitHub release workflow, protected environment, generated archives, platform installation, rollback, and Android signing are not verified by those unit tests.

Self-contained release packaging performs runtime-specific restores into ignored staging lockfiles so the committed NuGet locks remain platform neutral. The release workflow runs `git diff --exit-code` after packaging and fails if any tracked file changes. Local packaging verification must likewise compare the tracked tree before and after archive creation; successful archive output alone is not sufficient evidence.

The Android core suite calls the production URI policy, one-time enrollment parsing, proof-request builder, battery normalization, network/VPN summary, and DER-to-P1363 conversion. A real P-256 key signs the canonical enrollment payload and the generated Protobuf request is verified through the controller-compatible P1363 path. These tests do not exercise Android Keystore, lifecycle, network permissions, device APIs, or controller connectivity; those require emulator and physical-device evidence.

Do not leave multiple undocumented ways to run the same suite. Package and platform jobs may wrap these commands, but the root verification script is authoritative.

## Completion evidence

A handoff must report the literal commands run, whether each succeeded, the environment used, and any suite not run. “Tests added” is not evidence that tests passed.
