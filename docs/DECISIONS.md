# Decision Log

This log separates confirmed product constraints from working technical decisions. A working decision may change when tests provide better evidence. Product requirements change only through explicit user direction.

## Status definitions

- **Confirmed**: explicit user requirement.
- **Accepted**: current technical decision for implementation.
- **Provisional**: working direction awaiting an executable spike or benchmark.
- **Open**: unresolved.
- **Superseded**: replaced by a later entry.
- **Rejected**: explicitly not chosen.

## Product constraints

### P001 Target platforms

- Status: Confirmed
- Decision: Deliver Monitor App for Android, Windows, Fedora, and Ubuntu devices.
- Consequence: Shared-code claims require platform-specific verification. Packaging and lifecycle tests are part of support.

### P002 Functionality before visuals

- Status: Confirmed
- Decision: Implement and test operational functionality before advanced visuals and final UI polish.
- Consequence: The first client can be plain. Visual work cannot substitute for an incomplete enrollment, telemetry, history, or platform path.

### P003 Delegated implementation

- Status: Confirmed
- Decision: The primary coordinator delegates bulk implementation to lower agents and reviews their work.
- Consequence: Tasks need explicit boundaries, shared-tree coordination, and test/documentation handoffs.

### P004 Durable project memory

- Status: Confirmed
- Decision: Maintain a documentation set and short-term `TODO.md`; read both before new changes.
- Consequence: `AGENTS.md` enforces preflight and documentation updates.

### P005 Meaningful executable tests

- Status: Confirmed
- Decision: Tests must actually run and protect behavior important to app operation.
- Consequence: Unsupported or unexecuted suites are reported honestly and do not count as passing.

### P006 Self hosted Headscale

- Status: Superseded
- Decision: The earlier self-hosted Headscale/automatic setup direction is replaced by P007. Tailscale is user-managed reachability only.
- Consequence: Do not build Headscale automation or treat network membership as application trust.

### P007 Decentralized equal peers

- Status: Confirmed
- Decision: There is no Monitor App controller, central database, or always-on host. A Tailscale-IP introduction followed by bilateral application pairing connects equal peers directly whenever both services run.
- Consequence: Each peer collects, stores, queries, and presents locally; pairwise trust is explicit and independent of IP/tailnet membership.

### P008 Origin-owned peer replication

- Status: Confirmed
- Decision: Origin-owned streams synchronize peer to peer with explicit freshness, offline cache, and eventual convergence; no global consensus is required for origin-owned records.
- Consequence: Replication uses stable origin identity, cursors, ordering, and idempotency.

### P009 Observed devices and collector gateways

- Status: Confirmed
- Decision: Peers may observe non-peer peripherals and LAN infrastructure through purpose-built adapters. Identity/history survives attachment-host changes; collector ownership and failover are explicit; credentials remain local.
- Consequence: Model typed entities/relationships and adapter capability, health, freshness, source, evidence, and quality.

### P010 Broad categorized observation

- Status: Confirmed
- Decision: Pursue broad measurement through versioned capability modules and a canonical observation catalog, with truthful unavailable states, privacy/cost metadata, adaptive sampling/retention, and purpose-built inspectors.
- Consequence: The domain model supports domain lenses, exact search, selective customization, and a truthful fallback inspector without becoming a blank dashboard.

### P011 Network privacy and defensive detection

- Status: Confirmed
- Decision: Aggregate operational network metrics and infrastructure facts are allowed. Communication identities/content, packet capture, flow/session histories, DNS history, per-app traffic, and DPI are prohibited in ordinary monitoring. Defensive detection is a separate explicit capability with evidence/confidence and stricter permission/retention rules.
- Consequence: Module review and executable registration tests enforce the boundary.

### P012 Actual inventory and design separation

- Status: Confirmed
- Decision: Monitor App includes both what the user has/is running and what the user is planning/designing, but actual state and named design scenarios remain explicitly distinct in storage, queries, calculations, automation, history, and UI.
- Consequence: Owned, installed, connected, and running are separate actual facts. Design-only objects or relationships become actual only through an explicit transition that preserves plan, candidate, offer, and decision history.

### P013 Cable connector and length system

- Status: Confirmed
- Decision: Recreate the useful structure of the user's work cable-code system as a richer cable inventory and planning capability centered on the relationship between both connector ends, unit-bearing length, and additional ratings/parameters.
- Consequence: Fast reusable manual connector/length templates are required. QR codes and dedicated scanner hardware are not requirements. Cable identity, current possession/location/connection, and planned assignment remain distinct.

