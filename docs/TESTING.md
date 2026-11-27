# Testing Strategy

## Purpose

Tests exist to protect behavior required for Monitor App to enroll devices, collect truthful telemetry, preserve history, survive failure, and operate on Android, Windows, Fedora, and Ubuntu. Test count and coverage percentage are secondary to operational value.

The confirmed target is now an equal-peer mesh. Controller/enrollment tests below remain evidence for reusable legacy components, not proof of the target architecture. New work must prioritize the peer gates in the next section.

## Decentralized peer migration gates

- **Pairing contract:** two peers introduced by address complete bilateral application-key proof; address/tailnet membership alone fails; wrong key, substitution, replay, expiration, revocation, and incompatible versions fail closed.
- **Real two-peer integration:** start two production peer servers with separate file-backed SQLite stores, pair them, exchange origin-owned observations, disconnect, restart both, reconnect, and prove eventual convergence, stable identity, ordering, and no duplicate/conflicting sequence acceptance.
- **Local query:** each peer reads its own and replicated records with explicit origin, source time, ingest time, unit, quality, and freshness; offline cache remains distinguishable from live state.
- **History and reconstruction:** real-SQLite and loopback-HTTP tests cover half-open windows, 31-day/5000-row bounds, truncation with cursor paging across tied timestamps, ordering, decoded value/unit/quality, unavailable and permission-denied states never becoming zero, as-of record selection with age, lookback, and the schema-3 index upgrade.
- **Observed devices:** real-SQLite tests preserve stable peripheral identity, typed attachment evidence, and current plus historical collector ownership across host reassignment; IP-only claims cannot merge devices as strong identity, and assignments must agree with an explicitly provisioned adapter's entity and peer. Windows adapter tests cover the provisional Yamaha AG06MK2's configured identity/collector, driver evidence, observed time, and explicit unavailable/permission/error quality. The real connected device was inspected through the same fixed Windows PnP properties, but unplug/replug and denied-permission hardware cases remain simulated. Failover remains unavailable until the second collector is explicitly provisioned.
- **LAN topology:** reconcile reports from multiple peers using strong switch identity rather than IP; retain source/evidence/quality; distinguish reported from inferred links; keep physical LAN and Tailscale overlay separate.
- **Capability compatibility:** versioned namespaced modules register entities, observations, relationships, sampling/retention, categories, privacy/cost, and inspector descriptors. Unknown compatible modules receive a truthful generated fallback; incompatible modules fail explicitly.
- **Universal inventory contract:** actual relationships cannot reference design-only objects; design objects and relationships share a named project/scenario revision; product, offer, physical-instance, and design-placeholder identities remain distinct; the first cable slice preserves two connector ends, directionality, and unit-bearing nominal/measured length.
- **Privacy enforcement:** registration rejects prohibited packet/flow/identity/content observations while permitting declared aggregate operational network metrics. Adapter credentials never serialize into replicated records.
- **Defensive security:** the capability cannot start without separate enablement; findings require evidence/confidence; deeper flow/packet-derived modes require their own permission and bounded retention.

No platform is supported until its peer service lifecycle, protected identity storage, pairing/reconnect, local persistence/query, update, and required collector behavior pass on that named platform.

## Expanded scope gates

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
- Retention deletion boundaries (tiered retention: rollup aggregation with states kept as counts, idempotent re-run, pinned windows, transition/event retention, replication hold and hard cap, pruned-range replica outcome, hourly history auto-switch, schema v3 to v4 migration on a real file; `PeerRetentionTests`, `PeerStoreTests`). Receiver-side `RANGE_PRUNED`: `RangePrunedReplicationTests` (origin and replica in separate SQLite files; pull and push; durable across reopen; untrusted/relayed/gap-hiding ranges rejected) and the two-node gRPC theory `PeerNodeRuntimeTests.ReplicaMarksPrunedRangeAbsentAndConvergesWhetherTheOriginOrTheReplicaDials`. Node scheduling: `NodeBackgroundTasksTests` (manual `TimeProvider`: startup run, interval runs, failure survival, hourly retention pruning with rollups, default intervals/policy). Pin API: `LocalApiIncidentTests`.
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

### Live session tests

