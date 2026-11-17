# Architecture

## Status

This is the provisional functional architecture. Confirmed product requirements are identified separately from working technical choices. The first implementation stack, persistence engine, and controller deployment path are accepted in `DECISIONS.md`; component behavior remains subject to executable verification.

## Accepted implementation baseline

- Use C# on .NET 10 LTS for the controller, shared domain code, contracts, desktop and Linux agents, Android client code, and later experience clients.
- Host the controller with ASP.NET Core.
- Use standard gRPC over TLS and HTTP/2 with versioned Protocol Buffer contracts. Agent traffic may use bidirectional streaming; a future browser client must not weaken that transport merely to use gRPC-Web.
- Run one shared .NET worker agent implementation as a Windows Service or `systemd` service, with platform collectors behind explicit capability interfaces.
- Use Avalonia for the later shared Windows, Fedora, Ubuntu, and Android experience client. The first client remains a plain functional shell.
- Use SQLite in WAL mode for the first personal-scale controller persistence implementation.
- Deploy the first controller through Docker Compose. Windows, Fedora, and Ubuntu agents remain native packages and are not hidden inside the controller deployment.

## Confirmed constraints

- Target Android, Windows, Fedora, and Ubuntu devices.
- Use self-hosted Headscale for private-network coordination.
- Make setup approach download, install, and pair rather than manual VPN assembly.
- Preserve historical state and time travel as a core product capability.
- Keep purpose-built behavior ahead of arbitrary customization.
- Build functionality before advanced visuals and final UI polish.
- Keep playful visual manipulation separate from real operational actions.

## Functional system shape

```text
Experience clients
    |
    | application protocol with application identity
    v
Monitor App controller
    |- API and live stream
    |- enrollment service
    |- telemetry ingest
    |- topology and current-state engine
    |- history reconstruction
    |- action broker (later, separate permission domain)
    `- persistence
         ^
         | outbound agent sessions over the private network
         |
Endpoint agents and collectors
    |- Windows
    |- Fedora
    |- Ubuntu
    `- Android within platform limits

Headscale coordinates the private Tailscale-compatible network alongside this
application data flow. It is not the telemetry database or application identity
provider.
```

## Component responsibilities

### Shared domain contracts

Define stable, versioned representations for:

- Environment
- Node
- Capability
- Component
- Relationship
- Metric sample
- Event
- Snapshot
- Incident
- Enrollment
- Action and action result
- Freshness and quality

Contracts must distinguish unavailable, unknown, stale, offline, and valid zero values. All telemetry carries source time, ingestion time, unit when applicable, source identity, and quality.

The implemented `monitor.v1` wire boundary lives under `contracts/monitor/v1` and generates C# types in `MonitorApp.Contracts`. It defines an explicit `ProtocolVersion`; Windows, Fedora, Ubuntu, and Android platform values; capability state, observation quality, and metric unit enums; application enrollment and proof-of-possession messages; typed metric values; one unary `EnrollmentService`; and one bidirectional `AgentSession` gRPC stream. Typed metric values use a Protobuf `oneof`, so a valid numeric zero remains present and distinct from an unavailable observation with no value. Semantic validation rejects unspecified or unknown enum values, invalid timestamps and identities, malformed P-256 public keys, incorrect secret, challenge, signature, or fingerprint sizes, missing typed values for valid observations, values attached to unavailable observations, and noncontiguous telemetry batches.

The contract intentionally contains no Headscale types. Network enrollment remains behind its adapter and is composed with, but does not become, Monitor App application enrollment.

Application enrollment uses a 32-byte one-time enrollment secret plus a 32-byte proof challenge. The device creates and retains an ECDSA P-256 private key and sends only its SubjectPublicKeyInfo public key and a fixed-width 64-byte IEEE P1363 signature using SHA-256. The signed enrollment payload binds a domain label, enrollment id, challenge, and SHA-256 public-key fingerprint through the shared `ProofPayload` helper. Completion returns stable node and agent ids, the public-key fingerprint, and completion time; it never returns a reusable raw agent credential.