### P014 Physical design constraints

- Status: Confirmed
- Decision: Support modular physical, audio, RF, electrical, thermal, dimensional, mounting, and cable constraints over inventory and plans.
- Consequence: Findings expose inputs, units, source, assumptions, rule/version, evidence, confidence, and unknowns rather than one unexplained compatibility score.

### P015 Sourcing automation and notifications

- Status: Confirmed
- Decision: Include candidate products, seller offers, price/availability history, customizable event-condition-action rules, and notifications for conditions such as disconnection, price, availability, inventory movement, and project readiness.
- Consequence: Product, offer, and actual-instance identity remain separate. The automation engine is independent of UI/AI; push transport and equal-peer execution ownership remain open technical choices.

### P016 Distributed local AI

- Status: Confirmed
- Decision: Provide optional local-first AI assistance that can route bounded work to capable peers and use permissioned domain tools.
- Consequence: AI is not a source of truth, identity/trust authority, replication owner, rule runtime, or permission bypass. Model/runtime and routing choices remain open.

## Technical decisions

### T001 Functional component boundaries

- Status: Superseded by T017
- Decision: Use a self-hosted controller, outbound endpoint agents, an Android client/collector, versioned shared contracts, durable history, and adapters for Headscale and service integrations.
- Reason: This is the smallest architecture that supports all requested platforms, private connectivity, truthful telemetry, and historical reconstruction.
- Validation: First end-to-end vertical slice and restart-durability test.

### T002 Read only foundation

- Status: Provisional
- Decision: The first integrated functional release is read-only.
- Reason: Telemetry identity, freshness, history, and platform lifecycle must be reliable before real device control increases impact.
- Revisit: After the action broker security model and high-value actions are explicitly approved.

### T003 Headscale adapter boundary

- Status: Superseded by P007 and T017
- Decision: Isolate Headscale operations behind an internal adapter and pin a tested supported version.
- Reason: Registration and policy behavior are external dependencies that must not leak through domain and UI code.

### T004 Application identity beyond network membership

- Status: Accepted
- Decision: Tailnet membership does not authenticate an agent to Monitor App. Enrollment establishes separate application identity bound to expected network identity when possible.
- Reason: Network access alone must not authorize telemetry injection or future actions.

### T005 History composition

- Status: Provisional
- Decision: Model history with metric samples, discrete events, and periodic snapshots.
- Reason: The design requires time-window metrics and deterministic environment reconstruction without full-state copies at every instant.
- Validation: Reconstruction and persistence benchmark.

### T006 Implementation stack

- Status: Accepted
- Decision: Use C# on .NET 10 LTS, ASP.NET Core hosting where needed, standard gRPC over TLS and HTTP/2 with versioned Protocol Buffer contracts, file-backed SQLite, shared peer/collector code, and Avalonia for the later shared experience client. The earlier controller-specific application of this stack is superseded by T017.
- Reason: .NET 10 is the current long-term-support release and supports the required desktop and Linux environments. Avalonia provides one client framework across all requested platforms while leaving Android-specific lifecycle and collection behind a platform boundary. ASP.NET Core gRPC supports streaming agent sessions and generated contracts in the same language.
- Evidence: Current official platform-support documentation covers .NET 10 on supported Windows, Fedora, and Ubuntu releases and Avalonia on Windows, Fedora, Ubuntu, and Android. Executable repository and platform builds remain required before support is claimed.
- Consequences: Pin the SDK and packages; organize the repository as a .NET solution; keep platform collectors and packaging separately tested; do not start final Avalonia visual work before the functional milestone.
- Revisit condition: Revisit only if a requested target cannot pass an executable build and required platform tests with the pinned versions, or if the later visual prototype proves a measured rendering limitation that cannot be addressed within Avalonia.

### T007 Controller packaging

- Status: Superseded by P007 and T017
- Decision: Use Docker Compose as the first supported controller deployment. Keep Windows, Fedora, and Ubuntu endpoint agents as separate native packages.
- Reason: Compose provides one bounded, reproducible self-hosted path for the ASP.NET Core controller, pinned Headscale dependency, persistent storage, and TLS configuration without coupling endpoint service installation to the controller host.
- Evidence: Clean start, restart, upgrade, backup, and restore tests remain release gates for the Compose deployment.
- Consequences: Native controller packages and appliance images are deferred. Compose volumes and backup procedures must include SQLite and any managed Headscale state.
- Revisit condition: Revisit after the Compose path is operationally proven or when a confirmed deployment target cannot run the supported container stack.