Run on real loopback HTTP/2 gRPC and file-backed SQLite with generous (30 s) condition waits, never fixed sleeps: two nodes and phone-desktop streaming records both ways over a held-open session (live latency is measured and printed; about 25-170 ms on loopback), killing the server mid-session then convergence without duplicates and live delivery after reconnect, interest switching the real `NodeCollectionScheduler` between 10 s and 1 s with withdrawal, lease-expiry and revocation reverting it, unknown and revoked peers refused by the production coordinator, SSE `/api/v1/live` emission and headers, `PeerLiveFeed` publishing only new commits, `LiveInterestRegistry` bounds, and the in-flight window and gap rewind of `PeerLiveSession`. Not covered: real Android devices, mobile network changes, Doze, and real Tailscale links.

### Phone pairing tests

- `PairingShortCodeTests` (Core): normalization, single use, expiry, bounded live codes, global rate-limit lockout. `PeerPairingCodeClientTests` (Transport): a rogue listener without the code and replayed proofs are refused; public/wildcard addresses and malformed codes are rejected. `PeerContractTests`: unary additive lookup shape. `LocalApiPhonePairingTests`: the QR SVG is decoded with ZXing.Net (test project only) back to the exact payload, quiet zone, CSP/nonce with no inline script or style, expiry, loopback/wildcard message, configured-address enforcement, CSRF, pending phone name and fingerprint. `MobilePairingCodeTests`: real loopback gRPC address+code pairing, then desktop approval and persisted bilateral trust; single use; wrong/expired code refusal without leaking identity; rate limiting.

### Remote control tests

`tests/MonitorApp.Peer.Mobile.Tests/ControlTests.cs` drives real phone runtimes against a real desktop host over loopback gRPC with file-backed SQLite, the production pairing coordinator, alert store, collection scheduler and loopback API; only the biometric prompt is a scripted software `IUserPresenceSigner`. Covered: authorized normal commands apply and are audited without a prompt; expired, not-yet-valid, tampered, wrong-target, wrong-session, unknown-issuer, revoked, replayed, reused-id and reused-nonce commands are refused and audited; idempotent resend returns the stored result without re-applying; pairing approval is refused without an approved presence key, with a missing signature, and with another key's signature, and succeeds (completing the newcomer's pairing) with the enrolled key; elevated pause/mute tiers; presence-key enrollment stays pending until local approval (including over the CSRF-protected API) and rotation needs re-approval; grants restrict commands and pending-pairing visibility; pause really stops scheduled collection and resume restarts it; a muted rule is not delivered and arrives after unmute; the audit table rejects UPDATE/DELETE. `AlertMuteTests` and `MonitoringControlStateTests` cover the store and state units. The Android Keystore/BiometricPrompt signer is compile-only in CI and unverified on a device.

```text
dotnet test tests/MonitorApp.Peer.Mobile.Tests/MonitorApp.Peer.Mobile.Tests.csproj -c Release --filter FullyQualifiedName~ControlTests
```

### Alert tests

`tests/MonitorApp.Peer.Alerts.Tests` runs rule evaluation against real SQLite files and the production ingestion bridge (determinism, idempotency across evaluator restart, episode start/resolve, sustained windows, peer-silent, single-owner rule on a replicated copy), outbox durability across store reopen, expiry, recipient isolation, and trust gating. `tests/MonitorApp.Peer.Mobile.Tests/MobileAlertTests.cs` `LiveSessionTests.DesktopAlertReachesTheConnectedPhoneInboxWithinTwoSecondsAndIsAcknowledged` measures desktop alert to phone inbox latency on a held-open session with real runtimes (asserts < 2 s; the 30 s safety-net pull cannot satisfy it); `AlertRuleTests` also cover hold-duration flapping and the re-fire cooldown. `MobileAlertTests.cs` covers desktop raise to phone persistence to acknowledgement over real loopback gRPC, restart re-notification, and revoked/other-recipient isolation. `tests/MonitorApp.Contracts.Tests/AlertContractTests.cs` covers field numbers, unknown-field tolerance, and validator limits. Android notification posting needs device or emulator evidence that does not exist yet.