Later application authentication creates a new short-lived 32-byte device challenge. The agent signs a domain-separated payload binding agent id, challenge id, and challenge, and the controller verifies it against the enrolled public key. The controller implementation must make both enrollment and device challenges single-use, expiration-checked, rate-limited, and transactionally consumed; the contract library defines structure and canonical proof bytes but does not implement lifecycle persistence.

The first implemented domain behavior is the numeric observation freshness model in `MonitorApp.Domain`. It keeps measurement presence separate from quality, preserves numeric zero as a valid value, and classifies observations as Unknown, Fresh, Stale, Offline, or Unavailable. A valid observation uses an explicit UTC source timestamp and last-contact timestamp. Unknown and unavailable observations may retain UTC last-contact evidence even though they have no metric value. When that evidence exceeds the offline threshold, Offline takes precedence over metric quality; otherwise the observation remains Unknown or Unavailable.

The accepted time boundary keeps an observation Fresh through the exact stale threshold, Stale after that threshold through the exact offline threshold, and Offline only after the last-contact age exceeds the offline threshold. A configured nonnegative future-skew tolerance accepts modest device clock lead and clamps its age to zero. Timestamps beyond that tolerance fail with the offending field identified instead of silently producing a misleading state. Structurally invalid observations and non-finite numeric values also fail before classification.

### Controller

The controller is the self-hosted application authority. Its functional responsibilities are:

- Health and version reporting.
- Application authentication and device identity.
- Enrollment lifecycle.
- Agent capability registration.
- Telemetry validation and ingestion.
- Current-state projection.
- Inventory and typed relationships.
- Event and metric persistence.
- Snapshot creation and historical reconstruction.
- Read APIs and resumable live updates.
- Later, explicit action authorization and audit.

The controller must remain useful when a nonessential integration fails. It should distinguish its own failure from Headscale, private-network, agent, collector, and monitored-service failures.

The first controller implementation is an ASP.NET Core application. Its agent and client boundaries use generated C# types from versioned Protobuf definitions. Health and readiness endpoints may use ordinary HTTP, but telemetry and application state contracts remain versioned rather than ad hoc JSON.

The implemented controller exposes the six unary enrollment operations through ASP.NET Core gRPC and a minimal `/health` endpoint. Owner-side create, status, and cancel operations require a separate administrator token; the controller refuses to start when its administrator token or secret-verifier pepper is absent or too short. Public enrollment completion and device-proof operations are rate limited and have bounded identifiers, display names, and outstanding challenges.

Each `AgentSession` authenticates inside its own bidirectional stream. The controller issues a fresh challenge bound to the agent id, random session id, challenge id, and exact stream state; only the enrolled P-256 key can sign the domain-separated payload. The controller atomically consumes the challenge before returning `ControllerHello`. A proof copied to another stream has the wrong session/challenge context and fails, so no reusable bearer assertion or transport-affinity assumption is introduced.

### Endpoint agent

The endpoint agent is a long-running, least-privilege process for Windows, Fedora, and Ubuntu. It should:

- Establish an outbound authenticated session.
- Advertise platform, version, and capabilities.
- Collect a small initial set of system metrics.
- Preserve source timestamps.
- Buffer a bounded amount of telemetry during short disconnections.
- Resume without duplicating accepted samples.
- Report collector health and data freshness.
- Execute no operational action until the action permission model exists.

Platform collectors should implement a shared contract while reporting only capabilities they can support truthfully.

The agent is a .NET worker shared across Windows, Fedora, and Ubuntu. Service-host integration, collectors, installation, and lifecycle tests remain platform-specific. The shared runtime must not infer support from compilation alone.

The implemented agent core detects only the requested Windows, Fedora, and Ubuntu identities, reports stable host and boot evidence, and produces timestamped CPU, memory, storage, network, and collector-health observations. Windows uses operating-system counters; Fedora and Ubuntu use bounded parsers for Linux `/proc` data plus shared filesystem and network APIs. Failed or unsupported collection produces explicit unavailable, permission-denied, or error quality without a fabricated numeric value.

