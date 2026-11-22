# Change Log

This file records completed user-visible, operational, architectural, packaging, and documentation changes. Keep unreleased entries concise and move short-term execution detail to `TODO.md`.

## Unreleased

### Changed

- Peer node now runs inside the .NET Generic Host (`PeerNodeHostedService`) with `UseWindowsService`/`UseSystemd` integration (systemd `Type=notify`, journald-friendly logs, graceful stop disposes the runtime, non-zero exit on startup failure; console run unchanged). Service mode defaults the data directory to `STATE_DIRECTORY`/`/var/lib/monitor-app` (Linux) or `%ProgramData%\MonitorApp` (Windows); `MONITOR_APP_DATA_DIR`, `MONITOR_APP_PEER_BIND`, `MONITOR_APP_PEER_PORT`, `MONITOR_APP_ADMIN_PORT` environment variables sit between arguments and defaults. Peer bind stays loopback unless set explicitly. Added Linux packaging (`eng/packaging/linux`: hardened systemd unit, sysusers, `/etc/monitor-app/node.conf`, DEB build script, RPM spec) and Windows service install/uninstall scripts (`eng/packaging/windows`). Adds `Microsoft.Extensions.Hosting.Systemd`/`.WindowsServices` 10.0.0.
- Added bounded history query (UTC window max 31 days, max 5000 rows, explicit truncation flag plus keyset cursor), as-of state reconstruction with age and freshness within a lookback, and a subject/signal catalog as `PeerHistoryQueryService` with versioned `/api/v1/history`, `/api/v1/state`, `/api/v1/catalog` endpoints that report a `resolution` (`raw` today) for later rollup tiers. Unavailable, denied, error, and undecodable observations are returned as states without values. SQLite schema version 3 adds an `observed_at` index. The loopback page gained minimal current-state, history, and as-of views and nonce-bound CSP/nosniff headers.
- Moved `IPeerAdvertisementProvider` and `PeerCapabilityRegistry` from Peer.Service to Peer.Replication (namespace unchanged) so Peer.Sync no longer references ASP.NET Core.
- Replaced the mandatory full-document preflight and default full-suite verification for every edit with a risk-based workflow: always inspect the index, todo, working tree, and governing decisions; read complete documents and run broad tests only when the change scope requires them.
- Expanded the documented product scope to a universal monitoring, inventory, and design model with an explicit boundary between actual/owned/running state and named plans. Added connector-and-length-centered cable management, physical/audio/RF/electrical/thermal constraints, sourcing and price history, deterministic automation and notifications, optional distributed local AI, and a dependency-ordered roadmap. These are documented plans, not implemented features.
- Replaced the controller/agent and automatic self-hosted Headscale direction with a confirmed decentralized equal-peer mesh. Tailscale now supplies user-managed reachability only; bilateral application pairing establishes trust.
- Defined origin-owned peer replication, local storage/query on every peer, explicit freshness/offline cache, and eventual convergence without global consensus.
- Added observed peripherals and LAN infrastructure as typed entities collected through purpose-built adapters with stable identity, explicit collector ownership, local credentials, and evidence-aware topology reconciliation.
- Added versioned capability modules, a categorized observation catalog, broad truthful measurement rules, and purpose-built/fallback inspector contracts.
- Established the aggregate-operational network measurement boundary and separate opt-in defensive security capability. Ordinary packet/content/communication-identity collection remains prohibited.
- Reframed the current controller alpha as reusable migration evidence rather than the target deployment.

### Added
### Added

- Physical-LAN self-observation: typed LAN entity/relationship kinds with validation in Contracts (`LanTopology`), and `MonitorApp.Peer.Collection.Lan` with a Linux (sysfs/procfs) and Windows (NetworkInformation) self-interface collector that classifies Tailscale/WireGuard/docker/bridge/loopback as non-physical, records link state, speed/duplex, MTU, own MAC (physical only), Wi-Fi vs Ethernet, VLAN sub-interfaces, own subnets and default-gateway IP, persists through existing PeerStore APIs (no schema change), and links per-peer segments only as inferred "probable same segment" edges. Unsupported platforms report truthful unsupported state. Not yet registered by the peer node.

