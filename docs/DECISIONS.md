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
- Decision: Implement the first Android slice as a plain native .NET Android application plus a platform-independent core library. (Superseded in part by T031: the foreground-activity-only lifecycle and controller pairing path are replaced by a foreground-service peer.) Keep the workload-specific application outside `MonitorApp.sln`, while including the core and its tests in the ordinary solution. The alpha is foreground-only, uses Android Keystore for the P-256 enrollment key, requires HTTPS controller origins, displays explicit collection time, and does not imply background monitoring or final Avalonia UI direction.
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
- Evidence: Signed origin replication tests cover immutable identity/sequence and durable cursors. Real-SQLite observed-device tests prove matching strong identity across observers, reject IP-only strong identity, preserve typed attachment evidence, validate adapter/entity/collector consistency, and retain current plus historical collector ownership through restart and host reassignment.
- Consequences: Shared-metadata conflict semantics, adapter-specific discovery, and automatic failover remain deferred. Reassignment requires an explicitly provisioned adapter on the new collector; merely learning the device identity never authorizes polling.
- Revisit condition: After personal-scale load and reassignment tests.

### T019 Capability and privacy contract

- Status: Accepted
- Decision: Modules use versioned namespaced declarations and catalog metadata for domain/entity/subsystem/source/unit/temporal behavior/importance/privacy/cost. Registration rejects prohibited ordinary network observations; defensive detection is separately enabled and permissioned.
- Reason: Broad collection needs compatibility, categorization, and enforceable privacy rather than a fixed narrow allowlist or unbounded plugins.
- Evidence: Required contract/security tests in `docs/TESTING.md` and `docs/SECURITY.md`.
- Consequences: Unknown compatible modules receive a truthful generated inspector; important integrations remain purpose-built.
- Revisit condition: When the first external adapter contract is implemented.

### T020 Universal object envelope and state planes

- Status: Accepted
- Decision: Use a stable shared object envelope plus typed capability extensions and typed relationships. Store actual state and design-scenario state as explicitly scoped assertions/relationships rather than one blended status.
- Reason: Monitoring, inventory, cables, projects, products, offers, and plans need common identity/provenance without a giant nullable schema or false equivalence.
- Evidence: Generated-contract tests round-trip the universal envelope, named design scope, provenance/validity, typed relationships, distinct product/offer/physical-instance identities, plan-to-actual cable assignment, and the connector/length cable extension. Negative tests reject actual relationships that reference design-only objects, mismatched design revisions, collapsed identity references, and invalid connector/length metadata.
- Consequences: Product definitions, seller offers, actual instances, and design placeholders remain separate identities. The contract permits a design relationship to reference an actual object without changing that object's plane. Persistence, explicit promotion events, query projection, and UI remain required before this becomes a usable inventory feature.
- Revisit condition: After the first plain actual/design inventory client and cable slice.

### T021 Cable specification and quick-entry boundary

- Status: Provisional
- Decision: Represent a cable specification with two structured connector ends, directionality, unit-bearing nominal/measured length, ratings, and physical properties; represent actual items or counted batches separately. Make searchable manual connector-pair and length templates the baseline entry path.
- Reason: This preserves the useful connector-plus-length composition of the legacy sheets while supporting real compatibility, condition, location, connection, and plan-assignment queries without scanner hardware.
- Evidence: The initial contract round-trip and rejection tests cover two connector ends, reversible/directional roles, and positive unit-bearing nominal/measured length. Matching, ratings, constraint, persistence, and quick-entry workflow evidence remains required.
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

### T025 Split peer service into independently tested coordination boundaries

- Status: Accepted
- Decision: Separate durable pairing/session coordination, origin-owned replication, and the ASP.NET Core gRPC host into `MonitorApp.Peer.Application`, `MonitorApp.Peer.Replication`, and `MonitorApp.Peer.Service`. Keep the host dependent on narrow coordinator interfaces so security and network behavior can be tested without duplicating cryptographic or persistence rules.
- Reason: Approval/replay durability, replication convergence, and hostile network input have different invariants and fixtures. Independent components keep each path executable while preserving one future composed peer process.
- Evidence: Nine real-SQLite application tests, seven signed replication/SQLite tests, and five real Windows-loopback gRPC tests pass in their focused suites.
- Consequences: Passing component tests does not claim a runnable peer release. Production dependency wiring, local administration/query, protected platform keys, transport deployment, and the separate-store two-peer convergence test remain required.
- Revisit condition: If composition reveals a boundary that forces duplicate state or weakens end-to-end authentication.

### T026 Windows preview identity and local administration boundary

- Status: Accepted for engineering preview
- Decision: Compose the first interactable peer as a Windows-specific node using a CurrentUser-DPAPI-protected P-256 key, one local SQLite database, an explicit loopback/private peer listener, and a separate loopback-only administration page. Keep the shared peer, transport, persistence, and local-API libraries platform-neutral.
- Reason: A real launchable slice exposes composition failures early without pretending that an unprotected portable key file or remote browser administration is acceptable.
- Evidence: The protected identity survives restart, the key file is not importable as raw PKCS#8 and does not contain public identity bytes, two independent nodes start against separate stores, and both local pages answer over real HTTP. Manual launch also verified the same stable fingerprint across restart.
- Consequences: This is Windows preview evidence only. Initial fresh pairing, Windows service packaging, non-Windows protected key stores, and full two-peer convergence remain release gates.
- Revisit condition: Replace the Windows-only composition with platform hosts after each target has an approved protected-key and lifecycle implementation.
- Update: The node now also runs as a Linux engineering preview (`net10.0`, `LinuxFileIdentityProvider` with owner-only `identity.p256`, collector chosen by `PlatformDetector`), verified in an Ubuntu 24.04 container only. File permissions are weaker than DPAPI and are accepted for the preview; RPM/DEB, `systemd`, SELinux, and Fedora remain unproven, so no Linux support is claimed.

### T027 Fresh-pairing approval and trust-commit protocol

- Status: Accepted
- Decision: Treat the initiating local invitation submission as one explicit approval and require a separate approval on the contacted peer. Bind a caller-supplied pairing ID to the exact peer identity/key/version, reuse one durable challenge, verify the contacted peer's proof before the initiator sends its proof, and commit trust on each side only after the appropriate completed proof/confirmation step.
- Reason: Address possession or network membership is not identity, and one-sided proof or trust-before-confirmation would allow substitution and ambiguous partial pairing.
- Evidence: Contract, application, service, and transport suites cover pending/approved/rejected/expired/completed states, idempotent retry, changed-identity reuse, wrong proof, replay, revocation, and failure-before-trust. The composed Windows test drives both web approval boundaries and verifies bilateral trust in separate databases.
- Consequences: The local invitation UI requires the peer ID and SHA-256 fingerprint displayed by the contacted node. Pairing can wait while the remote user approves. Peer discovery shortcuts may be added later only if they preserve the same explicit identity preview and proof boundary.
- Revisit condition: Revisit only if a better human-verifiable introduction method preserves bilateral approval, transcript binding, and fail-closed trust commit.

### T028 Persisted reconnect and origin synchronization composition

- Status: Accepted for the Windows engineering preview
- Decision: Persist an explicitly approved peer IP/port separately from its pinned application identity, start one bounded outbound reconnect loop per active stored peer, ingest system snapshots as signed local-origin records, and synchronize direct-origin streams by exchanging advertised heads and durable acknowledgements over the authenticated session. Infer loopback-development versus Tailscale-encrypted transport only from the validated stored IP class; never treat the locator as identity.
- Reason: The first operational milestone requires collection and eventual convergence after either node and the network path have been unavailable, without adding a controller, relayed origins, or global consensus.
- Evidence: Focused persistence, service, transport, and Windows node suites pass. The composed test uses separate file-backed SQLite stores and real HTTP/gRPC hosts to pair, exchange, disconnect, collect independently, restart, reconnect with backoff, and compare every sequence, content hash, and origin signature on both nodes.
- Consequences: The initiator currently owns the reconnect locator; one authenticated session synchronizes both peers' direct-origin streams. This proves Windows loopback behavior only. Locator repair/rotation UI, live-session revocation, Tailscale/TLS deployment, non-Windows key stores/lifecycles, and multi-origin relay remain separate gates.
- Revisit condition: Revisit the one-connection-per-peer and full-session head advertisement model when continuous high-rate collection or more than one origin stream per peer requires incremental availability exchange.

### T029 Capability declaration and registration boundary

- Status: Accepted
- Decision: Represent each module as a bounded versioned Protobuf declaration with a lowercase namespaced module id, semantic version, compatible peer-protocol range, declared entity/relationship types, canonical observation metadata, and exactly one generated fallback inspector descriptor. Validate the declaration before persistence or advertisement. Ordinary modules may declare non-network, aggregate operational, and infrastructure-fact data but not communication identities, flows/sessions, query history, packet detail, per-process traffic, or content. Defensive-security modules use a distinct module/privacy class and require explicit enablement, evidence, confidence, and positive retention metadata.
- Reason: Broad measurement remains useful only if peers can interpret unknown compatible modules truthfully and reject privacy-prohibited declarations before collection begins.
- Evidence: Contract tests round-trip the catalog schema and reject incompatible versions, missing units/references/fallbacks, and every prohibited ordinary network class. Real-SQLite registry tests prove invalid declarations are not persisted, defensive declarations require their separate policy, and valid declarations advertise the exact version and SHA-256 hash. The Windows node registers and validates its built-in system module on startup.
- Consequences: Module declarations contain metadata, not credentials or private keys. The current contract validates purpose-built descriptors but selection/routing between purpose-built and generated inspectors remains the next UI-facing gate. Deeper defensive collection still requires local processing, authorization/legal review, and adapter-specific tests beyond declaration acceptance.
- Revisit condition: Revisit limits and compatibility fields when the first external adapter or rolling module upgrade produces measured requirements.

### T030 First peripheral adapter and peer release artifact

- Status: Accepted for engineering preview
- Decision: Use the currently connected Yamaha AG06MK2 as a provisional, read-only Windows adapter spike. Query only its fixed USB/PnP identity and driver/attachment properties through a bounded non-interactive process; keep the configured Monitor App entity identity authoritative because this device exposes no trustworthy serial. Package the Windows peer node as a separate self-contained alpha zip rather than presenting the legacy Windows agent as the peer app.
- Reason: This proves real peripheral availability, freshness, permission/error handling, and packaging without inventing vendor control semantics or treating a port-dependent Windows instance id as permanent identity.
- Evidence: The real device reports Yamaha USB VID 0499/PID 1757 and driver 2.1.7.5 on Windows. Adapter tests cover available/unavailable/denied/error quality, configured identity/collector, timestamp, and driver evidence. Release packaging includes `monitorapp-peer-windows-x64-VERSION.zip`.
- Consequences: The adapter has no operational actions and no credentials. USB container/location values are correlation evidence only. The existing `dev-0.1.0` pre-release remains a controller/agent development build; a current peer download requires reviewed committed source, an immutable version tag, configured signing environment, and a successful release workflow.
- Revisit condition: Replace or extend the spike when the user's normal hardware setup is available or a supported Yamaha API is needed for a concrete read-only job.

### T032 Physical LAN self-observation model

- Status: Accepted for engineering preview (working decision)
- Decision: With no managed switch, model the physical LAN only from each peer's own interfaces. Overlay/virtual/bridge/loopback interfaces are explicitly non-physical and separate from segments. The interface's own MAC is a strong claim for physical interfaces only. Segments are per-observer; subnet and gateway-IP matches yield only inferred `probable_same_segment` edges (cap 0.6). The default-gateway MAC is not collected (it would require neighbor-table access, which the network-data boundary forbids in ordinary monitoring); gateway identity stays weak. Switch/port types exist for a future adapter; only unknown-upstream placeholders are inferred. No schema change: relationship kinds 7-9 extend the existing integer enum.
- Reason: Respects the aggregate-without-communication-identities boundary and the rule that IPs cannot merge entities.
- Evidence: Collection tests (fixture, real /sys, real SQLite) and contract validator tests.
- Consequences: Link attributes (speed, duplex, MTU) are carried in the collected fragment but have no persistence slot until observation emission is wired. Android and macOS report unsupported.
- Revisit condition: A managed switch or user-approved gateway-MAC collection.