The executable service collects into a bounded file-backed SQLite WAL spool. The spool assigns monotonically increasing sequences, never evicts unacknowledged telemetry, rejects new observations when its configured count or byte budget is full, and removes data only through cumulative acknowledgements. Its concrete gRPC transport performs the in-stream device-key challenge, uploads the oldest contiguous batch, and deletes data only after a controller acknowledgement returned after the SQLite commit. HTTPS is required outside loopback development. The configured private-key PEM file is provisional; native packaging must replace it with platform-protected key storage and permissions.

The controller persists original submissions and controller-stamped envelopes in SQLite WAL. `(agent_id, sequence)` is the idempotency boundary: an identical retry is accepted, while changed bytes at an accepted sequence fail closed. Owner-gated endpoints expose enrolled inventory and the latest metric for each agent/metric/component tuple. These are diagnostic alpha endpoints, not the final client or historical reconstruction API.

### Android application and collector

Android is both an experience client and a constrained monitored endpoint. The implementation must respect VPN permission, background execution, battery optimization, and restricted system metrics.

Android must report freshness explicitly. Missing or delayed background data cannot be represented as a healthy zero. A persistent foreground service, scheduled work, or another platform mechanism is a working decision to validate rather than an assumed entitlement.

The implemented Android alpha is a plain native .NET Android application, deliberately separate from the later Avalonia experience. In the foreground it reads device identity, battery level and charging state, validated network transports, VPN presence, and an explicit UTC source time; unavailable Android values remain unavailable rather than becoming zero. It can verify the controller's HTTPS health endpoint and complete application enrollment with a P-256 key generated and retained by Android Keystore. One-time enrollment secret and challenge fields are cleared after every attempt and are never persisted; only the resulting agent/node receipt and public-key fingerprint are stored in private application preferences.

This alpha does not run a foreground service, schedule background collection, upload telemetry, install Headscale, or prove Android lifecycle behavior. Its development-signed Release APK is compilation evidence only until emulator instrumentation and a physical-device smoke test pass.

### Enrollment service

The enrollment path creates a short-lived transaction, coordinates a one-time Headscale registration method, establishes Monitor App application identity through device-key possession, and verifies end-to-end telemetry. Its unary service supports create, status, cancel, complete, device-challenge creation, and device-proof verification. Durable authentication retains the device public key and fingerprint rather than issuing a reusable raw agent credential.

Headscale-specific operations belong behind an adapter so a tested Headscale version can be pinned and upgraded deliberately.

### Persistence and history

The history model combines:

- Timestamped metric samples.
- Discrete events.
- Periodic state snapshots.

Historical reconstruction starts from a compatible snapshot and applies later events and metric state. The first implementation uses file-backed SQLite in WAL mode. Production persistence and integration tests use real SQLite rather than the EF Core in-memory provider. Migrations are versioned from the first schema.

Schema version 1 stores enrollments, nodes, device-authentication challenges, and redacted security audit events. Enrollment-secret verifiers use HMAC-SHA-256 with a required controller pepper and a per-record random salt. Completion atomically moves one pending enrollment to completed, stores the canonical P-256 public key and fingerprint, and erases the stored verifier, salt, and challenge. Device challenges are short-lived and atomically consumed once. Every connection enables foreign keys and a bounded busy timeout; the database uses WAL mode.

SQLite is a personal-scale starting point, not an unlimited scale claim. Revisit T008 after the vertical-slice load benchmark and before any multi-controller or large-fleet claim. A replacement is justified only if the agreed reference workload cannot meet ordered ingestion, restart durability, backup and restore, retention, or bounded history-query requirements, or if the product requires multiple controller writers.

### Experience clients

The first client may be deliberately plain. Its purpose is to prove:

- Enrollment status.
- Device inventory.
- Current metrics and freshness.
- Connection and collector failures.
- Historical queries and reconstruction.

The final living schematic, exploded architecture, animation, and game-like interaction come after the functional path is reliable. The domain API must support those experiences without making the temporary UI a source of truth.

The later shared client uses Avalonia on .NET 10. Android platform collection and lifecycle behavior remain in an Android-specific boundary even though most client and domain code is shared.

### Release and update trust

