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
- Consequences: Several viewers with different signals share the union, and every viewer receives the union's records. The phone's own collection is not yet narrowed by `signal_ids`. Duplicates remain possible briefly around a channel switch (idempotent).
- Revisit condition: Per-viewer push filtering, or measured cost of the union approach.