### T031 Android outbound-only peer with foreground service

- Status: Accepted
- Decision: Android peers are outbound-only in this slice (no inbound listener). They dial the desktop peer; origin-history replication is bidirectional over that one phone-initiated authenticated session (the peer session stream is symmetric, so the phone pulls the desktop's origin and pushes its own). Pairing stays bilateral: the phone user explicitly initiates from a versioned invitation (`monitor-pair1.` + base64url JSON: address, peer id, SHA-256 fingerprint, optional expiry) and the desktop user explicitly approves. The invitation carries no secret and grants no trust; the phone pins the invitation fingerprint. Collection and sync run in a user-started foreground service with a persistent notification (`FOREGROUND_SERVICE_SPECIAL_USE`, chosen over `dataSync` to avoid the Android 15 six-hour timeout; sideload distribution, not Play) stoppable from the notification or UI. The portable runtime (`MonitorApp.Peer.Mobile`) is lifecycle-agnostic (`Start`/`StopAsync`) and takes an injected `ILocalPeerIdentityProvider` (Android Keystore on device, software key in tests). The web-free parts of the peer stack (`IPeerAdvertisementProvider`, `PeerCapabilityRegistry`) moved from Peer.Service to Peer.Replication (namespace unchanged) so Peer.Sync no longer depends on ASP.NET Core.
- Reason: Kestrel is unsupported on Android, and the symmetric session removes the need for a phone listener, NAT/background-listener fragility, and a second trust path. Reconnect-per-pass sync (`PeerGrpcConnector.SyncOnceAsync`) is needed because a held-open session advertises local records only at session start.
- Evidence: A Linux integration test runs the production desktop host and the mobile runtime over real loopback HTTP/2 gRPC with two file-backed SQLite stores: phone-initiated pairing with desktop approval, offline collection on both sides, reconnect convergence without duplicates, idempotent repeat, and refusal after desktop revocation. No device or emulator evidence exists.
- Consequences: A phone is reachable only while it dials; a desktop cannot push to an offline phone, it waits for the next phone session. Phone sync is polling (default 15 s) with capped backoff; a refused (revoked) status is reported but local trust is not changed by a remote refusal. Android pairing over non-loopback uses the same plaintext-over-user-managed-private-network policy as desktop peers (Tailscale encrypts); `usesCleartextTraffic` stays false because gRPC uses managed sockets, which is unverified on device. Planned, not implemented: adaptive sampling (about 1 s while viewed, about 30 s otherwise) and tiered retention (7 days full, hourly rollups 1 year, events for years, pinned incidents forever). Alert rules are not implemented; the app shows an honest empty alert list.
- Revisit condition: Device testing of Doze/OEM battery behavior, or a requirement for desktop-initiated push to phones.

### T033 Phone pairing by QR plus proof-authenticated typed code

- Status: Accepted (working decision)
- Decision: The desktop admin page shows a 10-minute invitation as an inline SVG QR code (server-rendered with QRCoder) and its text payload. The typed fallback is a one-time 10-character Crockford base32 code resolved through a new additive unary `PeerPairingCodeService.Redeem` RPC on the peer port: the phone sends a PBKDF2-derived lookup id (never the code) and verifies an HMAC proof keyed from the code over the returned peer id and fingerprint, then runs the normal bilateral pairing. Codes live in an in-memory registry (single-use, expiring, bounded, globally rate-limited, uniform failures).
- Reason: Embedding a fingerprint fragment in a short code would be too long or too weak; a plain lookup without proof lets an impostor at the typed address supply its own fingerprint. A code-keyed proof is the simplest option that keeps the code short and not sufficient for impersonation, and needs no new trust anchor. A compact in-repo QR encoder was rejected in favor of a vetted package.
- Evidence: Core, Transport, contract, LocalApi (ZXing round trip), and loopback gRPC mobile tests listed in `docs/TESTING.md`.
- Consequences: Codes are lost on desktop restart; a failed pairing after redemption needs a new code; a burst of wrong guesses locks redemption for a minute; Peer.Node must share one registry between the admin page and the peer host.
- Revisit condition: Needing multi-source rate limiting, offline code issuance, or desktop-to-desktop typed pairing.

### T035 Tiered retention with hourly rollups

- Status: Accepted (working decision; thresholds are configurable defaults)
- Decision: Keep full-detail raw observations 7 days, hourly rollups 365 days, and pinned-incident windows indefinitely. "Events" are not an alerting engine yet: a transition record (first record of a subject/signal series, or observation quality differing from the previous record of that series) is kept for 5 years. Locally originated raw records that any active trusted peer has not acknowledged (replication cursor below the sequence, missing cursor counts as 0) are held past 7 days but never beyond a 30-day hard cap; revoked peers do not hold records. Replicated records from other origins are pruned by age only. Pruned sequence runs are recorded and answered to replicas as `RANGE_PRUNED` with a resume sequence. Rollups are keyed by (origin peer, subject, signal, UTC hour) without stream.
- Reason: Adaptive sampling will make raw volume high, while long-range views need only aggregates and state changes; replication must stay explicit about compaction.
- Evidence: `PeerRetentionTests`, `PeerStoreTests` schema v4 migration test.
- Consequences: Rollup hours older than raw retention are finalised once, so records replicated later into a finalised hour are not rolled up. Unpinning makes the window prunable on the next pass but never restores pruned data. Quality-only transitions miss value-only state changes (for example collector status text); richer event kinds need a real event model. A lagging or offline peer past the hard cap receives `RANGE_PRUNED` and loses raw detail for that range (rollups/transitions remain). Receiver-side RANGE_PRUNED handling and the pin API are specified in T041; a pin management UI is not implemented.
- Revisit condition: When an alerting/event engine or adaptive sampling lands, or retention storage cost is measured on real data.

### T037 Remote alerts delivered through the Android app

- Status: Accepted (user-confirmed: remote alerts go through the Android app itself; desktop peers alert the phone directly over Tailscale; no third-party or self-hosted push server).
- Decision: Rule evaluation has a single execution owner: the peer that collected the observation (provenance LocalOrigin and origin peer is this peer). Replicated copies are never evaluated, so they cannot re-fire. The peer-not-seen rule is owned by the observing peer for its own trusted peers (last seen = latest of pairing time, an authenticated session touch, and the newest replicated record received from that peer). Built-in rules only (peer silent, observation Unavailable/Error beyond a grace, numeric thresholds from a typed `AlertRulesConfig`); there is no user rule editor yet. An alert is an episode: `alert_id` is a hash of origin peer, rule, subject, and episode start, where the start is taken from stored observation time (never the evaluation wall clock), so repeat, delayed, or post-crash evaluation is idempotent; a partial unique index allows one active episode per owner, rule, and subject. Revision 1 is the raise, revision 2 the resolve. Alert events are retained (never deleted); only delivery bookkeeping (outbox rows) is purged after 30 days. Delivery is a per-recipient durable outbox (default expiry 7 days), at-least-once: the phone pulls with `AlertPull`, persists, then acknowledges with `AlertAck`; the desktop clears rows only on acknowledgement. A trusted peer that pulls is registered as a recipient (and queued all active alerts), so subscription is the phone's explicit act of syncing; there is no per-device alert setting yet. The same messages and outbox serve a held-open live session: `AlertBatch` may be sent unsolicited (empty request id) and `AlertDeliveryService.WaitForPushAsync` wakes on an enqueue; wiring that push into the session loop belongs to the live-session work. The phone stores alerts in an inbox keyed by alert id (higher revision wins) and the Android service marks a notification done only after Android accepted it, so a missed notification is re-raised after the next sync or restart. Alert tables live in `peer.db` under their own `alert_schema_version`, separate from PeerStore's migration chain.
- Reason: Phones are outbound-only, so push means queue durably and deliver on the next session; this needs no third-party egress and keeps notification state auditable and independent of UI and AI.
- Evidence: Real-SQLite rule tests (determinism, idempotency, episodes, ownership), outbox durability across store reopen, and loopback gRPC end-to-end tests (raise, phone persists, ack clears outbox, revoked and other recipients receive nothing). Android notification posting is unverified (no APK build here).
- Consequences: An offline phone learns of an alert only at its next session; with the current 15 s polling that is near-real-time while the service runs, and none while it is stopped or Doze-delayed. An older desktop that does not know `AlertPull` answers InvalidMessage and the phone skips alerts without failing replication. Notification channels beyond the phone, escalation, cooldown/debounce policy, rule editing, and reassigning rule ownership on collector failover remain open.
- Revisit condition: Device testing shows background delivery is unreliable (consider an opt-in push transport), or a rule editor or multi-owner failover is required.

## Rejected directions inherited from the product specification

- Generic progressive-information-density dashboard.
- Infinite freeform workspace customization.
- Simple topology as a sufficient product concept.
- Instrument panel, infrastructure atlas, or focus-and-periphery as the primary visual identity.
- Decorative gestures that can dispatch real actions.

### T042 Alert push over live sessions, debounce and cooldown defaults

- Status: Accepted for the push mechanism (user-confirmed: alerts go phone and desktop directly over Tailscale, no push server, phone needs real-time feedback). The numeric defaults below are WORKING DEFAULTS the owner has not chosen; revisit them.
- Decision: On a held-open session the desktop starts an alert push loop after the peer's first successful `AlertPull`: `AlertDeliveryService.WaitForPushAsync` wakes on an outbox enqueue and the batch is sent unsolicited (empty `request_id`). After each push the loop waits for the phone's `AlertAck` (sent after its durable write) before reading again, and redelivers after 5 s without one, so acknowledgements and the durable at-least-once outbox are unchanged. The phone's `AlertPull` on a live session becomes a 30 s safety net for missed wakeups and for desktops that cannot push (previously 5 s); an unsolicited batch does not clear the outstanding-pull flag. Rule debounce: hold = `ThresholdRule.Sustained` (disk-free 5 min, CPU 10 min), `UnavailableAfter` 2 min, `PeerSilentAfter` 10 min; cooldown = `AlertRulesConfig.RefireCooldown` 2 min (owner decision 2026-11-22, decision card), measured from the previous episode's resolve to the evaluation time. A breach inside the cooldown is delayed, not dropped: once the cooldown passes and the condition still holds it raises with its true episode start, so ids stay deterministic. Cooldown is per owner, rule and subject; resolves are never delayed. `GET /api/v1/alerts` reports per-recipient delivery state (`deliveries[].deliveredAt` from the acknowledgement); there is no user acknowledge or mute data, so it reports none.
- Reason: Phones are outbound-only, so push means a held-open phone-initiated session; waking on the enqueue gives sub-second delivery without a third-party service, and waiting for the ack avoids a hot resend loop because pending rows stay readable until acknowledged.
- Evidence: `LiveSessionTests.DesktopAlertReachesTheConnectedPhoneInboxWithinTwoSecondsAndIsAcknowledged` (real runtimes, loopback gRPC, SQLite; about 220 ms for raise and 125 ms for resolve, and it fails with push disabled because only the 30 s pull remains), `AlertRuleTests` hold and cooldown cases, `LocalApiHostTests` delivery state.
- Consequences: A phone that is offline, Dozing or has no live session still gets alerts only at its next session. A desktop older than this change never pushes, so a phone paired with it sees alerts within 30 s instead of 5 s. A resolve inside the cooldown is not delayed but a quick re-fire is held for up to 2 minutes. Real Tailscale/Android latency is unmeasured.
- Revisit condition: The owner picks alert defaults, device testing shows pull-only is still needed for Doze, or per-rule cooldowns or escalation are required.

### T034 Staged update pipeline, consent, and platform apply/rollback

- Status: Accepted. Consent policy confirmed by the project owner: download and verify automatically, each computer waits for approval before installing ("ask me first"); `auto` stays a one-line config option.
- Decision: `MonitorApp.Update` runs check (HTTPS fetch of the signed manifest) -> verify (T014 checks plus artifact size/SHA-256) -> stage (download to an owner-only staging directory, verify, atomic rename) -> apply behind `IUpdateApplier`, with a persisted `Available/Staged/Applying/Applied/RolledBack/Failed` state file and append-only audit log. Updates are downloaded, verified, and staged automatically but applied only on an explicit `UpdateApproval`; there is no silent install. Staged bytes are re-verified immediately before apply. Anti-rollback: equal/older versions are rejected and the highest manifest version ever seen is persisted; the only escape is a per-version, signed-manifest-only `DowngradeAuthorization` with approver and reason, audited. Linux: `dpkg -i`/`rpm -U` of the staged package, service restart, health marker (fresh file reporting the target version); failure reinstalls the retained last-known-good package, which is replaced only after the new version is healthy. Windows: side-by-side `versions/<v>` directories with a `current` directory symlink swapped while the service is stopped, health check, swap back on failure. Android: no in-app silent install; a verified staged APK is offered for the system installer and reconciled on next launch.
- Reason: Peers are always-on system services on user machines; an unattended replacement that cannot prove health or restore the previous build would turn an update into an outage, and unattended apply widens the trust surface beyond the signed manifest.
- Evidence: Real signatures and files, a real `dpkg-deb`/`dpkg --root` sandbox upgrade and rollback, real temp-directory Windows layout swap, and fake process/health/service seams for failure orchestration in `MonitorApp.Update.Tests`.
- Consequences: Corrupt update state fails closed. A crash during apply is reported as `Failed` on next start, never assumed successful. The first Linux package must seed the retained package (`RetainedPackageStore.Promote`) or the first rollback is `Failed`. Windows execution, systemd integration, rpm, node/service wiring, Android UI/PackageInstaller wiring, and trusted-key delivery remain unverified or TODO.
- Node wiring (preview): `NodeUpdateAgent` polls GitHub pre-releases `preview-*` (HTTPS, repository from `update-key.json`), policy `MONITOR_APP_UPDATE_POLICY` (default `approve`), local API `GET /api/v1/update` and antiforgery `POST /api/v1/update/approve`. Because the service is unprivileged and would be restarted by its own update, installation is a root helper (`MonitorApp.Peer.Node --apply-update`, started by a systemd path unit) that re-verifies the signed manifest and digest itself and uses `LinuxPackageApplier`. Preview versions are `0.1.<run_number>` (monotonic). Windows apply, phone approval, and fingerprint step-up for approval are TODO.
- Revisit condition: If the user selects automatic apply in a maintenance window, add it as an explicit policy that still requires health-gated rollback; revisit when package-manager repositories (apt/dnf) replace direct `dpkg`/`rpm` invocation.

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

### T036 Shared Avalonia client over a peer data-source abstraction

- Status: Accepted (working choice; Avalonia remains subject to platform proof).
- Decision: One client codebase. `MonitorApp.Client.Core` (net10.0, no UI dependency) holds view models and `IPeerDataSource` (runtime, peers, pairings, catalog, state, history, approve/reject/revoke, plus `StateChanged`/`SupportsLiveUpdates` for push). The desktop head uses the loopback HTTP adapter `LocalApiClient` (antiforgery handshake per POST, cursor paging with a page cap, errors mapped to unreachable / validation / incompatible). `MonitorApp.Client.Desktop` (Avalonia 11.3, compiled bindings) only contains views. An Android head, which has no local HTTP API, would implement `IPeerDataSource` over the in-process mobile runtime and its replicated SQLite store and reuse Core and the views; it is not created yet.
- Reason: Phones must show live data from desktop peers without an HTTP server; views and view models must not depend on a transport. Avalonia is pinned at 11.3.x (DataGrid 11.3.13) because the 12.x line is not adopted.
- Evidence: Core tests run the real `LocalApiHost` with seeded SQLite records and exercise paging, unavailable values (never shown as 0), validation errors, antiforgery approval, revoke confirmation, unreachable node, polling and push update paths. A headless Avalonia test binds all sections.
- Consequences: Unavailable, denied, or stale readings are shown as states. Approved pairings stay listed as Approved until the peer completes pairing. Alerts will be a new `SectionViewModel` plus one DataTemplate. Desktop GUI and any Android head are not visually or device verified. Live push has only a test adapter; no production source raises it yet.
- Revisit condition: If Avalonia fails on Android or a target platform, or a push-capable source needs a different contract than `StateChanged`.

### T038 Live held-open sessions and adaptive sampling interest

- Status: Accepted (user direction: phone as mobile command center; about 1 s sampling while viewed, about 30 s otherwise).
- Decision: Held-open `PeerSession`s stream newly committed origin records both ways (wake on an in-process `PeerStore.Live` signal, bounded in-flight batches, gap acknowledgement rewinds). The phone keeps one long-lived session per paired desktop; the dialer always asks the remote to push its own origin because the remote may be unable to dial back. A peer declares `LiveInterest` leases; the receiving origin samples at 1 s while any unexpired interest exists. Interest is honoured only for the receiver's own data, only from trusted authenticated peers, and is bounded. UIs consume `PeerStore.Live` or loopback SSE `/api/v1/live`.
- Reason: The old connect-drain-close cycle advertised origin availability only at session start, so a held-open session could not deliver new data and the phone could only poll.
- Evidence: Real loopback gRPC and SQLite tests in `MonitorApp.Peer.Mobile.Tests`, `MonitorApp.Peer.Node.Tests`, `MonitorApp.Peer.LocalApi.Tests`, `MonitorApp.Peer.Application.Tests`, `MonitorApp.Peer.Persistence.Tests`.
- Consequences: Two desktops that dial each other push the same records twice (idempotent; stale acknowledgements are tolerated instead of failing the session). Interest is not relayed across hops. Alerts are still pulled (every 5 s on a live session). Android behavior under Doze and mobile network changes is unverified.
- Revisit condition: Measured battery cost on a device, a need for per-signal sampling, or relayed multi-hop viewing.

### T040 One adaptive Avalonia UI for desktop, phone, and tablet

- Status: Accepted (user direction: the phone and Android tablet are a mobile command center as usable as the desktop, on one shared client).
- Decision: All screens, styles, and the `LineChart` live in `MonitorApp.Client.Ui` (net10.0, Avalonia 11.3.x, no desktop-only APIs). `MainView` is the only shell: below 720 px wide it uses an overlay menu plus a bottom bar of the first four sections; otherwise an always-open side pane. Lists are cards that wrap, not data grids; interactive targets are at least 44 px; nothing is hover-only. Heads stay thin (desktop window plus HTTP adapter; Android `AvaloniaMainActivity` plus `InProcessPeerDataSource`). Optional capability interfaces (`IAlertSource`, `IViewingSource`, `IPairingInitiator`, `IHostServiceControl`) keep `IPeerDataSource` small. Live sections (`LiveSectionViewModel`) run only while on screen and declare viewing interest for this device and every trusted peer.
- Reason: Two UIs would drift; the phone has no HTTP server, and the desktop adapter and the in-process adapter differ only in transport.
- Evidence: Headless tests at 400x800 and 1100x760; `InProcessPeerDataSourceTests` over a real runtime and desktop peer; Android compile only in CI.
- Consequences: Desktop loses sortable tables (cards show the same fields). History lists the newest 200 readings (the chart and loaded rows hold all). Camera QR scan remains open. The Android lock file must be regenerated in CI.
- Revisit condition: Avalonia fails on a target device, or dense desktop tables are required again (add a width-gated table view).

### T039 Remote monitoring control with a biometric presence gate

- Status: Accepted (user direction: phone as mobile command center; first commands are monitoring controls only; security is paramount; high-risk actions require biometric verification).
- Decision: Add signed `ControlCommand`/`ControlResult` messages to the authenticated `PeerSession` (additive oneof fields 30-31). Commands are signed by the issuer's identity key over an explicit canonical encoding (issuer, target, session id, issued/expires, nonce, action, payload) and are accepted only for an actively trusted issuer, this target, this session, within the validity window, with an unseen nonce, and within the per-peer grant. Results are durable and keyed by (issuer, command id); an identical resend returns the stored result (`replayed`), a different command under the same id is refused. Every attempt is audited append-only. Normal tier: resume, acknowledge, unmute, reject pairing, short pause (at most 1 h), short mute (at most 24 h), sampling 10 s-1 h. Elevated: longer or open-ended pause, mute over 24 h. High: pairing approval. Elevated and high commands also need a signature from the issuer's separately enrolled user-presence key (P-256, per-use biometric on Android). The presence key is enrolled by a command carrying a proof of possession but stays pending until the local administrator approves its fingerprint on the desktop; a remote peer can never activate its own key and no grant can waive the presence requirement. Manual pause/interval overrides carry an explicit expiry or last until cleared (persisted across restart by T047), never override live-viewing interest for the interval (the faster wins) and always win over interest for pauses. Muted alerts stay queued in the outbox and are delivered after unmute or expiry (subject to the outbox TTL).
- Reason: Telemetry reads and operational changes are different permission domains. Reusing the authenticated session avoids a second transport, while command-level signatures, session binding and nonces keep a captured or replayed message useless and make each action attributable and auditable independent of the transport.
- Evidence: `ControlTests` (real loopback gRPC, SQLite, scheduler, alert store, pairing coordinator), `AlertMuteTests`, `MonitoringControlStateTests`.
- Consequences: Owner decision (2026-11-22, decision card): deny by default. A trusted peer without an explicit grant row may issue no control command, including presence-key enrollment, until the administrator grants it on that computer (`POST /api/v1/control/grants/{peerId}`); pairing approval additionally needs an approved biometric key. A phone whose OS invalidates the key (new fingerprint) must be re-approved locally. Pause is desktop-wide; mutes are per recipient (T047). Desktop-local biometric (Windows Hello, fprintd) for high-risk UI actions is deferred. Operational actions (power, services, scripts) remain out of scope and would need their own decision, grants and tiers.
- Revisit condition: Adding operational actions, multi-user roles, per-peer-kind defaults, persisting overrides across restarts, or a hardware-attested presence key (Android key attestation).

### T044 Phone approval of staged updates over the signed control channel

- Status: Accepted (owner direction: updates download and verify automatically, each computer waits for the owner's approval, the phone is the mobile command center, biometric for high-risk actions, grants deny by default).
- Decision: Extend the T039 channel additively. `ApproveUpdate{version, artifact_sha256}` is a `ControlCommand` payload (field 18), classified High (presence signature required) and permitted only by a new `ControlGrant.UpdateApproval` flag (default false; `control_grants.update_approval`, control schema v2). Read-only `UpdateStatusRequest/Response` (session field 32) returns installed version, staged version and artifact SHA-256, state, last error and whether approval is pending, only to an active trusted peer holding the grant. "Manifest digest" is the artifact SHA-256 recorded from the signed manifest when the update was staged (the agent re-verifies it before applying). The desktop refuses with `staged_update_changed` when version or digest differ, and `ILocalUpdateController.ApproveAsync(version, digest)` re-checks atomically with starting the apply; success takes the same path as `POST /api/v1/update/approve`. `IRemoteUpdateGateway` (Peer.Control) is implemented by `RemoteUpdateGateway` over the controller, so Peer.Control does not depend on the update library.
- Reason: The phone must be able to act on a staged update without being able to choose what is installed or to approve a release it has not seen; binding to version and digest makes the biometric confirmation specific to what was displayed.
- Evidence: `RemoteUpdateApprovalTests` (real loopback gRPC, real `NodeUpdateAgent` with fake fetch/download/applier: no grant, no presence, applied exactly once, replay idempotent, digest/version mismatch, nothing staged) and `NodeUpdateAgentTests.BoundApprovalRequiresTheExactStagedVersionAndDigest`.
- Consequences: Existing grants do not gain update approval. Enrollment of a presence key is allowed for a peer holding only the update grant. A mismatch tells the user to re-read status. Not verified on a real phone or with a real package install.
- Revisit condition: Adding rollback/skip commands, per-version policies, or a stronger binding to the signed manifest envelope hash.

### T049 Per-recipient alert preferences

- Status: Accepted (TODO follow-up).
- Decision: The alert-owning desktop stores per-recipient delivery preferences in `alert_preferences` (alert schema v4, migrated from v3): a minimum severity (default Unspecified, meaning every severity) and a set of opted-out rule ids ("rule kinds", for example `threshold.cpu_high`). `AlertStore.RaiseAsync`, `ResolveAsync` and `RegisterRecipientAsync` skip the outbox row for a recipient whose preferences exclude the alert, in the same transaction; the alert is still recorded as an event. Preferences apply to alerts raised or resolved after they are set; rows already queued are unchanged, and a resolve follows the same filter as its raise. A phone sets its own preferences with the new signed control command `SetAlertPreferences` (additive `ControlCommand` field 19, `min_severity`, repeated `muted_rule_kinds`): normal tier, replaces the previous preferences, the recipient is always the authenticated issuer, and it needs the same grant as mute (monitoring controls). The local administrator can read and set any recipient's preferences through `GET /api/v1/alerts/preferences` and `POST /api/v1/alerts/preferences/{peerId}` (antiforgery-protected, loopback), `LocalApiClient.GetAlertPreferencesAsync`/`SetAlertPreferencesAsync`. `IControlSource.SetAlertPreferencesAsync` exposes the command to the shared client.
- Reason: A phone should not need to receive every alert, and the filter must be applied where the outbox is written so a filtered alert costs nothing and cannot reappear after reconnect. Preferences are narrowing only, so the default and a lost row keep today's behaviour.
- Evidence: `ControlTests.PhonesSetTheirOwnAlertPreferencesAndOnlyMatchingAlertsAreQueuedForThem` (two phones, grants, bounds, filtered events still recorded), `AlertStoreTests.SchemaV3MigratesToPreferencesWithoutLosingData`, `ControlReadTests.AlertPreferencesRoundTripThroughTheLocalApi`, `ControlSourceTests`.
- Consequences: Rule kinds are exact rule ids; there is no pattern or category matching, and unknown ids are accepted. A phone that raises its minimum severity does not lose alerts already queued, and alerts filtered earlier are not back-filled when preferences are loosened (only the next raise or resolve is delivered). The `/api/v1/alerts` delivery view shows no rows for filtered recipients. No UI exists yet in the shared client.

### T047 Alert mutes are per recipient; manual overrides persist across restart

- Status: Accepted (TODO follow-up; supersedes the "in memory" and "not per recipient" parts of T039).
- Decision: A mute issued by a phone silences alert delivery to that phone only; `alert_mutes` is keyed by (recipient, scope) and unmute is scoped to the issuer. Existing global mutes migrate to the issuing phone's own mute (alert schema v3). Manual pause and sampling-interval overrides are saved in the control store (control schema v3) after each applied command and restored by `ControlService.RestoreMonitoringAsync` at node start; overrides whose expiry has passed are not restored. Control audit entries are unchanged.
- Reason: One phone's mute must not blind other recipients, and an operator's explicit pause should hold across a desktop reboot or service restart, with expiries as the safety bound.
- Evidence: `ControlTests.ManualOverridesSurviveANodeRestartUnlessTheyHaveExpired` and `AMuteFromOnePhoneSilencesOnlyThatPhone`.
- Consequences: An open-ended pause (elevated, biometric-protected) now persists until resumed, including across restarts. The local administrator cannot yet mute on behalf of a phone.

### T046 Camera QR scanning on Android through framework camera2 and ZXing

- Status: Accepted (owner-confirmed: a phone pairs by scanning the QR on the computer's admin page, with the typed short code as fallback).
- Decision: Scanning is a platform service behind `IQrScanner` (Client.Core). The UI binds to `IQrPairingSource.ScanAndPairAsync`, implemented by `InProcessPeerDataSource`: scan, `PeerPairingInvitation.Decode`, then the existing invitation pairing (desktop-side approval is unchanged). Android implements it with a camera2 preview (`TextureView` plus a YUV_420_888 `ImageReader`) whose Y plane is decoded by ZXing.Net `QrFrameDecoder`; only text starting with `monitor-pair1.` is accepted, other QR codes are ignored. `QrScanActivity` requests the `CAMERA` permission itself; denial, no camera, or a camera failure raise `QrScanUnavailableException`, which the data source turns into an `ApiValidationException` that points at the typed code.
- Reason: Sideloaded APK, so no Google Play Services code scanner; CameraX via Xamarin bindings would add about twenty transitive AndroidX/Kotlin packages (and a large lock-file and vulnerability surface) for a one-screen preview, whereas camera2 is in the framework and ZXing.Net is already pinned.
- Evidence: `QrScanPairingTests` (real QR encode, frame decode with row padding and noise, real desktop pairing through the real runtime, denial/cancel/junk paths). The Android activity itself has no automated test.
- Consequences: Portrait-locked scanner; preview aspect assumes a landscape sensor buffer shown in portrait. `CAMERA` is a new dangerous permission (see `docs/SECURITY.md` if the permission list there changes); frames are decoded in memory and never stored or sent. Android compile and device behavior are unverified until the android-build job and a phone run.
- Revisit condition: Devices whose sensor orientation or preview aspect is wrong, slow decoding, or a maintained binding for a Google-independent scanner.

### T045 Desktop-local user presence for high-risk local administration

- Status: Accepted (owner direction 2026-11-22: security paramount; fingerprint verification on laptop/tablet/phone readers is welcome for high-risk actions; the phone side is T039).
- Decision: High-risk mutations on the loopback local API (approve a pairing, approve a phone's presence-key enrollment, grant any control right, approve an update install) require a user-presence proof once a presence key is enrolled. Because the node is a service with no interactive desktop, the person is verified in the interactive desktop client process, which holds a locally enrolled presence key and signs a one-shot challenge from the node. Protocol: `POST /api/v1/local-presence/challenge {action, subject}` returns a random id and nonce, a 60 s lifetime (maximum 5 min), and the exact payload `monitor-local-presence-v1 \n id \n nonce \n action \n subject \n expiry`; the client rebuilds the payload itself and refuses to sign a mismatch, verifies the person, signs (ECDSA P-256 IEEE P1363 or RSA 2048+ PKCS#1 v1.5, SHA-256) and retries the mutation with `X-Presence-Challenge` and `X-Presence-Signature`. The node consumes the challenge on first use (valid or not), requires matching action and subject (pairing id, enrollment id, update target version, or `peerId|monitoring=..|pairing=..` for grants), a live challenge, and the enrolled key. Rejecting, revoking, and lowering grants never need presence (they only reduce trust). Policy `LocalPresencePolicy` (`--local-presence` or `MONITOR_APP_LOCAL_PRESENCE`): `off`, `when-enrolled` (default; unenrolled nodes behave as before and the UI shows "fingerprint not set up" from `GET /api/v1/local-presence`), `required` (fail closed with `presence_not_enrolled` until a key is enrolled). Enrollment is trust on first use with proof of possession (`presence.enroll` signed by the new key, subject = its SHA-256 fingerprint); a second key is refused; removal (`presence.remove`) needs a proof from the enrolled key. The enrolled public key is stored in `local-presence.json` (0600) in the data directory; an unreadable file fails startup rather than silently disabling enforcement. Client side: `ILocalUserPresence` (Windows Hello `UserConsentVerifier`, Linux `fprintd-verify`, unavailable) and `ILocalPresenceProvider` in `MonitorApp.Client.Core`; `LocalApiClient` retries a `presence_required` refusal with a proof automatically. Providers: `WindowsHelloPresenceProvider` (KeyCredentialManager, RSA, hardware-backed, Hello prompt at every signature) in the Windows-only project `MonitorApp.Client.Presence.Windows` (TFM `net10.0-windows10.0.19041.0`, in the solution; it compiles on Linux with the SDK's Windows targeting), and `SoftwareLocalPresenceProvider` (ECDSA P-256 key in an owner-only file, signature released only after the verifier succeeds) with `FprintdUserPresence` in `MonitorApp.Client.Presence`.
- Reason: The loopback API trusts any local process that can fetch a CSRF token. A presence proof from a key that only the interactive person can use raises the bar for same-machine malware that is not running as that person's session, and gives the owner a fingerprint check on the actions that widen trust or install code. fprintd is invoked as a process rather than through a D-Bus library: no new package in the trust chain, no code generation, `fprintd-verify` already performs the claim/verify/release protocol and polkit handling, and the boundary can be tested with a real child process. The tool is run with no arguments from a fixed absolute path that must not be group/world writable, with a hard timeout that kills it; success requires exit code 0 and `verify-match`.
- Evidence: `LocalApiPresenceTests` (real host: policy matrix, binding to action/subject/key/lifetime, replay, TOFU and removal, RSA, persistence and corrupt store, update gating), `LocalPresenceTests` in `MonitorApp.Client.Core.Tests` (real `LocalApiClient`, real software provider and key file, real fprintd-verify process against fake tools; only the platform biometric prompt is a stub), `NodeServiceHostTests` (option parsing).
- Consequences: Local root/Administrator can bypass (stop the node, delete `local-presence.json`, or edit the database), and malware running as the same user can read the Linux software key file and sign without a fingerprint; Windows Hello key credentials do not have that weakness. The built-in admin web page cannot produce proofs, so once enrolled its approve buttons fail until the page is taught to use the desktop client. Desktop UI wiring is not done here (UI team): inject an `ILocalPresenceProvider` into `LocalApiClient.Presence`, call `EnrollLocalPresenceAsync`, and catch `LocalPresenceCancelledException`/`LocalPresenceRequiredException`/`LocalPresenceRefusedException`.
- Revisit condition: A TPM-backed Linux key (tpm2-pkcs11) or a polkit-mediated signer if same-user malware matters; moving presence into the gRPC/peer path; enrollment at install time instead of TOFU.

### T043 Windows in-place update apply via a detached SYSTEM helper

- Status: Accepted (user direction: updates download and verify automatically, each computer waits for owner approval, Windows runs as an always-on service).
- Decision: Windows installs use a side-by-side layout (`versions\<v>\node|desktop`, `current` directory symlink, service runs `current\node\MonitorApp.Peer.Node.exe`). After approval the service writes the same apply-request as Linux and starts the on-demand SYSTEM scheduled task `MonitorAppUpdate` (no triggers; the service SID may only run it). The task runs the node binary with `--apply-update <update dir> --install-root <root>`, which re-verifies everything from the signed manifest and runs `WindowsSideBySideApplier` with `ScExeServiceController` and the file health marker. The node enables apply for package kind `zip` only when the admin installer's `update-helper.json` marker exists (`NodeUpdateOptions.CanApplyHere`, install root from `MONITOR_APP_INSTALL_ROOT` in the service environment). The shared privileged flow was factored out of the Linux helper (`ApplyHelper.RunVerifiedAsync`); on Windows the staged path must also lie inside the update directory.
- Reason: A service cannot reliably replace itself, and must not be able to write its own binaries; a pre-registered, argument-fixed task is the least-privilege equivalent of the systemd path unit.
- Evidence: platform-neutral tests run the real helper, handshake, signed manifest, zip, and directory symlinks with a fake service controller; Windows-category tests run real `sc.exe`/`schtasks.exe` failure paths and the symlink swap. No real service install is exercised.
- Consequences: Legacy flat Windows installs must re-run `Install-MonitorApp.ps1` once to adopt the layout. A task or SDDL failure leaves the node on staged-only behavior with a visible failed-apply error, not a broken install. Services running through a symlinked path, task SDDL, and ACL inheritance are unproven until a real Windows run.
- Revisit condition: CI or hands-on Windows runs showing the scheduled-task route or symlinked service path does not work (fallback: a demand-start helper service with an SDDL-granted start right, or a junction instead of a symlink).

### T041 Node scheduling for retention and self-LAN, and receiver-side pruned ranges

- Status: Accepted (working decision; schedules and thresholds are configurable)
- Decision: The node runs two background jobs on `NodePeriodicTask` (a `TimeProvider`-driven schedule that runs once at startup, then every interval, never overlapping, recording failures without stopping): the retention pass every hour with the confirmed policy (raw 7 days, hourly rollups 365 days, events 5 years, pinned incidents until unpinned), and the self-LAN collection every 60 seconds. Self-LAN persists the node's own entities, claims and relationships (`SelfLanCollector`) and emits link state, speed, full-duplex and MTU as ordinary signed observations on the origin stream through `PeerSystemMetricsIngestionBridge` (subject `peer:{id}`, collector `monitor.lan.self`, signals `lan.link.{interface}.{up|speed_mbps|full_duplex|mtu}`, new `METRIC_UNIT_MEGABITS_PER_SECOND`, one `collector.health.lan` status). Values the host does not expose are `Unavailable`, never guessed; speed and duplex are only emitted for physical Ethernet/Wi-Fi. Relationships keep their first `ValidFrom` across runs; ones the host stops reporting get `ValidTo` at the collection time; a failed or unsupported collection changes nothing; a reappearing relationship is reopened under its original start (the gap is not kept). The local API gains `GET /api/v1/incidents`, `POST /api/v1/incidents/pin` (label up to 200 chars, UTC window up to 31 days) and `POST /api/v1/incidents/{id}/unpin`, with the same antiforgery check as every other admin mutation. For pruned ranges, the origin tells replicas explicitly and the replica records the range as intentionally absent in the same `pruned_origin_ranges` table (merged, durable), and its cursor skips it. Pull: a range request that starts in a pruned range is answered `RANGE_PRUNED` with `ProtocolError.pruned_range` (the exact origin, stream and first/last sequence); the connector calls `AcceptRangePrunedAsync`, sends the resulting acknowledgement and continues. Push: `OriginRecordBatch.pruned_range` carries the range that precedes the batch (or the whole batch when nothing follows), so an origin pushing to a server-side replica no longer fails on its own pruned ranges. The replica accepts a pruned range only from the actively trusted owner of that stream and only when it starts no later than the next sequence the replica needs, so a peer cannot hide missing records.
- Reason: Retention without a schedule never ran, and without explicit absence a lagging replica either looped on an error or kept a perpetual gap that blocked its cursor.
- Evidence: `NodeBackgroundTasksTests`, `SelfLanScheduleTests`, `RangePrunedReplicationTests` (two file-backed stores), `PeerNodeRuntimeTests.ReplicaMarksPrunedRangeAbsentAndConvergesWhetherTheOriginOrTheReplicaDials` (real gRPC, both dial directions), `LocalApiIncidentTests`.
- Consequences: The scheduled LAN collection adds roughly four observations per interface per minute to the origin stream (virtual and overlay interfaces: two); tests that compare exact record counts start the node with `NodeLiveOptions(CollectSelfLan: false)`. The Linux LAN collector needs no privilege; unreadable VLAN/route files are "unknown". Pins are local: they protect only this peer's raw copy and do not replicate. A live-session push that hits a pruned range sends the range and continues; a pull error mid-live-session ends that session after the acknowledgement and the next session resumes. Android and Windows LAN collection remain unverified on hardware.
- Revisit condition: When pins must replicate, when per-interface observation volume matters on large hosts, or when a switch adapter supplies real uplink/port topology.

- Consequences: Two desktops that dial each other pushed the same records twice at first (fixed by T048; stale acknowledgements remain tolerated). Interest is not relayed across hops. Alerts are still pulled (every 5 s on a live session). Android behavior under Doze and mobile network changes is unverified.
- Revisit condition: Measured battery cost on a device, or relayed multi-hop viewing.

### T048 Per-signal fast sampling and a single live push channel per peer pair

- Status: Accepted (follow-up to T038).
- Decision: `LiveInterest.signal_ids` now narrows fast collection: the registry exposes the union of named signals (an empty list from any viewer means all), the node scheduler stores only those signals while interest is active, and still runs a full collection once per normal interval so other history does not go quiet. Viewers declare signals with `BeginViewing(remotePeerId, signalIds)`. When two peers dial each other each holds two sessions to the other; `LivePushChannels` picks one pushing session per remote peer: the session initiated by the peer with the lower peer id (ordinal), else the only or most recent one. The other session stays open (heartbeats, interest) but does not push, and takes over from the persisted replication cursor if the active one ends.
- Reason: Removes duplicate sends and lets a viewer of one metric cost one metric's collection and push, with no protocol change.
- Evidence: `PeerNodeRuntimeTests.TwoNodesDialingEachOtherReceiveEveryLiveObservationOnce` and `ViewerAskingForOneSignalReceivesOnlyThatSignalAtTheFastRate` (real loopback gRPC and SQLite).
- Consequences: Several viewers with different signals share the union, and every viewer receives the union's records. The phone's own collection is narrowed the same way (T053). Duplicates remain possible briefly around a channel switch (idempotent).
- Revisit condition: Per-viewer push filtering, or measured cost of the union approach.

### T050 Phone transport in the LAN model

- Status: Accepted.
- Decision: The phone maps Android's coarse `NetworkTransportSummary` into the desktop's self-interface model (`PhoneTransportMapper`) and runs the unchanged `SelfLanCollector` (entities, claims, relationships, ValidTo) and `LanLinkObservations` on its own collection tick, right after the ordinary reading and from that same platform read. Each transport is one synthetic interface (`wifi`, `cellular`, `ethernet`, `vpn` as overlay, `other`); it is up only when the network is validated, and a transport that disappears is absent so its `AttachedTo` relationship gets a ValidTo. `NetworkTransportSummary` gained optional `IsMetered` and down/up bandwidth estimates (additive, defaults null); they become `lan.link.{if}.metered` and `.bandwidth_down_mbps`/`.bandwidth_up_mbps`, attributed only when exactly one non-VPN transport exists, otherwise Unavailable. `InterfaceMedium.Cellular` was added.
- Reason: One LAN model and one persistence/ValidTo path for desktop and phone, with no forked logic and no invented identity.
- Evidence: `PhoneTransportLanTests` (wifi, wifi+vpn, cellular, none sequence through `MobilePeerRuntime`).
- Consequences: Android hides interface names, MACs, subnets and gateways, so no segment, gateway or VLAN edges and no MAC claims exist for the phone; speed, duplex and MTU are Unavailable. Bandwidth values are platform estimates, not measured link speed. LAN failures are recorded in `LastLanError` and never fail ordinary collection. Each tick adds LAN records to the phone's origin stream.
- Revisit condition: Reading metered/bandwidth in `AndroidDeviceReader` on a device, or a need for per-network identity (SSID is deliberately not collected).

### T051 Alert acknowledgement state and escalation

- Status: Accepted.
- Decision: Acknowledgement reuses the existing T039 `alert_operator_acks` table (first acknowledgement wins; who and when) rather than adding a second one: a phone's signed `AcknowledgeAlert` control command records the phone's peer id, and the new loopback `POST /api/v1/alerts/{id}/acknowledge` (antiforgery token required, `LocalApiClient.AcknowledgeAlertAsync`) records `local`. `GET /api/v1/alerts` now returns `acknowledged`, `acknowledgedBy`, `acknowledgedAt`, `escalated`, `escalatedAt` and `escalation` on each delivery row. A Critical alert that is still raised and has no acknowledgement `AlertRulesConfig.EscalationAfter` (default 15 minutes, zero disables) after it was raised is escalated exactly once by the owning evaluator's tick: the escalation is recorded in `alert_escalations` and one escalation outbox row is queued per recipient whose preferences allow the alert, in a single transaction, so a node restart never escalates twice. Acknowledging or resolving deletes escalation rows not yet delivered. Alert schema is v5: `alert_escalations`, an `escalation` column in the outbox primary key (table rebuilt, rows kept) and `escalated` on the phone inbox. Protobuf, additive only: `AlertRecord.escalated = 13` (reserved range is now 14 to 20) and `AlertAckEntry.escalation = 3`; an escalated delivery repeats the raise at revision 1, and its acknowledgement clears both the escalation and raise rows, while a plain acknowledgement never clears an escalation row. The phone inbox re-arms its notification once when an escalated copy of the same revision arrives. `AlertDto` gained optional acknowledgement and escalation fields.
- Reason: The acknowledgement record already existed and was audited, so exposing and extending it avoids two sources of truth. Keeping escalation as a separate delivery of the same revision leaves revision semantics (1 raised, 2 resolved) and every existing recipient untouched, and the mute and preference rules apply unchanged.
- Evidence: `AlertEscalationTests` (window, once across restart, severity, preferences, ack and resolve stop it, v4 migration, inbox re-notification, evaluator on a controllable clock), `ControlTests.UnacknowledgedCriticalAlertEscalatesOnceWhileAcknowledgedOnesDoNot` (two phones over the real session plus the loopback API), `LocalApiHostTests` and `ControlReadTests` acknowledge round trips.
- Consequences: Escalation time counts from the alert's raise time, not from first delivery, so a node that was down for longer than the window escalates on its first tick. An alert acknowledged by one phone is acknowledged for everyone (no per-recipient acknowledgement), and acknowledgement does not recall the original delivery. Escalation is not shown with any distinct Android notification channel yet, only flagged on the alert. A phone muting the alert's rule still receives it after the mute lapses, as for any queued alert.
- Revisit condition: A need for per-recipient acknowledgement, repeated or multi-stage escalation, an escalation target other than the existing recipients, or an unacknowledge action.

### T052 Peer-state and update history feed

- Status: Accepted (working decision).
- Decision: Each node and phone records transitions (never heartbeats) of every trusted peer's connectivity state in `peer_state_transitions` in its own `peer.db` (`Paired`, `Online`, `Stale`, `Offline`, `Revoked`; timestamp, optional reason; only a change from the latest recorded state is stored). `PeerStateTracker` takes session up/down events from `LivePushChannels.ConnectivityChanged` (first session joins, last ends) on the node and from `MobilePeerRuntime`'s sync status on the phone, applied in order. A periodic reconcile (node: every 15 s; phone: when history is read) adds `Paired` (at the pairing time), `Revoked`, missed connects/disconnects, and `Stale` = offline for at least the "peer not seen" alert threshold. Session events are ignored while the host shuts down, so a node stopping does not record its peers offline. Retention prunes the table with the event horizon (5 years, T041). Update outcomes are read from the existing append-only `update-audit.jsonl` (`applied`, `rolledback`, `failed`, and `reconciled` settlements), which now also records the target version; older lines without a version report `unknown`. Loopback API: `GET /api/v1/history/peer-states?from=&to=` returns `[{peerId, state, at, reason?}]` and `GET /api/v1/history/updates?from=&to=` returns `[{version, outcome, at, detail?}]`, oldest first, capped at 5000; `to` defaults to now, `from` to seven days earlier, an unparsable instant, `from` after `to` or a span over five years is a 400. Clients use the optional `IHistorySource` (`LocalApiClient`, `InProcessPeerDataSource`; the phone returns no update events).
- Reason: The time-travel timeline needs "Fedora offline at 21:14" and "update 1.3 rolled back", which current state and the alert rule cannot answer after the fact.
- Evidence: `HistoryFeedTests` (two real nodes, peer drops and returns, read through `LocalApiClient`, range validation), `InProcessPeerDataSourceTests.HistoryFeedRecordsTheDesktopGoingOfflineAndBackAndHasNoUpdateEvents`, `NodeUpdateAgentTests.UpdateHistoryListsOnlyInstallOutcomesWithVersionOldestFirstWithinTheRange`.
- Consequences: A transition is observed by the node that sees it; histories are not replicated, so each device's timeline is its own view. A peer that was `Online` when the node crashed is recorded `Offline` at the next reconcile, not at the crash time. The phone records `Stale` only when its history is next read. The result cap truncates the newest entries of an oversized range.
- Revisit condition: When a timeline needs a replicated cross-device view, per-peer filtering, or paging beyond the cap.

### T053 Phone-side collection narrowed by live-interest signals

- Status: Accepted (closes the phone gap left open in the signal_ids decision).
- Decision: `MobilePeerRuntime`'s collection loop asks `LiveInterestRegistry.FastSignalFilter` for the phone's own peer id before each tick and passes the union to `PeerSystemMetricsIngestionBridge.RunOnceAsync(signalFilter)`, the same rule as `NodeCollectionScheduler`: while a viewer named signals, fast ticks store only those, and once per normal interval (`CollectionInterval`) a full collection still runs. The LAN model (`PhoneLanCollection`) is not signal-addressed and runs only on full collections. `CollectOnceAsync` stays a full collection.
- Reason: A viewer of one phone metric should cost one metric of collection, storage and push, and battery, with no protocol change.
- Evidence: `LiveCollectionNarrowingTests` (real phone loop, registry, bridge and SQLite).
- Consequences: A viewer of a `lan.*` signal gets it only at the normal interval. Several viewers share the union.
- Revisit condition: Signal-addressed LAN collection, or measured battery cost of the full normal-interval collection.

### T054 Per-peer alert rule overrides (user rule editor)

- Status: Accepted.
- Decision: Thresholds and enablement of the built-in rules can be overridden per peer: CPU above, memory above and disk free below (percent, 1 to 99) and "peer not seen" (minutes, 1 to 10080). A new built-in rule `threshold.memory_high` (90 percent for 10 minutes, Warning) exists so memory has something to override. Overrides live in `alert_rule_overrides(peer_id, rule_id, enabled, threshold)` (alert schema v6, additive migration) and are keyed by the peer the rule applies to: the owner itself for CPU, memory and disk (only the owner evaluates its own signals, so a remote peer's signal rule is set on that peer), a watched trusted peer for peer-not-seen. They are replaced as a set per peer (an empty set restores the defaults) and read by `AlertEvaluator` on every pass. A disabled rule reports healthy so its open alert resolves. They are not preferences: they change what fires for every recipient. One validator (`AlertRuleCatalog`) serves the loopback API (`GET /api/v1/alerts/rules`, `POST /api/v1/alerts/rules/{peerId}`, `LocalApiClient.GetAlertRulesAsync/SetAlertRulesAsync`), the remote command (`ControlCommand.set_alert_rule_overrides`, `IControlSource.SetAlertRulesAsync`), the store, and the phone client (which refuses bad values before any biometric prompt). Reading is a session read (`AlertRulesRequest`/`AlertRulesResponse`, field 34, `IControlSource.ReadAlertRulesAsync`).
- Security: a remote edit needs trust plus the monitoring-controls grant (deny by default), is classified Elevated (enrolled and approved biometric key, because it can silence alerts for everyone), is covered by the signed canonical encoding, and is audited (`set_alert_rule_overrides`, `read_alert_rules`) whatever the outcome. Invalid payloads are refused as `invalid_payload` and change nothing. The loopback edit needs the antiforgery token like the other local mutations.
- Reason: Extends the T049 path (same command, grant and read mechanics) instead of adding a parallel one, and keeps the single-owner evaluation model.
- Evidence: `AlertRuleOverrideTests` (overrides change when alerts fire, disable resolves, reset, ranges, migration from v5), `ControlSourceTests.RemoteAlertRuleEditsAreDeniedByDefaultElevatedValidatedAndAuditedThroughTheControlSource`, `ControlReadTests.AlertRuleOverridesRoundTripThroughTheLocalApiAndOutOfRangeValuesAreRejected`.
- Consequences: The new memory rule raises alerts on existing nodes whose memory stays above 90 percent. No editing of severity, hold time, new rule kinds or custom signals. Overrides are not replicated between peers; each owner holds its own. Peer-not-seen overrides are held by the observing peer. No desktop or phone UI yet.
- Revisit condition: Custom rules or severities, per-recipient thresholds, or a UI that needs finer grants than monitoring controls.
### T055 Relay of live interest across one hop

- Status: Accepted (follow-up to T038 and T048).
- Decision: `LiveInterest` gained `relay_hops_remaining` (field 4, additive; older peers read 0 and keep refusing foreign subjects). A viewer asks each connected peer to relay interest for a peer it watches but holds no session with, with 1 hop. A relay accepts only 1, only from an actively trusted peer, never for the requester itself, remembers it under the same lease clamp, signal and concurrent-interest caps as direct interest, and does not change its own sampling. It declares it only on its held-open session with the subject itself (merged with its own watch, signals unioned, empty meaning all) with 0 hops and a lease capped by the originating lease, renewed with the usual cadence; end, expiry, requester revocation or loss of the subject's trust withdraws it. The subject treats it as ordinary interest from the relay.
- Reason: The phone often reaches a desktop only through another desktop; the subject must sample fast for it, without trust becoming transitive.
- Evidence: `PeerNodeRuntimeTests.RelayForwardsInterestToTheSubjectOnlyWhileTheLeaseLivesAndNeverFurther` (four real nodes, loopback gRPC), `LiveInterestRegistryTests` relay cases.
- Consequences: A peer can only make a third peer sample fast if that peer is trusted by the relay and has an active session with it; forwarded interest has 0 hops and goes only to the subject, so loops and chains are impossible. A viewer sends relay requests to every connected peer lacking a direct session. Replication of the subject's records to the viewer through the relay is not part of this decision.
- Revisit condition: Transitive peer introduction or multi-hop record replication, or relay requests needing a routing hint.

### T056 Operator sections on the loopback administration page

- Status: Accepted (stopgap until the Avalonia client covers the same operations).
- Decision: The existing `/` page gains server-rendered sections for alerts with acknowledge, a per-peer alert-rule editor, control grants (grant, revoke, pending presence-key enrollments), recent peer-state and update history, pinned incidents (pin and unpin) and update status with approve. Peer-provided strings are HTML-encoded server-side. One small inline script (same CSP nonce) posts JSON to the existing loopback endpoints with the page's antiforgery token in `X-CSRF-TOKEN` and writes results with DOM text APIs only.
- Reason: The loopback APIs for T045 to T055 had no desktop-browser front end, so the owner could not operate them without scripting.
- Evidence: `LocalApiAdminPageTests` (every section rendered with real stored data, hostile strings escaped, every mutation 403 without the token and accepted with it, API validation text returned for the rule editor).
- Consequences: No new endpoints and no remote-capable surface. High-risk actions stay gated by the API: granting any permission, approving an update and approving a presence enrollment still need desktop presence when it is enabled, and the page only reports the `presence_*` refusal (it cannot verify the person). Revoking all permissions and rejecting an enrollment need no presence. History here shows the last 7 days, 50 rows per list; the JSON feeds remain the complete source.
- Revisit condition: The Avalonia client reaches parity, or the page needs presence verification (a browser cannot satisfy it today).

### T057 Controller-era inventory, Domain deletion, and remaining extraction plan

- Status: Accepted (closes the inventory item in `TODO.md`; extraction steps below remain open work).
- Decision: Every legacy project was traced by `ProjectReference`, `.github/workflows`, `eng/` (verify, release, packaging, smoke), `preview.yml`, `install-smoke.yml` and the Android head. Only `MonitorApp.Domain` and `MonitorApp.Domain.Tests` had no consumer anywhere (675 lines including lock files, three telemetry freshness types and one test class, none referenced by `Peer.*`, `Client.*`, `Update`, Android, CI, release or packaging) and were deleted and removed from `MonitorApp.sln`. No other project was deleted, and no lock file changed (the Android lock file was not touched). The remaining legacy projects stay until the steps below are done.
- Inventory (incoming references -> status):
  - `MonitorApp.Domain` (+ `Domain.Tests`): no incoming reference. Deleted.
  - `MonitorApp.Controller`: referenced only by `Controller.IntegrationTests`, but `eng/release/Publish-AlphaArtifacts.ps1` (called by `release.yml`) publishes `monitorapp-controller-linux-x64-VERSION.tar.gz`, and `eng/verify.ps1` runs its tests. Kept: release-referenced.
  - `MonitorApp.Agent.Service`: referenced by no project, but `Publish-AlphaArtifacts.ps1` publishes the three `monitorapp-agent-{windows,fedora,ubuntu}-*` archives from it (`release.yml`). Kept: release-referenced.
  - `MonitorApp.Persistence` (enrollment and telemetry SQLite stores, `Persistence.Tests`): referenced by `Controller` and `Persistence.Tests`; `eng/verify.ps1` runs its `Category=SQLite` tests. Kept: it is `Controller`'s dependency. No `Peer.*` project uses it (`Peer.Persistence` is independent).
  - `MonitorApp.Agent.Core` (+ `Agent.Core.Tests`, `Agent.Persistence.Tests`): referenced by `Agent.Service`, `Peer.Collection`, `Peer.Mobile`, `Peer.Node`, `Peer.Alerts.Tests`, `Controller.IntegrationTests`. Peer still uses `ISystemSnapshotCollector`, `SystemSnapshot`, `PlatformDetector`, `SystemSnapshotCollector` (Linux/Windows metrics), `TelemetryEnvelopeFactory` (`PeerSystemMetricsIngestionBridge`) and `HostIdentity`. Kept: live collector code.
  - `MonitorApp.Android.Core` (+ tests): referenced by `MonitorApp.Android` and `Peer.Mobile` (`DeviceReading`, `NetworkTransportSummary`, `BatteryReadingNormalizer`, `IDeviceKeySigner`, `EcdsaSignatureEncoding`). Kept; the Android build is not verifiable here.
  - `MonitorApp.Contracts` (+ tests): referenced by every `Peer.*` library, `Agent.Core`, `Android.Core`, `Controller`, `Persistence`. Holds `peer.proto`, `capability.proto`, `inventory.proto`, `common.proto`, `telemetry.proto` and `ProofPayload` (the P-256 proof payload used by `Peer.Core/PeerProofs.cs`) alongside controller-era `enrollment.proto` and `agent.proto`. Kept.
  - `MonitorApp.Update` (+ tests): referenced by `Peer.Node` and `eng/release/MonitorApp.ReleaseTool`. Manifest, signature, integrity, downgrade and apply logic are in live use. Kept as is.
- Reason: A deletion that would break the release job, verification script or a peer composition is not a cleanup. Removing the controller and legacy agent archives is a release-contract change that needs its own decision.
- Evidence: Full-solution Release build with warnings as errors and the complete test run after the deletion (see `docs/CHANGELOG.md`).
- Remaining extraction and deletion steps, in order:
  1. Release: change `Publish-AlphaArtifacts.ps1` to publish only the peer archives (and the `.deb`/`.rpm` from `eng/packaging`), update `docs/RELEASES.md` and the update manifest consumers, then drop the controller and agent artifacts. This unblocks steps 2 and 3.
  2. Delete `MonitorApp.Controller`, `Controller.IntegrationTests`, `MonitorApp.Persistence`, `Persistence.Tests`, the "Run real SQLite persistence" and "controller enrollment" steps in `eng/verify.ps1`, and `enrollment.proto` plus `EnrollmentContractValidator`/`EnrollmentStore` fixtures in `Contracts` and `Contracts.Tests` once nothing else uses them (check `Android.Core.EnrollmentCompletionBuilder`, `EnrollmentMaterial`, `PairingReceipt`, `ControllerUriPolicy` first).
  3. Delete `MonitorApp.Agent.Service`, `GrpcAgentSessionTransport`, `AgentSessionCoordinator` and `agent.proto`.
  4. Extract the still-used `Agent.Core` collectors (`ISystemSnapshotCollector`, `SystemSnapshot`, `PlatformDetector`, `LinuxSystemMetrics`, `WindowsSystemMetrics`, `HostIdentity`, `TelemetryEnvelopeFactory`) into `MonitorApp.Peer.Collection`, moving `Agent.Core.Tests` (the Windows tests and the Linux collector tests) with them, then delete `Agent.Core` and its spool (`SqliteTelemetrySpool`, `TelemetrySpool*`, `SpoolCapacityExceededException`) with `Agent.Persistence.Tests`. Peer replication uses `Peer.Persistence` and does not use the agent spool.
  5. Android: move `DeviceReading`, `NetworkTransportSummary`, `BatteryReadingNormalizer`, `IDeviceKeySigner` and `EcdsaSignatureEncoding` into `Peer.Mobile` and delete the enrollment-only types in `Android.Core`. This changes the Android project references and its lock file, so it needs the CI `android-build` job (the lock file cannot be regenerated on this machine).
  6. Retire the controller-era messages in `Contracts` and `contracts/monitor/v1` only after steps 2 to 4, keeping field numbers reserved.
- Consequences: The solution still builds two release artifact families that are not part of the peer architecture; that is intended until step 1. `MonitorApp.Update` and the P-256 proof code in `Contracts`/`Peer.Core` need no extraction.
- Revisit condition: Release publishing stops emitting controller or agent archives, or a Peer project stops using `Agent.Core` or `Android.Core`.

### T058 Dual-boot machines: one machine, several OS installs

- Status: Accepted (design; implementation tracked in `TODO.md`).
- Decision: A physical machine that dual-boots keeps one peer identity per OS install. Keys are never shared between installs: the Windows key stays DPAPI-protected and the Linux key stays a 0600 file, and neither OS can read the other's. Machines are a viewer-side grouping on top of peer trust:
  - Every node publishes a signed `host.descriptor` origin record about itself on start and when it changes: hardware id, OS family (`windows`, `linux`), OS name and version, host name, boot id and boot time. The hardware id is `SHA-256("monitorapp-machine-v1|" + SMBIOS system UUID)`, lowercase hex, never the raw UUID. Windows reads it from `Win32_ComputerSystemProduct`; on Linux `/sys/class/dmi/id/product_uuid` is root-only, so the `.deb`/`.rpm` maintainer script (root) writes only the hash to `/var/lib/monitor-app/hardware-id` (0644, root-owned). A missing or all-zero UUID gives no hardware id and the install is never suggested for linking.
  - The hardware id is a hint, never trust. When two actively trusted peers report the same hardware id, a viewer offers "these look like one machine" and the owner confirms the link once, gated by presence (fingerprint on the phone, desktop presence where enabled). Unlinking needs no presence. Each install must already be individually paired: linking never adds trust.
  - A linked machine is shown as one device with its OS installs. The install that is online is "running"; the others are "booted into <other OS>", not offline, and raise nothing. The machine is offline only when no install is online.
  - Red flags stay strict. Two installs of one machine online at the same time with different boot ids for more than 2 minutes (longer than a reboot handover) mark the machine `conflict`: a single computer cannot run both, so it suggests a copied identity or a wrong link. An install whose reported hardware id no longer matches the one recorded at link time is shown as `hardware_changed`, its link is suspended (it is shown and classified as a separate device) until the owner confirms again.
- Reason: Dual-boot is normal for the owner's machines. Treating the absent OS as a failed device would cause false alarms, and sharing one key across OSes would weaken key protection on both.
- Consequences: Each viewer stores its own links (`machine_links`), so a link confirmed on the phone is not yet known to the laptop. Per-peer data (history, alerts, rules, grants) stays keyed by peer id, so grants are given per install.
- Revisit condition: Links should replicate between the owner's devices, or a hardware attestation (TPM) becomes available to bind installs.

### T059 Remote OS switching for dual-boot machines

- Status: Accepted (design; implementation tracked in `TODO.md`).
- Decision: The owner can restart a dual-boot machine into a chosen OS install from any of their devices. It is a high-risk `SwitchBootTarget` control command: the issuer needs a new `power_control` grant on the target and a presence signature (fingerprint), like update approval.
  - Only one-time boots. On Linux the root update helper pattern is reused: the unprivileged node writes a request, a root helper started by a systemd path unit re-checks the signed command result, then runs `grub2-reboot`/`grub-reboot <entry>` and `systemctl reboot`. The GRUB default (`saved_entry`, `GRUB_DEFAULT`) is never written, so the next boot after that, or any failed boot, returns to the default entry (Fedora on the owner's laptop).
  - Supported only where it can be done safely. Linux needs `GRUB_DEFAULT=saved` (Fedora's default) and a grubenv the helper can write; otherwise the install reports `unsupported` with the reason, and the app never edits GRUB configuration. Windows usually cannot write GRUB's grubenv (Fedora keeps it under `/boot`), so a Windows install offers "restart to the default entry" only, which reaches Fedora on the owner's laptop. Choosing any other entry from Windows is reported as unsupported.
  - The target re-reads its own boot menu and refuses when the entry id is missing or its title differs from `expected_title`.
  - Wake-on-LAN: a peer on the same LAN as a machine that is off can send a magic packet to the MACs from the machine's last descriptor. That only powers it on into its default entry. Waking into a non-default OS is not possible without the OS running first, so the UI says so.
- Reason: The owner dual-boots and wants to switch OS from the phone without being at the machine, without the risk of leaving a machine stuck in a non-default OS or with an edited boot configuration.
- Consequences: A switch always means a restart, so all sessions on that install drop and the machine is then shown "booted into <other OS>" when the sibling install connects. Waking a machine needs another peer awake on its LAN.
- Revisit condition: Writing a shared-ESP grubenv from Windows, UEFI `BootNext` support, or the owner wants a persistent default change.

### T301 Standalone design-planning boundary

- Status: Accepted (working implementation decision within the owner-assigned rack-planning area).
- Decision: `MonitorApp.Planning` is a pure .NET, design-only domain component. Named project/scenario revision is carried through rack layouts and project evaluation; catalog products, seller offers, proposed parts, and actual-instance references remain distinct. Rack, pedalboard, audio, connector, power, and physical evaluators emit deterministic errors, warnings, or unresolved findings from declared evidence. Unknown dimensions, protocols, power profiles, polarity, ratings, and measurements never become implicit compatibility or safety. The browser prototype may explore interactions and import/export proposal JSON, but the C# domain is the intended authoritative evaluator and neither layer writes actual inventory, controls equipment, or edits Claude-owned UI/runtime modules.
- Reason: Planning needs executable domain rules before integration, while actual/design separation and unknown-state behavior must remain enforceable independently of any visual scene or device connection.
- Evidence: `MonitorApp.Planning.Tests` covers project/task/offer identity, rack and nested-subrack geometry, pedalboard clearances, physical constraints, audio/MIDI/CV and pedal DC rules, and connector/protocol/power/adapter compatibility.
- Consequences: Persistence, promotion into actual inventory, client integration, complete RF coordination, sourcing adapters, and hardware validation require later claims and tests. Compatibility findings are decision support, not authorization to energize equipment or a regulatory/safety certification.
- Revisit condition: The universal inventory persistence layer or client integrates planning, or a domain requires a versioned serialized/wire contract.

### T080 Mesh membership: add once, trusted everywhere

- Status: Accepted (owner decision 2026-11-23 on a decision card: "Add once, trusted everywhere". Supersedes "transitive trust out of scope" in `docs/SECURITY.md` and the deferred peer introductions in `docs/ARCHITECTURE.md`).
- Decision: Every peer keeps a set of signed mesh statements in `peer.db` (peer schema v5, tables `mesh_statements` and `mesh_trust_marks`). An ADMIT says "I trust device X with key K" and carries X's display name and up to four locators; a REMOVE says "I removed device X with key K". Each statement is signed by its issuer's identity key over an explicit length-prefixed canonical encoding with its own domain label (`MeshStatementCryptography`, `monitor-mesh-statement-v1`) and names the issuer by peer id and key hash. A peer issues statements only from its own trust table: an ADMIT for each direct pairing (re-issued when its locators or name change), a REMOVE for each device the owner revoked there. Statements travel in the additive `CapabilityExchange.mesh_statements` (field 2) of every session, which is sent only after both sides authenticated; older peers ignore the field. A receiver accepts statements only from an actively trusted sender, and stores one only when its issuer is reachable from the receiver and its signature verifies; statements dated more than ten minutes ahead are refused.
  - Trust is derived from the statement set alone, so every peer reaches the same answer whatever order statements arrive in: *reachable* = (peer id, key) pairs reachable from this peer over verified ADMITs, ignoring removals; *removed* = subjects of verified REMOVEs whose issuer is reachable; *trusted* = reachable over ADMITs whose issuer and subject are not removed.
  - Removal is final for that key and wins over any admission. A removed device's admissions stop counting. A removed device's own removals are still honoured, so a stolen device can cost the owner service (it can remove others) but can never stay trusted or add devices once any member removes it.
  - The mesh creates trust rows only for unknown peer ids (never rebinds an id to another key, never revives a revoked row), marks the rows it creates or revokes, revokes a mesh-admitted device when nothing still trusted vouches for it, and records "Joined the mesh (added by X)" / "Removed from the mesh by X" in the peer-state history.
  - When the statement set or derived trust changes, held-open live sessions end (the server with a retryable error, the dialer by reconnecting) so peers swap statements again; computers dial newly trusted peers with locators and retry peers whose session was refused, phones already supervise every active peer.
  - Revoking a device on a computer is now a high-risk local action (`peer.revoke`, desktop-local presence when enrolled) because it removes the device everywhere.
- Reason: The owner wants a true mesh: pair a device once, from any device, and have every device trust it; remove it once and have it gone everywhere.
- Security: Mesh trust grants what pairing granted (sessions and replication); it never grants control. Control grants stay per device and deny by default, presence keys still need local approval on the target. A compromised member can admit devices until it is removed; the owner sees every join in each device's history. Statements are bounded (512 per exchange, 1024 stored, 4 locators, 64-character names).
- Evidence: `MeshMembershipTests` (spread through a chain with who-added-whom and locators, idempotent repeat; unknown issuers, forged issuer key, tampering and future dates never stored; removal spreads and survives re-admission; a removed device racing to remove others cannot stay trusted or add devices; no key rebinding; order-independent derivation) and `PeerNodeRuntimeTests.PairingWithOneComputerJoinsTheWholeMeshAndRemovingADeviceAnywhereRemovesItEverywhere` (three real nodes over loopback gRPC: two pairings through one computer, the other two trust and talk to each other directly, a revoke on one reaches the computer that paired it).
- Consequences: A mesh-admitted phone is reachable only when it dials, as before, and two phones still exchange readings only through a computer they both reach once records are forwarded (T087). Removing was final here; R3 (below) lets a removed device pair again. Local presence (fingerprint) for joins is enforced by the existing pairing approval on the device that paired; phone-side admission and removal with a fingerprint prompt come with T085/T086. A peer older than this change neither spreads nor honours statements; devices only reach it through direct pairing.
- Revisit condition: Joining meshes the owner does not want merged, per-device trust scopes (for example a shared family mesh), or multi-hop record forwarding (T087).


### T085 Add a phone or tablet face to face (two-way QR)

- Status: Accepted (follows the owner's T080 decision; phones cannot accept connections, so two phones cannot pair over the network).
- Decision: Two phones or tablets join each other by scanning each other's screens. The new device shows a join code (`monitor-join1.` + base64url `MeshJoinRequest`): its peer id, public key, name, a 16-byte random nonce and the time, self-signed with its identity key over a length-prefixed encoding with its own domain label (`monitor-mesh-join-v1`); it is valid for ten minutes. A member scans it, shows both devices' short check codes (the first four bytes of each key fingerprint), asks for the owner's fingerprint, trusts the new device directly, and shows a welcome code (`monitor-welcome1.` + base64url `MeshWelcome`): the member's identity and name, the joiner's peer id and nonce, and up to two of the member's own ADMIT statements (computers with addresses first), signed by the member (`monitor-mesh-welcome-v1`, statements bound by their SHA-256). The new device accepts a welcome only for its current nonce, checks the member's signature, asks for the owner's fingerprint, trusts the member directly, and applies the statements through normal mesh rules. From then on the mesh spreads the new device through the member's next session with any computer.
- Security: Both QR codes only travel between two screens in the owner's hands, which is the authentication; the signatures stop a code being altered (name, key, addresses, statements), replayed to another device or to a later request, or reused (the nonce is cleared after joining). Both sides use the fingerprint or screen lock when one is set up; on a device with neither, the code comparison and the in-app tap are the confirmation (2026-11-24, see the R3 entry). A device cannot welcome itself, and a removed device or a peer id bound to another key is never added.
- Evidence: `MeshFaceToFaceTests` (round trip, renamed/swapped-key/redirected/expired/oversized join codes refused, welcome replay to a later request or another device refused, smuggled statements, changed addresses and forged signer refused, self-welcome refused), `MobileMeshTests.ATabletAddedFaceToFaceByAPhoneJoinsTheMeshAndReachesTheComputers` (a tablet joins through a phone, the computer learns it from the phone, the tablet syncs with the computer directly; declined fingerprint adds nothing; a used welcome cannot be applied again), `MobileMeshTests.AddingADeviceFailsClosedWithoutAFingerprintReader`, `MeshJoinScreenTests` (the real screens draw the QR and the other device's decoder reads that drawing).
- Consequences: Two phones that joined only each other exchange trust immediately but readings only once one of them reaches a computer and readings are forwarded (T087).
- Revisit condition: Phones gain a listener (T088), or codes need to travel over a channel other than a screen.

### T087 Forward readings through other devices

- Status: Accepted (part of the owner's T080 mesh decision: readings travel through any device that is online, still signed by the device that took them).
- Decision: A device may forward another device's readings. Forwarding is pull-based and needs no new message types. A computer's session hello now also lists, for each other actively trusted device whose readings it holds, how far it holds that stream without gaps (over every peer it got it from). A dialing device asks for the part it lacks with an ordinary `OriginRangeRequest` for that origin; the computer answers from what it stores, skipping ranges its own retention removed and stopping at the first record it lacks. Live sessions repeat an open-ended pull every 15 seconds; a trusted device whose stream neither side knew at session start renews the session after about a minute so the next hello lists it. A computer ignores acknowledgements for readings it only forwarded. Records are stored once, whoever delivered them: a cursor for one source starts from the furthest any source reached, and a record that arrives again from its origin is an identical retry.
- Security: Each forwarded record is checked against the origin's own key, so a forwarding device cannot change, invent or reorder readings; it can only withhold them. A record is accepted only while both the forwarding device and the origin are actively trusted, and a computer only forwards readings of devices it actively trusts and never hands a device its own stream back. Only the origin may declare part of its stream removed (a forwarded pruned range is refused).
- Evidence: `OriginReplicationCoordinatorTests.ATrustedDeviceCanRelayAnOriginsRecordsOnlyUnalteredAndOnlyWhileTheOriginIsTrusted` (forged signature, forwarded prune, revoked origin refused; origin redelivery is a retry), `ARelayForwardsOnlyContiguousRecordsItHolds`, `MobileMeshTests.APhoneAndATabletSeeEachOthersReadingsThroughTheComputerTheyBothReach` (two outbound-only devices exchange readings through one computer, both at session start and live).
- Consequences: A phone and a tablet, or a phone and a computer it cannot reach, see each other's readings whenever both reach some computer. A computer that only accepts connections and never dials does not pull forwarded readings (computers dial each other, so this does not arise between paired computers). Forwarded readings are as fresh as the 15-second pull, not pushed. Older peers neither forward nor ask.
- Revisit condition: Readings must cross more than one forwarding computer in near real time, or devices need to withhold some readings from some members.

### T086 Seeing and controlling the mesh

- Status: Accepted (follows the owner's T080 decision).
- Decision: The Pairing and trust list says how each device came to be trusted here (paired on this device, or added or removed by a named member) from the stored mesh marks, and removal is worded as removal from all devices. A computer that starts trusting a device another member added raises a Warning alert, `mesh.device_joined`, naming who added it; the device itself is not told about itself. Every computer raises its own, so the phone shows and notifies only the earliest per device and drops ones about itself. Removing a device on the phone asks for the fingerprint when one is set up, like computers; without one, removal still works, so a lost device can always be removed.
- Evidence: `MobileMeshTests.APhoneHearsOnceThatADeviceJoinedThroughAnotherComputer` (alert reaches the phone once, from the computer that learned the device; device list provenance), `MeshJoinAlertTests`, `MobileMeshTests.RemovingADeviceOnAPhoneAsksForTheFingerprint`.
- Revisit condition: The owner wants joins approved by a second device before they take effect, rather than announced after.

### D1-D5 The app fills its model by itself (network discovery, Tailscale, services, hardware)

- Status: Accepted (owner decision 2026-11-24 for discovery: "Yes, with a switch per computer").
- Decision: Each computer describes what it can find: its hardware (maker, model, processor, memory, graphics, drives, installed systems from UEFI boot entries), the services it runs (Tailscale, Syncthing and others), every device on its tailnet (`ts:{id}`), and, only while its "Find devices on this network" switch is on, the devices on its own private subnet (`net:{mac}`). Found devices are subjects of ordinary signed readings, not a new record type, so replication, retention, the Map and Things need no new paths. The shared model turns them into things bound to their subject (the MAC is the strong identity, so two computers finding one device make one thing; kinds are estimates), links each device "connected to" its network's router and the router to the computer that found it. A tailnet device with the same name as a mesh member is that member and is not drawn or added twice. The Map draws found devices under their router and Tailscale and Syncthing as nodes beside the computer that runs them. The node's service account cannot read the owner's Syncthing config, so the owner's desktop app hands the key to its own loopback node.
- Security: see `docs/SECURITY.md` (network discovery paragraph). The earlier rule "never read the neighbor table" still holds for ordinary monitoring; discovery is the explicit, switched exception.
- Evidence: `SelfDiscoveryTests` (only private subnets of /22 or smaller are swept, the computer itself is skipped, a second pass inside 30 minutes does not ping, junk mDNS is ignored, UPnP location must be HTTP), `SelfDiscoveryProjectionTests` (Map names and router parents, no duplicate member, one thing per MAC with links), `SelfDiscoverySafetyTests` (off by default, choice kept, key file owner-only, key only to a loopback node). A real Linux node stored hardware, services and the switch round trip (403 without the antiforgery token).
- Consequences: Found devices replicate to every member, including their MACs and addresses (they never leave the mesh). Windows lists only the running system (UEFI variables need a privilege the service lacks); the Fedora side lists Windows and Fedora. Tailscale on Windows under the service account and SSDP/mDNS through host firewalls are unverified on real hardware. Renaming a found device in Things renames the thing; the Map still shows the network name until the Map draws from the model.
- Revisit condition: The owner wants found devices kept off some members, port or service probing, or discovery on networks other than the computer's own.

### R3 Pair a removed device again (re-admission)

- Status: Accepted (owner, 2026-11-24: a removed device must be able to pair again normally, with fresh approval and no reinstall, while removal still takes effect everywhere at once).
- Decision: A new statement kind READMIT (3) is an admission that also names, by statement id, the removals it lifts (1 to 32 ids, signed: the ids are added to the canonical encoding for this kind only, so ADMIT and REMOVE signatures are unchanged). Pairing a known device again with the same key is allowed and waits for the owner's approval exactly like a new device; completing it (`PeerStore.PairTrustedPeerAsync`) makes the row active with a new pairing time and clears its mesh marks. The device that paired it then issues a READMIT naming every removal of that device it had received before the new pairing time. Derivation: a removal is lifted when a counted READMIT for the same (peer id, key) names it and the READMIT's issuer is reachable and not itself removed; this is repeated until nothing changes, so lifts must trace back to a device that was never removed (two removed devices cannot lift each other). Rows revoked by the mesh, or by this device's own removal that was lifted, come back ("Let back in (paired again on X)"). Removing again issues a new REMOVE (new id), which no earlier READMIT names, so it wins everywhere whatever the clocks say. Statements are issued before derivation on every exchange, so a pairing completed moments ago is never undone by the removal it lifts. Peer schema v6 rebuilds `mesh_statements` once to allow kind 3 (copy, then drop, in one transaction).
- Security: Only an owner-approved pairing produces a READMIT, which any trusted member could already do for a new device. A removed device cannot bring itself or another removed device back. A removal received after the re-pairing is never lifted by it. Phones add and remove devices with the fingerprint, or the screen lock when no fingerprint is set up; with neither, the in-app step (comparing the code and tapping Add or Remove) is the confirmation and the screen says no lock is set up (Wilson, 2026-11-24: his devices have no lock, and refusing left him unable to add or remove anything). Older peers ignore READMITs (unknown kind) and keep the device removed until they update.
- Evidence: `MeshMembershipTests.PairingARemovedDeviceAgainLetsItBackEverywhereAndANewerRemovalStillWins`, `AReAdmissionLiftsOnlyTheRemovalsItNamesAndRemovedDevicesCannotLiftEachOther`, `ARemovalThatArrivesAfterPairingAgainIsNeverLiftedByIt`, `AStoreFromBeforeReAdmissionsKeepsItsStatementsAndAcceptsThem`; `PeerPairingCoordinatorTests.RemovedPeerCanPairAgainOnlyThroughAFreshApproval`.


## Phones keep only recent readings from another device when catching up (2026-11-25)

- Status: Accepted (owner asked for readings to flow between devices; the field logs of 2026-11-24 showed a re-paired phone stuck copying a computer's full history).
- Decision: A phone that asks for live push (`PeerSyncLiveOptions.RequestRemotePush`) fetches at most `PeerSyncOptions.MaximumBackfillRecords` (20,000) of another device's own readings when it is behind. The older part is recorded on the phone as a pruned (not held here) range, using the same tombstone used for retention, so the phone's cursor moves past it instead of waiting forever. The phone sends that cursor to the computer, so the computer's retention can release what it held back for the phone. Before going live, a phone sends at most 4 batches of its own readings and pulls at most 4; the live session carries the rest both ways from the same persisted cursors. Its own readings always go before any pull. Computers that dial each other are unchanged and still copy everything.
- Consequence: A phone's history of a computer starts where it caught up. The computer still holds all of its own readings, and history views read them there.
- Evidence: `LiveSessionTests.APhoneFarBehindSendsItsOwnReadingsAndGoesLiveWithoutCopyingTheDesktopsWholeHistory`. Field data: the laptop's stream was contiguous from 1 to 508,681. The Pixel sent 6 to 67 range requests per session, never an OriginBatch or LiveSubscribe, and every session ended with 0 readings received.

### Phones resume monitoring after updates and restarts if left on (2026-11-25)

- Context: an app update or reboot closed the foreground service, and nothing restarted it until someone tapped Start monitoring. On 2026-11-25 the tablet sat silent for 11 minutes after an update with its app open; Wilson: "any time I touch anything I have to restart the runtime".
- Decision (Wilson, decision card): resume if left on. Starting monitoring is still an explicit user action; the choice is remembered (app preferences) and the service resumes on BOOT_COMPLETED, MY_PACKAGE_REPLACED and app open until the user stops it from the app or the notification. specialUse foreground services may start from these broadcasts.

### T076 Monitor App 2.x planning drafts

The Flutter UI can hold disposable session drafts while the shared planning contract is being developed. Drafts own interaction state only and have no service or file boundary. Durable project identity, revisions, placement, and links to Things belong to a future monitor_core contract. The current rack checks use only entered geometry and cannot establish installation compatibility.

With the shared planning contract available, an explicit save writes a project revision through `ProjectStore`. If a newer revision is already present, the UI keeps the working copy open for review or export. Known equipment height and planned rack position are saved independently through the typed placement contract.