### T008 Persistence engine

- Status: Accepted
- Decision: Use file-backed SQLite in WAL mode for the first personal-scale functional slice. Use real SQLite in persistence tests and version migrations from the first schema.
- Reason: SQLite keeps each personal-scale peer small while providing transactions, durability, ordered queries, and a practical backup path. Every peer owns a local database; there is no shared central writer.
- Evidence: The vertical slice must prove concurrent ingestion at the agreed reference workload, restart durability, ordered history queries, migrations, retention, backup, and restore using the production provider and configuration.
- Consequences: Do not use the EF Core in-memory provider as persistence evidence. Keep persistence behind domain-facing interfaces and avoid SQLite-specific behavior in the protocol.
- Revisit condition: Revisit after the vertical-slice benchmark and before a multi-controller or large-fleet claim. Consider a client-server database only if SQLite fails the agreed ingestion, query, retention, backup, or restore acceptance conditions, or multiple controller writers become required.

### T009 Build and dependency baseline

- Status: Accepted
- Decision: Pin the repository to the .NET SDK 10.0.400 feature band with latest-patch roll-forward, centralize NuGet versions, commit project lock files, treat compiler warnings as errors, enable recommended .NET analyzers, and use `eng/verify.ps1` as the discoverable verification entry point.
- Reason: Reproducible dependencies and one executable verification path are prerequisites for delegated cross-platform work.
- Evidence: Locked restore, Release build with zero warnings, the root verification script, and self-contained packaging with a byte-for-byte tracked-tree comparison pass on Windows with SDK 10.0.400. The first private CI run caught and rejected runtime-specific residue in committed locks.
- Consequences: Package changes update committed platform-neutral lock files deliberately. Runtime-specific publishing must use ignored staging locks and leave all tracked files unchanged. Every new test suite must be added to the root verification script when its project is introduced.
- Revisit condition: Update the pinned feature band only through an explicit dependency change with locked restore, build, test, and formatting evidence.

### T010 Initial agent wire contract

- Status: Accepted
- Decision: Generate the `monitor.v1` C# boundary from Protocol Buffers and carry the outbound agent session over one bidirectional gRPC stream. Use an explicit protocol version, cumulative accepted-through acknowledgements, monotonically increasing per-agent sequences, opaque enrollment credentials, typed metric-value presence, and separate quality and unit enums. Keep Headscale types out of the application contract.
- Reason: One generated boundary avoids hand-written serializer drift, permits cumulative reconnect acknowledgement, preserves valid numeric zero, and keeps network membership distinct from Monitor App identity.
- Evidence: Contract round-trip and semantic validation tests cover zero-value presence, unknown-field preservation, Android platform representation, opaque enrollment values, bidirectional service shape, invalid timestamps and units, missing identity, quality/value mismatch, and noncontiguous batches.
- Consequences: Agents and controllers share `MonitorApp.Contracts`; controller-set ingestion timestamps use the same telemetry envelope but are absent on agent submission; batches are contiguous and acknowledgements are cumulative.
- Revisit condition: Declare the supported major/minor compatibility window and frozen upgrade fixtures before a released agent and controller may be upgraded independently.

### T011 Device proof of possession

- Status: Accepted
- Decision: For protocol v1, enroll and authenticate an agent with an ECDSA P-256 device key using SHA-256. Enrollment and later device-authentication challenges are 32 random bytes. Signatures use fixed-width 64-byte IEEE P1363 encoding. The device sends its SubjectPublicKeyInfo public key at enrollment, retains the private key locally, and later proves possession with short-lived signed challenges. Enrollment completion returns node and agent ids plus the SHA-256 public-key fingerprint, never a reusable raw agent credential.
- Reason: A durable asymmetric identity lets the controller authenticate a returning device without storing or reissuing a bearer credential that can be copied and replayed. Fixed key, challenge, and signature encodings make cross-platform validation deterministic.
- Evidence: Generated-contract tests exercise all six unary enrollment service methods, validate exact sizes and P-256 SPKI structure, perform real signing and verification after Protobuf round trips, reject identity-bound payload changes and the wrong curve, and confirm safe validation errors do not echo secret or signature bytes.
- Consequences: The controller must store the enrolled public key and fingerprint, hash the one-time enrollment secret, generate challenges with a cryptographically secure random source, enforce expiration and atomic single use, rate-limit challenge endpoints, and bind a successful proof to the authenticated transport session. Device private-key storage and rotation require platform-specific implementation and tests.
- Revisit condition: Add a new explicit algorithm enum value and compatibility tests before supporting another key or signature algorithm; never reinterpret the v1 value or encoding.