- Update pipeline in `MonitorApp.Update`: HTTPS manifest fetch, signed verification, size/hash-verified atomic staging, persisted update state machine with audit log, explicit-approval apply, anti-rollback with an audited per-version downgrade authorization, and `IUpdateApplier` strategies for Linux dpkg/rpm (retained last-known-good, health-gated rollback), Windows side-by-side versioned directories with a swapped `current` pointer (execution unverified), and a portable Android verified-APK offer. Not yet wired into the node/service or any installer.
- Linux engineering preview of the peer node, verified in an Ubuntu 24.04 container only: `MonitorApp.Peer.Node` now targets `net10.0`, selects `PlatformDetector` for the collector (unsupported distributions fail at startup), stores the P-256 identity in `identity.p256` (directory 0700, file 0600 created atomically, load refuses group/other-accessible, symlinked, or foreign-owned files), and defaults its data directory to `$XDG_DATA_HOME/monitor-app` or `~/.local/share/monitor-app`. Runtime pairing and convergence tests, plus Linux identity tests, run on Linux in `eng/verify.ps1`. Windows DPAPI behavior is unchanged. Fedora, packaging, `systemd`, and SELinux are unproven.
- `MonitorApp.Peer.Mobile`: portable, lifecycle-agnostic Android peer runtime (outbound-only, injected identity, Android battery/network readings through the shared signed-origin ingestion path with an `monitor.android.device` capability declaration, explicit pairing, reconnecting phone-initiated bidirectional sync) with real loopback gRPC/SQLite integration tests; `PeerPairingInvitation` versioned pairing payload; `PeerGrpcConnector.SyncOnceAsync`.
- Android app is now a foreground-service peer shell (health summary, pairing by pasted invitation, empty alert list); the controller enrollment path and receipt store were removed from the app. Build and device behavior are unverified (no Android SDK available in the verification environment).
- Comprehensive product and architecture specification in `Monitor App Design Specification.txt`.
- Repository-wide preflight and documentation rules in `AGENTS.md`.
- Initial architecture, development, testing, security, and decision documentation.
- Short-term project task tracking in `TODO.md`.
- Android, Windows, Fedora, and Ubuntu as explicit requested device targets.
- Functionality-first delivery and meaningful executable testing as confirmed project constraints.
- Root .NET 10 solution with an SDK feature-band pin, centralized package versions, committed package locks, warnings-as-errors, recommended analyzers, and a single verification script.
- Shared domain freshness model that distinguishes Unknown, Fresh, Stale, Offline, Unavailable, and valid numeric zero from explicit UTC source and contact timestamps.
- Controlled-clock boundary tests for observation quality, stale and offline thresholds, invalid timestamp order, future timestamps, and policy configuration.
- Versioned `monitor.v1` Protocol Buffer contracts and generated C# types for common platform/capability concepts, typed telemetry, application enrollment, and a bidirectional agent session.
- Contract validation and executable tests for valid zero preservation, unknown fields, Android representation, enrollment values, streaming shape, identity, timestamps, units, quality/value consistency, and contiguous sequences.
- An enrollment service contract with create, status, cancel, complete, device-challenge, and device-proof unary operations.
- Canonical domain-separated proof payloads and real ECDSA P-256/SHA-256 compatibility tests for enrollment and returning-device authentication.
- Shared Windows, Fedora, and Ubuntu agent core with explicit platform detection, host and boot identity, timestamped CPU, memory, storage, network, and collector-health observations.
- Bounded file-backed SQLite WAL telemetry spool with monotonic sequences, durable restart, cumulative acknowledgement, capacity refusal without silent eviction, and identical retry after failed upload.
- Minimal read-only agent executable that collects into the durable spool and uploads through an authenticated outbound gRPC stream when a controller URL and enrolled P-256 key path are configured.
- Agent unit, real SQLite persistence, and platform-local Windows collector tests in the canonical verification path.
- Runnable ASP.NET Core controller with a minimal health endpoint and owner-gated gRPC enrollment lifecycle.
- Versioned file-backed SQLite WAL schema for enrollments, durable device identities, single-use authentication challenges, and redacted security audit events.
- Real SQLite integration coverage for restart durability, expiry, cancellation, wrong-device intent, proof failure, concurrent single-use completion, replay rejection, and enrollment-secret absence from database/WAL files.
- Controller-boundary coverage for administrator authorization and credential-safe gRPC failures.
- Stream-bound P-256 agent authentication with fresh single-use challenges, durable SQLite metric ingestion, commit-before-ack responses, identical retry handling, and replay/conflict rejection.
- Owner-gated inventory and current-metric diagnostic endpoints plus real loopback HTTP/2 gRPC tests for upload, restart, reads, wrong-key rejection, and cross-stream proof rejection.
- Platform-independent signed update-manifest verification with channel, version, compatibility, target, size, and SHA-256 enforcement.
- Release tooling for protected P-256 key generation, deterministic version metadata, signed manifests, and self-contained controller and Windows/Fedora/Ubuntu agent alpha archives.
- GitHub Actions CI on Windows and Ubuntu plus an immutable-tag release workflow that refuses to overwrite existing releases and records the unimplemented Android signing gate.
- Release and update-key operational guidance in `docs/RELEASES.md`.
- Native .NET Android functional alpha with foreground device identity, battery/charging, validated network/VPN state, explicit freshness, HTTPS controller health, and application enrollment.
- Android Keystore P-256 proof signing, one-time enrollment-field clearing, private non-secret pairing-receipt storage, and platform-independent Android core coverage.
- Development-signed Android API 36 Release APK build path using the .NET Android workload and JDK 21.
- Root getting-started guidance and a guarded helper for creating and pushing a new private GitHub repository from a clean committed `main` branch.
- Versioned `monitor.v1` equal-peer contracts for discovery hints, bilateral pairing, authenticated sessions, capability exchange, origin range/batch replication, durable cursors/acknowledgements, and explicit compatibility failures.
- Platform-independent peer identity and pairing core with real ECDSA P-256 transcript-bound proofs, replay/expiry handling, protocol negotiation, deterministic duplicate-session selection, and defensive key-material copies.
- Versioned file-backed SQLite WAL peer store for local identity, trust/revocation, locators, pairing sessions, immutable local and replicated origin records, current queries, replication cursors/gaps, observed-device evidence, relationships, adapter health, capability catalog, and collector assignments.
- Generated-contract, cryptographic, and eight real file-backed peer-persistence tests in the root solution and verification path.
- Durable peer application coordination for explicit approval, bilateral pairing, reconnect authentication, restart-safe challenge replay rejection, trust/revocation, capability negotiation, and deterministic duplicate-session admission.
- Signed origin-replication coordination with trusted-origin validation, bounded range and batch handling, idempotent SQLite persistence, conflict rejection, durable cursor acknowledgement, and gap convergence.
- Fail-closed ASP.NET Core gRPC peer host with explicit private/loopback binding, message/work/time bounds, authenticated-session gating, safe compatibility/error responses, non-secret local diagnostics, and real loopback integration tests.
- Protocol 1.1 bilateral reconnect authentication now carries and verifies the server proof before the client sends its proof or any application operation; protocol 1.0 fails explicitly.
- Outbound peer connector with pinned identity, safe endpoint policy, bounded timeouts/backoff, capability/replication/heartbeat dispatch, and real production-host loopback tests.
- Loopback-only local administration page/API for identity, trust, pairing decisions, and stored observation metadata with CSRF-protected mutations and redacted output.
- Launchable Windows peer preview with CurrentUser-DPAPI-protected P-256 identity, shared local database, composed peer/admin hosts, and restart-stability tests.
- Structured protocol-1.1 fresh-pair approval states, identity-bound idempotent pairing IDs, one durable challenge, and production incoming pairing coordination with rejection/expiry/replay/revocation checks.
- Outbound fresh-pairing client and local invitation form that require explicit initiating/contacted-peer approval, pin the displayed remote identity, verify the server proof first, and persist trust only after completed confirmation.
- Real two-node browser/API plus gRPC integration coverage proving two fresh DPAPI identities and separate SQLite stores establish bilateral active trust.
- Peer-native collection and synchronization projects that convert system snapshots into signed local-origin records, advertise bounded capability/cursor state, pull remote ranges, push local batches, and durably retain acknowledgements.
- Persisted Windows preview reconnect scheduling from paired-peer locators, with bounded backoff and automatic authenticated sessions whenever the stored peer endpoint becomes reachable.
- A real Windows two-peer integration path that exchanges observations, disconnects both nodes, collects independently while the other node is offline, restarts, reconnects, and proves ordered byte/signature-equivalent convergence without duplicate sequences in separate SQLite stores.
- Production-host security coverage proving that a reachable but unpaired application identity receives only a non-retryable, credential-safe authorization error and no challenge or application operation.
- A versioned namespaced capability-module contract and canonical observation catalog covering entity/relationship types, signal value and temporal behavior, source, unit, freshness, importance, privacy, collection cost, sampling, retention, peer compatibility, and generated/purpose-built inspector descriptors.
- A production capability registry that validates and bounds declarations before SQLite persistence and advertisement, rejects ordinary communication-identity/content/packet/flow/query/per-process traffic categories, and requires separate enablement plus evidence/confidence for defensive-security modules.
- A validated built-in `monitor.system` declaration for the Windows preview's CPU, memory, storage, aggregate network counters, and collector-health observations.
- Root verification now reports the in-process Windows two-peer convergence coverage instead of listing that suite as unavailable.
- Deterministic inspector selection now chooses an installed applicable purpose-built inspector and otherwise returns the validated generated fallback without exposing mutable registry state.
- Added a versioned universal inventory contract and validator that separate actual state from named design revisions, keep product definitions, seller offers, physical instances, and design placeholders as distinct identities, validate typed relationship provenance/validity, and prevent actual relationships from silently consuming design-only objects.
- Added the first bounded cable contract slice with two typed connector ends, reversible/directional roles, and positive unit-bearing nominal and measured lengths; sourcing, automation, AI, persistence, matching, and UI workflows remain outside this slice.
- Strengthened observed-device persistence so IP/network-address claims cannot be strong identity, strong claims can reconcile reports independently of collection host, collector assignments must match their adapter's device and peer, and reassignment retains restart-stable ownership plus attachment history without replicating credential references.
- Added a provisional read-only Yamaha AG06MK2 Windows adapter probe using the connected device's Yamaha USB identity. It preserves configured app identity separately from port correlation data and reports driver/attachment evidence, freshness, unavailable, permission-denied, and safe error states without device controls.
- Added the self-contained Windows equal-peer preview zip to alpha release packaging; the existing `dev-0.1.0` GitHub pre-release contains only the older controller/agent generation and no current peer release has been tagged.
- Phone pairing UX: the local administration page has an "Add a phone" section that renders a 10-minute invitation as a server-side inline SVG QR code (QRCoder, no scripts or external requests) plus the copyable payload, offering only the node's configured private peer-listener address (a loopback or wildcard listener shows a Tailscale/private-bind message). A typed fallback code (10 Crockford base32 characters) is redeemed over the new additive `PeerPairingCodeService.Redeem` RPC; `MobilePeerRuntime.PairWithCodeAsync` is the phone side. Desktop approval of the phone's request is still required.
- Tiered retention foundation: schema v4 adds hourly rollups (`observation_rollups_hourly`), pinned incident windows (`pinned_incidents`) and pruned-range tombstones (`pruned_origin_ranges`); `PeerRetentionService.RunAsync` (not yet scheduled by the node) rolls up completed hours idempotently, prunes raw records older than 7 days except pinned windows, transition records within 5 years and locally originated records some active trusted peer has not acknowledged (30-day hard cap), and prunes rollups after 365 days. `/api/v1/history` gains `resolution=raw|hourly|auto` (default auto: hourly rollups when the window reaches beyond raw retention; additive `rollups` field). A replica requesting a pruned origin range now gets `OriginRangePrunedException` / new `PROTOCOL_ERROR_CODE_RANGE_PRUNED` with the resume sequence instead of a silent hole.