```text
dotnet test tests/MonitorApp.Peer.Alerts.Tests/MonitorApp.Peer.Alerts.Tests.csproj -c Release
```

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

The peer node tests (restart-stable identity, 0600/0700 key storage and unsafe-permission refusal tagged `Category=Linux`, two-node pairing, and offline convergence) run on Linux in an Ubuntu 24.04 container; that is Linux engineering-preview evidence, not Ubuntu or Fedora support. A Linux container can test portable Linux collection code but does not prove RPM installation, SELinux behavior, `systemd`, or a Fedora release claim. The same distinction applies to Ubuntu packaging and Windows services.

### LAN self-observation tests

`tests/MonitorApp.Peer.Collection.Tests` cover route/VLAN/MAC parsers, interface classification (overlay/virtual/bridge/loopback non-physical), a fixture sysfs tree, a run against this host's real `/sys/class/net` (Linux), a real-SQLite persistence round trip, and reconciliation that IP-only matches never hard-merge. `SelfLanScheduleTests` cover link-observation conversion (unknown values are `Unavailable`) and `ValidTo` handling (vanished, unreadable host, reappearing, other observers untouched); `NodeBackgroundTasksTests` run the scheduled collection through a real node store. Windows classification is unit-tested from enum/name inputs only; the real Windows collector is unverified on hardware. Contract rules are in `MonitorApp.Contracts.Tests`.

### Preview and update-agent tests

Desktop-local presence (T045): `LocalApiPresenceTests` (real loopback host: unenrolled/enrolled/required/off policy, proof bound to action, subject, key and lifetime, replay, TOFU enrollment with proof of possession, removal, RSA keys, update-version binding, persistence, corrupt store fails closed, antiforgery on challenges); `LocalPresenceTests` in `MonitorApp.Client.Core.Tests` (real `LocalApiClient` and `SoftwareLocalPresenceProvider` with a key file against the real host, cancellation and unavailability leave state unchanged, gated update/grant/enrollment approvals; `Category=Linux` tests run the real `FprintdUserPresence` process boundary against fake `fprintd-verify` scripts including timeout kill and writable-tool refusal). Only the platform biometric prompt is stubbed. Not run: Windows Hello (`MonitorApp.Client.Presence.Windows` compiles on Linux only), real fprintd and fingerprint hardware.

`NodeUpdateAgentTests` (fake fetcher/downloader/applier; real signatures): approve versus auto policy, no re-apply, failed version not retried, untrusted key / bad hash / older version never staged or applied, health marker written at start, disabled-when-unconfigured, policy parsing. `ApplyHelperTests`: root helper installs only after signed-manifest, digest and version checks and records the result; tampered package, version mismatch and missing key fail closed. `PreviewManifestTests`: multi-artifact signed manifest round trip, tampering, numeric ordering of `0.1.N`. `LocalApiUpdateTests`: `GET/POST /api/v1/update` with antiforgery. Manually run: `eng/packaging/linux/build-packages.sh` (deb with update key, helper units, seed script) and `eng/release/MonitorApp.ReleaseTool create` over `.deb/.rpm/.apk/.zip/.tar.gz` names. Not verified: the workflow on GitHub, the Fedora rpm, systemd path-unit/helper on a real host, Android signing, Windows scripts, `gh` draft-release handling.

### Packaging tests

Before platform support is claimed, test a clean install, upgrade, restart, and uninstall for:

- Windows service package.
- Fedora RPM.
- Ubuntu DEB.
- Android APK or application bundle path.
- Peer service and local data package on every applicable platform.

Installation tests must confirm that uninstall removes the application while preserving or removing data according to an explicit user choice.

Current service-packaging evidence (Ubuntu 24.04 container, no systemd PID 1, no SELinux, no rpmbuild, no Windows):