### T012 Durable endpoint telemetry spool

- Status: Accepted
- Decision: Assign each agent telemetry envelope a monotonically increasing sequence in a bounded file-backed SQLite WAL spool. Never evict unacknowledged telemetry; stop accepting new observations at the configured count or byte limit. Remove only a cumulative controller-acknowledged prefix and retry the identical pending sequence range after a failed upload.
- Reason: Short controller or network outages must not invent gaps, reorder observations, or silently discard the evidence needed for historical reconstruction.
- Evidence: Real file-backed SQLite tests cover restart persistence, continued sequence allocation, prefix acknowledgement, capacity refusal without eviction, batch-size refusal, and identical retry after a simulated connection failure. A platform-local Windows test exercises the production host collector.
- Consequences: The service can continue collecting only while bounded storage remains available. Packaging must protect the spool directory, expose an understandable full-spool state, and choose reviewed capacity defaults. The spool does not itself authenticate the controller or prove cross-process delivery.
- Revisit condition: Revisit capacity and batching defaults after the end-to-end outage/resume test and reference telemetry-volume measurement; keep the no-silent-eviction rule unless the user approves a visible retention policy.

### T013 Durable enrollment persistence and owner boundary

- Status: Accepted
- Decision: Persist enrollment version 1 in file-backed SQLite WAL using atomic conditional transitions. Store only a salted HMAC-SHA-256 verifier derived with a required controller pepper, retain canonical P-256 public keys and fingerprints for application identity, and gate enrollment creation, inspection, and cancellation with a separate administrator token.
- Reason: Enrollment must survive controller restart, reject reuse across concurrent connections, avoid plaintext bearer-secret storage, and prevent unauthenticated callers from minting pairing material.
- Evidence: Real file-backed SQLite tests cover restart, cancellation, expiration, wrong intent, wrong proof, concurrent completion, enrollment and challenge replay, and database/WAL secret scanning. A controller-boundary test covers administrator gating and safe gRPC error detail.
- Consequences: Controller startup requires externally provisioned pepper and administrator token values of at least 32 bytes. The bootstrap token is not a final user authentication system. Returning-device proof outside an agent stream is not a session credential; telemetry uses the separate stream-bound mechanism in T016.
- Revisit condition: Replace or extend the bootstrap owner boundary when the user identity method is selected; preserve atomic single-use and redaction guarantees in any replacement.

### T014 Signed release and update eligibility boundary

- Status: Accepted
- Decision: Publish release metadata as a signed envelope using ECDSA P-256/SHA-256 with fixed P1363 signatures. The signed payload identifies schema, channel, version, minimum compatible version, publication time, and exact HTTPS URL, platform, architecture, size, and SHA-256 for each artifact. Production code verifies a provisioned trusted public key and applies upgrade policy before an artifact is eligible; installation is a later platform-specific boundary.
- Reason: A private GitHub repository and TLS protect access in transit but do not make a downloaded binary intrinsically authentic, correctly targeted, or safe from downgrade. Eligibility and byte integrity must be enforceable without granting the updater release-authority credentials.
- Evidence: Fourteen unit cases use real P-256 signatures and production file hashing to cover valid selection, wrong-key rejection, downgrade/reinstall rejection, minimum-compatible-version enforcement, platform/architecture mismatch, canonical versions, and size/hash mismatch. The release tool builds with warnings as errors.
- Consequences: The protected GitHub release environment is the only workflow source of private signing material; missing or mismatched key configuration fails closed. Trusted public keys must be delivered through an authenticated path. No current component silently installs updates. Windows service replacement, RPM/DEB transactions, Android signing, rollback, key rotation in deployed clients, and an executed private-repository release remain release gates.
- Revisit condition: Add a new signature-algorithm or manifest-schema identifier rather than reinterpreting version 1. Revisit the trust-set and rotation design before shipping the first updater-enabled installer.

### T015 Android functional alpha boundary