### Changed

- Accepted .NET 10 LTS and C# as the implementation baseline, with ASP.NET Core gRPC and Protocol Buffers, a shared .NET worker agent, and Avalonia reserved for the later cross-platform client UI.
- Selected file-backed SQLite in WAL mode for the first personal-scale persistence slice, subject to real durability tests and a benchmark revisit condition.
- Selected Docker Compose as the first controller deployment path while keeping Windows, Fedora, and Ubuntu agents as native packages.
- Replaced the testing-command placeholder with the canonical .NET, SQLite, Headscale, Android, and platform verification commands the repository scaffold must implement.
- Made locked restore, domain tests, Release build, and formatting verification executable; future suites are reported explicitly as unavailable.
- Expanded root verification to run every existing solution Unit and Contract test so newly added contract tests cannot be silently omitted.
- Expanded root verification with the durable agent-spool suite and Windows collector integration test when verification runs on Windows.
- Expanded root verification to run the real SQLite enrollment and controller-boundary suites.
- Updated Google Protobuf for gRPC compatibility and pinned a patched SQLite native library after restore rejected a vulnerable transitive version.
- Pinned GitHub Actions workflow dependencies to reviewed immutable commit hashes.
- Kept the workload-specific Android project outside the root solution while including Android core/tests so ordinary cross-platform CI verifies shared Android logic without requiring a mobile SDK.

