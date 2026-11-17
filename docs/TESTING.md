# Testing Strategy

## Purpose

Tests exist to protect behavior required for Monitor App to enroll devices, collect truthful telemetry, preserve history, survive failure, and operate on Android, Windows, Fedora, and Ubuntu. Test count and coverage percentage are secondary to operational value.

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

### Headscale integration tests

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
- Controller deployment package.

Installation tests must confirm that uninstall removes the application while preserving or removing data according to an explicit user choice.

### End-to-end functional test

The first required end-to-end scenario is:

1. Start clean controller persistence.
2. Create an enrollment.
3. Start a real test agent.
4. Complete application enrollment.
5. Register capabilities.
6. Submit real or controlled collector telemetry.
7. Read current state from the client-facing API.
8. Stop the controller while the agent produces additional samples.
9. Restart the controller and allow the agent to resume.
10. Query history and prove samples are ordered, identified, and not duplicated.
11. Restart again and prove node identity and history persist.

This test is a release gate for the functional foundation.

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

The accepted stack is .NET 10. The repository currently contains the domain and generated-contract libraries, the SQLite enrollment persistence boundary, the ASP.NET Core controller, and their tests. Commands for projects that do not exist yet remain canonical future targets and are identified below.

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

Real Headscale integration tests:

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

The script currently restores locked dependencies, builds with warnings as errors, runs every existing solution test tagged `Unit` or `Contract` (including update verification and Android core), runs real SQLite enrollment, controller telemetry, and agent-spool tests, runs controller enrollment plus real loopback HTTP/2 gRPC telemetry tests, runs the Windows collector integration test when the host is Windows, and verifies formatting. It explicitly reports cross-process, Headscale, Android device/instrumentation, Windows service lifecycle, Fedora, and Ubuntu suites as unavailable. The workload-specific Android APK command above remains a separate platform build. Add each missing suite when its project is created; verification must never silently skip a required suite.

The SQLite enrollment suite uses a real temporary database file with WAL enabled. It covers restart durability, expiration, cancellation, wrong-device intent, wrong signature, replay rejection, concurrent completion from separate store instances, and absence of raw, hexadecimal, or base64 enrollment secrets in database/WAL files. The telemetry case proves stream-challenge replay rejection, durable sequence acknowledgement, identical retry, conflicting retry rejection, latest-metric reads, and restart. The controller suite includes the enrollment service boundary plus a production Kestrel loopback HTTP/2 gRPC path covering authenticated spool upload, owner-gated reads, controller restart, duplicate retry, wrong-key rejection, cross-stream proof rejection, and safe error redaction. It is a real socket/protocol test in the test process; separate controller/agent process testing remains required.

The agent-spool suite uses a real temporary SQLite WAL file and production serialization. It covers restart durability, monotonic sequence continuation, cumulative prefix deletion, capacity refusal without eviction, refusal to violate controller batch size, and identical retry after upload failure. The Windows collector test calls production operating-system counters and verifies CPU, memory, storage, network-quality semantics, host identity, and boot evidence. Fedora and Ubuntu parser unit tests do not replace platform-local integration or packaging tests.

The update suite signs real manifest bytes with an ephemeral P-256 test key and calls the production verifier and artifact reader. It covers trusted-signature acceptance, wrong-key rejection, downgrade and reinstall rejection, compatibility floors, platform and architecture mismatch, canonical version parsing, and file size and SHA-256 mismatch. The GitHub release workflow, protected environment, generated archives, platform installation, rollback, and Android signing are not verified by those unit tests.

The Android core suite calls the production URI policy, one-time enrollment parsing, proof-request builder, battery normalization, network/VPN summary, and DER-to-P1363 conversion. A real P-256 key signs the canonical enrollment payload and the generated Protobuf request is verified through the controller-compatible P1363 path. These tests do not exercise Android Keystore, lifecycle, network permissions, device APIs, or controller connectivity; those require emulator and physical-device evidence.

Do not leave multiple undocumented ways to run the same suite. Package and platform jobs may wrap these commands, but the root verification script is authoritative.

## Completion evidence

A handoff must report the literal commands run, whether each succeeded, the environment used, and any suite not run. “Tests added” is not evidence that tests passed.