- Status: Accepted
- Decision: Implement the first Android slice as a plain native .NET Android application plus a platform-independent core library. Keep the workload-specific application outside `MonitorApp.sln`, while including the core and its tests in the ordinary solution. The alpha is foreground-only, uses Android Keystore for the P-256 enrollment key, requires HTTPS controller origins, displays explicit collection time, and does not imply background monitoring or final Avalonia UI direction.
- Reason: This proves Android collection and secure pairing mechanics without making every Windows/Ubuntu CI run install a mobile SDK or prematurely committing the final visual framework. Foreground-only behavior is truthful under Android lifecycle limits.
- Evidence: Seven core tests pass against production normalization, URI, proof, and signature-conversion code. A .NET 10 Android API 36 Release APK builds with zero warnings using workload 36.1.69 and JDK 21. No emulator or physical-device behavior has passed.
- Consequences: Compilation is not an Android support claim. The APK remains development-signed. Background execution, telemetry upload, VPN permission behavior, Android package signing, emulator instrumentation, physical-device smoke testing, and final Avalonia integration remain explicit gates.
- Revisit condition: Revisit the native shell when the functional telemetry path and selected experience framework are ready, or when Android lifecycle testing requires a different supported platform mechanism.

### T016 Stream-bound agent authentication and durable telemetry acknowledgement

- Status: Accepted
- Decision: Authenticate each bidirectional `AgentSession` with a fresh P-256 challenge signed inside that exact stream. Bind the proof to agent id, random session id, challenge id, and challenge bytes; atomically consume it before accepting telemetry. Persist contiguous telemetry in file-backed SQLite and acknowledge only after commit. Treat `(agent_id, sequence)` as the idempotency boundary: identical retries succeed and changed retries fail.
- Reason: A separate proof RPC cannot safely authenticate a later HTTP/2 stream, while a reusable session token would recreate bearer replay risk. Commit-before-ack and byte-identical retry preserve history through ambiguous disconnects.
- Evidence: Real loopback Kestrel HTTP/2 gRPC tests exercise production client/server code for device-key authentication, upload, owner reads, restart, identical retry, wrong key, cross-stream substitution, replay, and redacted failures. Real SQLite tests repeat authentication, ingest, conflict, current-state, and restart behavior through the production store.
- Consequences: The controller keeps no reusable agent bearer secret. The agent requires its enrolled private key for every new stream. Loopback plaintext HTTP/2 exists only for development/tests; non-loopback configuration requires HTTPS. Cross-process outage/resume, platform key protection, retention, and bounded history queries remain release gates.
- Revisit condition: Add an explicit new proof version before changing payload framing, signature algorithm, or challenge semantics; never reinterpret v1.

### T017 Equal-peer migration boundary

- Status: Accepted
- Decision: Replace controller/agent deployment with one peer service containing device identity, bilateral pairing, local SQLite observation/query, direct replication, collectors/adapters, and a local client boundary. Reuse existing crypto, protocol, persistence, collectors, Android, and update code only where peer tests prove compatibility.
- Reason: This implements P007 without discarding validated foundations.
- Evidence: Documentation migration; executable evidence is the bounded two-peer milestone in `TODO.md`.
- Consequences: Controller, controller enrollment administration, central history, Headscale adapter, and controller artifacts are superseded and receive no new product features.
- Revisit condition: After the two-peer end-to-end test, refine component names and rolling-upgrade window.

### T018 Origin replication and observed-device ownership

- Status: Accepted
- Decision: Use immutable origin identity/sequence with per-peer replication cursors and idempotent retry. Keep observed-device identity separate from attachment and collecting peer; configure collector ownership explicitly and never replicate adapter credentials.
- Reason: This permits offline operation and convergence without consensus while preventing duplicate polling and identity loss.
- Evidence: Required integration cases in `docs/TESTING.md`.
- Consequences: Shared-metadata conflict semantics and automatic failover remain deferred.
- Revisit condition: After personal-scale load and reassignment tests.

### T019 Capability and privacy contract

- Status: Accepted
- Decision: Modules use versioned namespaced declarations and catalog metadata for domain/entity/subsystem/source/unit/temporal behavior/importance/privacy/cost. Registration rejects prohibited ordinary network observations; defensive detection is separately enabled and permissioned.
- Reason: Broad collection needs compatibility, categorization, and enforceable privacy rather than a fixed narrow allowlist or unbounded plugins.
- Evidence: Required contract/security tests in `docs/TESTING.md` and `docs/SECURITY.md`.
- Consequences: Unknown compatible modules receive a truthful generated inspector; important integrations remain purpose-built.
- Revisit condition: When the first external adapter contract is implemented.