`MonitorApp.Update` is the platform-independent trust boundary for release metadata. A release manifest is carried inside a signed envelope and identifies its schema, channel, version, minimum compatible installed version, publication time, and one or more component/platform/architecture artifacts. Each artifact has an HTTPS URL, exact byte size, and SHA-256 digest.

The verifier accepts only a configured trusted P-256 SubjectPublicKeyInfo key and fixed P1363 ECDSA/SHA-256 signature. It then enforces channel, upgrade direction, compatibility floor, component, platform, architecture, size, and content hash. Downloading a public key alongside the manifest does not establish trust; trusted public keys must be provisioned through an already authenticated installer or release.

The current boundary verifies update eligibility and artifact integrity only. Installation, service replacement, rollback, and recovery remain platform-specific work. No unsigned or unverified artifact is silently installed.

## First deployment topology

Docker Compose is the accepted first controller deployment path. The deployment will contain the controller, pinned Headscale service or its explicitly configured connection, persistent volumes, and required TLS configuration. The Compose path must prove clean start, restart durability, upgrade, backup, and restore.

Endpoint agents are native operating-system packages:

- Windows service package.
- Fedora RPM with `systemd` and SELinux verification.
- Ubuntu DEB with `systemd` verification.
- Android APK or application bundle for the client and constrained collector.

Native controller packages and appliance images are deferred until the Compose deployment is operationally proven.

## Initial vertical slice

The first operational slice should prove this complete path:

1. Start the controller with durable local storage.
2. Create a short-lived enrollment.
3. Register a test agent through the application enrollment boundary.
4. Agent advertises platform and capabilities.
5. Agent sends real host identity, CPU, memory, storage, and network freshness data where supported.
6. Controller validates and persists the telemetry.
7. A client or command-line diagnostic reads current state.
8. Controller restarts without losing the enrolled node or accepted history.
9. A historical query returns the correct values for a bounded time window.
10. Tests prove expiration, duplicate handling, invalid identity rejection, persistence, and platform collector behavior.

This slice must work locally before Headscale automation or advanced UI is described as complete. Headscale integration then replaces manual network assumptions without changing the application identity and telemetry contracts.

## Implemented build foundation

The repository now pins the .NET 10.0.400 feature band, uses centralized package versions and committed NuGet lock files, builds with warnings as errors and recommended analyzers, and exposes `eng/verify.ps1` as the verification entry point. Current verification covers locked restore, Release build, all solution Unit and Contract tests (including the platform-independent Android core), real file-backed SQLite enrollment and agent-spool tests, the controller enrollment boundary, a platform-local Windows collector test when running on Windows, and formatting. The workload-specific Android app is built separately so ordinary Windows and Linux solution verification does not require an Android SDK. Cross-process agent/controller, Headscale, Android device/instrumentation, Windows service lifecycle, Fedora, and Ubuntu platform suites remain unavailable.

GitHub Actions definitions now verify the solution on Windows and Ubuntu and provide a protected signed-release path. The release path is designed to publish self-contained controller and agent alpha archives, generate a signed update manifest and checksum list, and refuse to overwrite an existing release. It remains operationally unverified until executed in the private GitHub repository with its protected release environment configured. Android APK signing remains a separate explicit gate.

## Platform boundaries

### Windows

- Long-running service lifecycle and clean uninstall.
- Platform-native CPU, memory, storage, network, and battery collection where available.
- Tailscale alternate coordination configuration may require CLI or policy/registry handling.

### Fedora

- `systemd` service lifecycle.
- RPM packaging is required before release support is claimed.
- SELinux behavior must be tested rather than disabled as a workaround.

### Ubuntu

- `systemd` service lifecycle.
- DEB packaging is required before release support is claimed.
- Supported Ubuntu versions must be recorded with actual install tests.

### Android

- APK/application lifecycle, VPN permission, and background collection constraints.
- Emulator tests are necessary but not sufficient for release; one physical-device smoke test is required for platform claims.

## Deferred architecture

These concerns remain deliberately deferred until the first vertical slice works:

- Operational control actions.
- Multi-user tenancy.
- Remote push notification service.
- Advanced retention rollups.
- Syncthing specialization beyond an adapter contract.
- Final rendering technology.
- Cinematic interaction and ambient animation.
