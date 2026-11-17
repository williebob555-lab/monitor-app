# Change Log

This file records completed user-visible, operational, architectural, packaging, and documentation changes. Keep unreleased entries concise and move short-term execution detail to `TODO.md`.

## Unreleased

### Added

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