### T020 Universal object envelope and state planes

- Status: Provisional
- Decision: Use a stable shared object envelope plus typed capability extensions and typed relationships. Store actual state and design-scenario state as explicitly scoped assertions/relationships rather than one blended status.
- Reason: Monitoring, inventory, cables, projects, products, offers, and plans need common identity/provenance without a giant nullable schema or false equivalence.
- Evidence: Required schema, promotion, query, and UI tests in `docs/TESTING.md`; no implementation evidence yet.
- Consequences: Product definitions, seller offers, actual instances, and design placeholders remain separate identities. Promotion links records and preserves history.
- Revisit condition: After the first plain actual/design inventory client and cable slice.

### T021 Cable specification and quick-entry boundary

- Status: Provisional
- Decision: Represent a cable specification with two structured connector ends, directionality, unit-bearing nominal/measured length, ratings, and physical properties; represent actual items or counted batches separately. Make searchable manual connector-pair and length templates the baseline entry path.
- Reason: This preserves the useful connector-plus-length composition of the legacy sheets while supporting real compatibility, condition, location, connection, and plan-assignment queries without scanner hardware.
- Evidence: Required cable round-trip, match, constraint, and workflow tests in `docs/TESTING.md`; no implementation evidence yet.
- Consequences: Human labels are optional aliases and do not encode the authoritative record. QR and dedicated scanner support are outside the required cable path.
- Revisit condition: After user review of connector taxonomy and the first cable inventory prototype.

### T022 Deterministic automation ownership

- Status: Provisional
- Decision: Run versioned event-condition-action rules in a durable deterministic peer service with explicit execution ownership, idempotency, schedule state, cooldown/deduplication, audit, and a notification outbox.
- Reason: Alerts must survive UI/model shutdown and replicated events must not produce duplicate actions across equal peers.
- Evidence: Required restart, fake-clock, outbox, and two-peer ownership tests in `docs/TESTING.md`; no implementation evidence yet.
- Consequences: Natural language produces reviewable draft rules only. Notification delivery and operational actions remain adapters with separate permissions.
- Revisit condition: Before the first live notification adapter.

### T023 Local AI job routing boundary

- Status: Provisional
- Decision: Initially route a bounded AI job and explicit tool grants to one eligible peer; do not assume cross-peer tensor execution. Store outputs as derived artifacts with model/source provenance.
- Reason: Whole-job routing satisfies the distributed local-resource goal with a smaller privacy, failure, and scheduling surface.
- Evidence: Capability, permission, provenance, failure, and resource-policy tests in `docs/TESTING.md`; no implementation evidence yet.
- Consequences: Deterministic services remain authoritative and useful without a model. Model family, runtime, embeddings, and hardware acceleration remain open.
- Revisit condition: After one read-only semantic-search or explanation slice runs on two different peer capability classes.

### T024 First peer-native foundation boundary

- Status: Accepted
- Decision: Establish the first peer-native slice as generated `monitor.v1` pairing/session/replication contracts, a platform-independent P-256 bilateral proof and compatibility core, and a versioned file-backed SQLite peer store. Keep the network host/session coordinator and platform key storage as the next boundary.
- Reason: Contract, cryptographic, and persistence invariants can be proven independently before process/network lifecycle makes failures harder to isolate.
- Evidence: Contract tests use generated types and real signing; peer-core tests cover bilateral proof, substitution, transcript mutation, expiry, replay, compatibility, duplicate sessions, and key immutability; eight real SQLite tests cover restart, trust/revocation, origin ordering/idempotency/conflicts, cursors/gaps, evidence, collector ownership, and credential-safe descriptors.
- Consequences: These libraries are not a peer release and do not prove Tailscale/private-interface binding, platform-protected keys, durable replay across restart, or two-peer convergence. The root solution and verification script include all three boundaries.
- Revisit condition: After the production peer service and real two-peer integration test pass.

## Rejected directions inherited from the product specification

- Generic progressive-information-density dashboard.
- Infinite freeform workspace customization.
- Simple topology as a sufficient product concept.
- Instrument panel, infrastructure atlas, or focus-and-periphery as the primary visual identity.
- Decorative gestures that can dispatch real actions.

## Decision entry template

```text
### TNNN Short title

- Status:
- Decision:
- Reason:
- Evidence:
- Consequences:
- Revisit condition:
```