```bash
eng/packaging/linux/build-packages.sh 0.1.0-preview     # about 1-1.5 min; builds artifacts/packages/*.deb, RPM only if rpmbuild exists
dpkg-deb -I artifacts/packages/monitor-app_0.1.0-preview_amd64.deb && dpkg-deb -c artifacts/packages/monitor-app_0.1.0-preview_amd64.deb
dpkg -i artifacts/packages/monitor-app_0.1.0-preview_amd64.deb   # creates monitor-app user and 0700 /var/lib/monitor-app
runuser -u monitor-app -- env MONITOR_APP_DATA_DIR=/var/lib/monitor-app MONITOR_APP_PEER_PORT=45431 MONITOR_APP_ADMIN_PORT=45432 /opt/monitor-app/MonitorApp.Peer.Node
dpkg -r monitor-app      # keeps /var/lib/monitor-app
dpkg --purge monitor-app # removes data, /etc/monitor-app, and the user
```

Install smoke (`.github/workflows/install-smoke.yml`, on pull requests and manually): three scenario jobs that install the real packaged builds. `ubuntu` (real systemd): `apt install` the `.deb`, enable/start, wait for `/api/runtime` and valued points in `/api/v1/state` on loopback port 55432, `systemctl restart` keeps the peer id and identity file, print `systemd-analyze security`, in-place update through the node's own update agent (local HTTPS release server with a test CA in the system store, signed 0.9.2 manifest staged with the right SHA-256, approval over the antiforgery local API, root helper installs it, health marker, peer id and data unchanged) and a rollback case (a 0.9.3 package that installs but whose node reports 0.9.2 must be rolled back to the retained 0.9.2; the helper must log `RolledBack` and the API must report `RolledBack`; a known risk is that the restarted node reconciles its state before the helper writes the result, leaving `Failed`, which this step would then expose), `apt install` a newer deb keeps identity and history sequence, `apt remove` keeps `/var/lib/monitor-app` and the user, `apt purge` removes data, config and user. `fedora` (container, no systemd): `rpm -i`, check files/user/unit, run the node as `monitor-app` for one collection and API check, `rpm -U`, `rpm -e`. `windows`: the preview zip through `Install-MonitorApp.ps1`, service running as `NT SERVICE\MonitorApp`, `identity.dpapi`, readings, restart keeps peer id, side-by-side `current` symlink, `MonitorAppUpdate` SYSTEM task, uninstall then `-Purge`. Scenarios are deliberately broad; each step logs and failures dump `journalctl`, service/event-log and node-log tails. Still not covered: Fedora with systemd and SELinux, Windows upgrade and in-place update apply, the update helper on rpm/Windows. Status: written, not yet run on GitHub.

`NodeServiceHostTests` (runs on Linux and Windows) covers service-mode option resolution and the real Generic Host: start, loopback admin answers, stop, restart keeps identity, startup failure surfaces. Not run: `systemctl` lifecycle and `Type=notify`, hardening effects, DEB upgrade, RPM, Fedora/SELinux, Windows service install, DPAPI under a virtual service account, ACLs, recovery actions. The DEB is not built in `eng/verify.ps1` (too slow); run the command above manually.

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

The accepted stack is .NET 10. The repository currently contains the domain and generated-contract libraries, legacy controller components, and peer core, persistence, application, replication, collection, synchronization, gRPC-host, local-API, and Windows-node projects with tests. Commands for projects that do not exist yet remain canonical future targets and are identified below.

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

This suite includes capability-module compatibility, canonical catalog metadata, deterministic purpose-built-versus-fallback inspector selection, credential-free schema checks, ordinary-versus-defensive network-data policy, actual/design separation, distinct product/offer/instance identity, typed relationships, and the connector/length cable contract. The peer-service suite adds real-SQLite capability registration and advertisement coverage. Universal inventory persistence, promotion workflows, matching, and a client remain future gates.

Standalone design-planning tests:

```powershell
dotnet test tests/MonitorApp.Planning.Tests/MonitorApp.Planning.Tests.csproj -c Release --no-restore
```

These unit tests exercise multi-standard rack and nested-subrack placement, panel projection, pedalboard clearances, evidence-aware physical findings, audio/MIDI/CV and pedal-power compatibility, connector/protocol/power/adapter checks, task dependencies, and project/product/offer separation. They do not verify installed hardware, authoritative inventory persistence, or client integration.

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
# Windows runs everything; Linux runs everything except Category=Windows (DPAPI and Yamaha adapter tests):
dotnet test tests/MonitorApp.Peer.Node.Tests/MonitorApp.Peer.Node.Tests.csproj -c Release --no-build --filter "Category!=Windows"
```

Signed update-manifest policy, artifact-integrity, staging/state-machine, apply/rollback tests (includes the `Integration` dpkg sandbox test, which returns early when `/usr/bin/dpkg` and `dpkg-deb` are absent; Windows service control and rpm are not exercised):

```powershell
dotnet test tests/MonitorApp.Update.Tests/MonitorApp.Update.Tests.csproj -c Release --no-build 
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