### Fixed

- Restored platform-neutral NuGet lockfiles after the first private CI run exposed runtime-specific publishing residue; self-contained packaging now isolates runtime locks and fails if it changes any tracked file.
- Made the private-repository publishing helper tolerate expected missing-remote and missing-repository probes under Windows PowerShell 5 without weakening creation or visibility checks.
- Preserved last-contact evidence for unknown and unavailable metric observations so an expired device contact now produces Offline instead of masking disconnection behind metric quality.
- Added configurable future-clock-skew tolerance to freshness evaluation; accepted skew is clamped to zero age and excessive skew identifies the invalid timestamp field.
- Added regression coverage for offline precedence, UTC enforcement, NaN and infinity rejection, and future-skew boundaries while retaining valid numeric zero behavior.

### Security

- Established initial trust boundaries and enrollment requirements before implementation.
- Replaced reusable raw agent credentials in enrollment responses with device public-key proof, SHA-256 fingerprints, and short-lived signed authentication challenges.
- Added structural rejection of malformed or wrong-curve public keys, incorrect secret/challenge/signature/fingerprint sizes, invalid challenge windows, and safe-error secret echo.
- Enrollment secrets are persisted only as per-record salted HMAC-SHA-256 verifiers with a required controller pepper; completion and device challenges are atomically single-use.
- Owner enrollment operations require an administrator token, and controller startup fails closed when bootstrap secrets are missing or weak.
- Update eligibility now fails closed for invalid signatures, untrusted keys, downgrade/reinstall, incompatible versions, wrong targets, and size/hash mismatch; signing and Android-keystore files are excluded from source control.
- Android enrollment retains the private P-256 key inside Android Keystore and never persists the one-time enrollment secret or proof challenge.
- Short pairing codes are one-time, 10-minute, never transmitted (PBKDF2-derived lookup id; the desktop reply carries an HMAC proof keyed by the code), globally rate-limited, and refuse unknown/expired/used/limited attempts identically; see `docs/SECURITY.md` Phone pairing.