Android peer runtime (portable, Linux/Windows CI; real loopback HTTP/2 gRPC, two file-backed SQLite stores, production desktop host):

```powershell
dotnet test tests/MonitorApp.Peer.Mobile.Tests/MonitorApp.Peer.Mobile.Tests.csproj -c Release
dotnet test tests/MonitorApp.Peer.Transport.Tests/MonitorApp.Peer.Transport.Tests.csproj -c Release
```

These cover phone-initiated pairing with desktop approval, offline collection, reconnect convergence without duplicates, revocation refusal, wrong-pinned-fingerprint rejection, Android reading units/quality/source time and explicit unavailable state, and the pairing-invitation codec/policy. The phone's Keystore identity, foreground service, and APK are not covered by these tests. The Android app was restructured after the last verified APK build and has not been rebuilt.

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

The script currently restores locked dependencies, builds with warnings as errors, runs every existing solution test tagged `Unit` or `Contract`, runs the real SQLite enrollment/controller telemetry, agent-spool, peer-store, pairing-application, and peer-replication suites, runs controller and peer-host/outbound-transport real loopback HTTP/2 gRPC tests, runs the portable mobile peer runtime suite (real loopback gRPC phone-to-desktop pairing and bidirectional convergence), runs the local administration HTTP suite, and verifies formatting. On Windows it also runs the collector plus the composed node/DPAPI tests, including bilateral web pairing and the separate-store offline-collection/restart/convergence scenario. It explicitly reports the cross-process legacy path, Headscale, Android device/instrumentation, Windows service lifecycle, Fedora, and Ubuntu suites as unavailable. The workload-specific Android APK command above remains a separate platform build. Add each missing suite when its project is created; verification must never silently skip a required suite.

The peer foundation suites currently cover generated contract shape and validation; real P-256 bilateral proof and transcript substitution/replay/expiry behavior; compatibility and duplicate-session selection; durable approval, challenge, and replay state; a real file-backed SQLite peer store; signed bounded replication; real Windows-loopback inbound/outbound gRPC; loopback-only local administration with CSRF/redaction/private-address validation; and a composed Windows node with restart-stable DPAPI identity. The composed-node suite starts two fresh nodes, submits an invitation through one local web API, approves it through the other, completes bilateral proof over production gRPC, and verifies active trust in both separate SQLite databases. These tests do not prove packaging/service lifecycle, real TLS/Tailscale deployment, non-Windows protected key storage, collection through the peer node, scheduled reconnect, or two-peer history convergence after outage.

The SQLite enrollment suite uses a real temporary database file with WAL enabled. It covers restart durability, expiration, cancellation, wrong-device intent, wrong signature, replay rejection, concurrent completion from separate store instances, and absence of raw, hexadecimal, or base64 enrollment secrets in database/WAL files. The telemetry case proves stream-challenge replay rejection, durable sequence acknowledgement, identical retry, conflicting retry rejection, latest-metric reads, and restart. The controller suite includes the enrollment service boundary plus a production Kestrel loopback HTTP/2 gRPC path covering authenticated spool upload, owner-gated reads, controller restart, duplicate retry, wrong-key rejection, cross-stream proof rejection, and safe error redaction. It is a real socket/protocol test in the test process; separate controller/agent process testing remains required.

The agent-spool suite uses a real temporary SQLite WAL file and production serialization. It covers restart durability, monotonic sequence continuation, cumulative prefix deletion, capacity refusal without eviction, refusal to violate controller batch size, and identical retry after upload failure. The Windows collector test calls production operating-system counters and verifies CPU, memory, storage, network-quality semantics, host identity, and boot evidence. Fedora and Ubuntu parser unit tests do not replace platform-local integration or packaging tests.

The update suite signs real manifest bytes with an ephemeral P-256 test key and calls the production verifier and artifact reader. It covers trusted-signature acceptance, wrong-key rejection, downgrade and reinstall rejection, compatibility floors, platform and architecture mismatch, canonical version parsing, and file size and SHA-256 mismatch. The GitHub release workflow, protected environment, generated archives, platform installation, rollback, and Android signing are not verified by those unit tests.

The update pipeline tests add real files and state, ephemeral-key signed manifests with fake network seams, `HttpClient` handler stubs for transport bounds, scripted process/health fakes for rollback orchestration, a real `dpkg-deb` build and `dpkg --root/--admindir` sandbox upgrade, unhealthy-upgrade rollback, and retained-package check, and real temp-directory Windows side-by-side swaps including a zip-slip entry. Windows `sc.exe` control is verified only for command shape. `WindowsApplyHelperTests` (Peer.Node) run the real Windows apply helper on temp dirs with a signed manifest, zip and directory symlinks and a fake service controller (healthy switch, rollback, tampered zip, staged path outside the update directory, non-versioned root) plus the `HandoffApplier` request/result handshake; `NodeUpdateAgentTests` cover zip apply gating on the installed-helper marker. `Category=Windows` tests run real `sc.exe`/`schtasks.exe` failure paths and the symlink swap on Windows only. Not verified: registering the `MonitorAppUpdate` task, task SDDL, starting it from the service account, or a real service restart through `current`.

Self-contained release packaging performs runtime-specific restores into ignored staging lockfiles so the committed NuGet locks remain platform neutral. The release workflow runs `git diff --exit-code` after packaging and fails if any tracked file changes. Local packaging verification must likewise compare the tracked tree before and after archive creation; successful archive output alone is not sufficient evidence.

The Android core suite calls the production URI policy, one-time enrollment parsing, proof-request builder, battery normalization, network/VPN summary, and DER-to-P1363 conversion. A real P-256 key signs the canonical enrollment payload and the generated Protobuf request is verified through the controller-compatible P1363 path. These tests do not exercise Android Keystore, lifecycle, network permissions, device APIs, or controller connectivity; those require emulator and physical-device evidence.

Do not leave multiple undocumented ways to run the same suite. Package and platform jobs may wrap these commands, but the root verification script is authoritative.

## Desktop client tests

`tests/MonitorApp.Client.Core.Tests` starts the real `LocalApiHost` over a file-backed SQLite store seeded through the production `PeerStore`, and drives the typed client and view models against it (round trip, cursor paging and page cap, unavailable values, validation, antiforgery, revoke confirmation, unreachable node, polling and push updates). `tests/MonitorApp.Client.Desktop.Tests` is an Avalonia.Headless suite: it binds every section to a real node and checks the shared shell at phone width (400x800: bottom bar, menu overlay, every section reachable) and desktop width (side navigation), plus resizing between them. `tests/MonitorApp.Client.Mobile.Tests` runs `InProcessPeerDataSource` against a real `MobilePeerRuntime` and a real desktop peer over loopback gRPC and SQLite (replicated state, history paging, as-of, catalog, push `StateChanged`, viewing interest reaching the desktop, alerts, typed-code and invitation pairing). The Dive has one scenario test in each suite. `DiveViewModelTests` (Core) covers tile order, wording, root cause, navigation, selection sync, actions, approvals and history. `DiveViewTests` (Desktop, headless) covers the responsive grid, real taps, Escape, the back chip, the recorded band, and that the zoom comes to rest. `ShellScreenshots` (Category=Manual, built with `-p:ManualShots=true`) renders Dive and outline PNGs for design review. None verifies visual appearance or Android; the Android head compiles only in the CI `android-build` job. Run `dotnet test` on each project (all are in `eng/verify.ps1`).

## Monitor App 2.x settings requests

From `lite/app`, run `flutter test test/request_activity_test.dart`. The
widget test reads actual request and response files through `monitor_core`
and checks local request scope, waiting, expiration, failure, and the host
response at phone width.

## Completion evidence

A handoff must report the literal commands run, whether each succeeded, the environment used, and any suite not run. “Tests added” is not evidence that tests passed.
