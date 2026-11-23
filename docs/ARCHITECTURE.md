# Architecture

## Status

This is the target functional architecture after the user explicitly superseded the controller-centric design. Monitor App is a decentralized system of equal peers. The current repository still contains a controller alpha and controller-oriented contracts; those are migration inputs and test evidence, not the intended deployment topology.

### Implementation claim boundary

| Capability | Current status | Evidence or next gate |
| --- | --- | --- |
| Authentication, telemetry, collectors, SQLite durability, Android foreground snapshot, and update verification | Implemented only in the controller-era alpha | Existing executable tests; reusable only after peer migration proves compatibility |
| Peer contracts, pairing/reconnect core, durable approval/replay state, and peer-local SQLite schema | Implemented foundation | Generated contract tests, real P-256 tests, and real file-backed SQLite tests |
| Origin-owned replication coordinator | Implemented in-process component | Signed-record, trust/revocation, range-limit, retry, gap, cursor, and restart tests against real SQLite |
| Capability modules and canonical observation catalog | Implemented contract and registration foundation | Versioned namespaced declarations carry entity, relationship, signal, source/unit/time/freshness/importance/privacy/cost, protocol compatibility, retention, and inspector metadata; deterministic selection uses an installed purpose-built inspector when applicable and otherwise returns the declared generated fallback; executable policy rejects prohibited ordinary network data and enforces separately enabled evidence-bearing defensive modules |
| Equal-peer pairing, reconnect, collection, and synchronization | Implemented and composed in the Windows preview | Real Windows loopback tests prove explicit fresh-pair approval, persisted locator reconnect/backoff, signed system collection, disconnect/restart, bounded origin exchange, durable cursors, and duplicate-free convergence in separate SQLite stores |
| Windows interactive peer preview (also runs as a Linux engineering preview, verified in an Ubuntu 24.04 container) | Implemented preview; Linux uses a permission-protected key file and is not a support claim | DPAPI identity restart test, two independent node-host test, two-node web invitation/approval plus offline-history convergence test, loopback administration page, and manual launch verification |
| Desktop-local user presence (T045) | Implemented; Windows Hello and fprintd unverified on hardware | `LocalPresenceService` in `Peer.LocalApi` issues single-use action-bound challenges and verifies a locally enrolled presence key; the interactive desktop client (`ILocalPresenceProvider` in `Client.Core`; `Client.Presence` fprintd/software key; `Client.Presence.Windows` Windows Hello) verifies the person and signs, because the node service has no desktop session. Policy default required-when-enrolled; local root/Administrator can bypass (see `docs/SECURITY.md`) |
| Universal inventory with separate actual and design state | Implemented contract foundation | Versioned object/relationship schema validates named design revisions, provenance/validity, cross-plane references, and distinct product/offer/physical-instance identity; persistence and a plain local client remain |
| Cable connector/length inventory | Implemented narrow contract slice | Two typed connector ends, directionality, and unit-bearing nominal/measured length are validated; manual templates, persistence, matching, query, and constraint tests remain |
| Observed-device identity and collector ownership | Implemented persistence plus first adapter spike | Strong identity evidence, typed attachment history, adapter-local credential references, validated current ownership, and restart-stable reassignment history are covered by real SQLite tests; IP-only identity is never strong. A provisional read-only Yamaha AG06MK2 Windows adapter reports presence, driver/attachment evidence, freshness, and explicit failure states without operational controls |
| Bounded history/as-of query and catalog | Implemented raw-resolution service and versioned local JSON API | Real-SQLite window, cursor, as-of, state-not-zero, and HTTP tests; hourly rollup resolution (`resolution` raw, hourly, or auto) implemented over schema v4 |
| Tiered retention (raw 7d, hourly rollups 365d, transition records 5y, pinned incidents) | Implemented; the node runs it at startup and hourly; pin/unpin on the loopback local API; receivers record origin-pruned ranges as absent (T041) | `PeerRetentionService.RunAsync`; real-SQLite tests for rollups, pruning rules, replication hold, and pruned-range replica outcome |
| Historical scene reconstruction and living/exploded UI | Confirmed scope; not implemented | Snapshot/event reconstruction and functional client before advanced rendering |
| Rack, pedalboard, connector, audio, and physical planning (T301) | Implemented standalone design-only foundation; not integrated with inventory or the Avalonia client | Pure .NET planning model and 30 unit tests cover multi-standard placement, evidence-aware constraint findings, audio/MIDI/CV and pedal DC rules, connector/protocol/power compatibility, named project revisions, and product/offer separation; persistence, actual-inventory promotion, full RF coordination, and authoritative client integration remain |
| Product sourcing, offers, and price history | Confirmed scope; not implemented | Permitted source adapter and provenance/freshness tests |
| Automation and push notifications | Confirmed scope; not implemented | Durable rule/outbox ownership and delivery tests |
| Distributed local AI | Confirmed scope; not implemented | Optional capability routing and permission/provenance tests |

Documentation examples are plans, not support claims.

## Confirmed constraints

- Target Android, Windows, Fedora, and Ubuntu.
- Require no Monitor App controller, central monitoring database, or always-on Monitor App host.
- Let the user enter a Tailscale IP for initial peer contact.
- Reconnect paired peers directly whenever both services are running and Tailscale-reachable.
- Treat Tailscale as reachability only; application identity and authorization remain independent.
- Store and serve data locally on every peer.
- Include non-peer devices and components through purpose-built adapters on collector peers.
- Model the physical LAN separately from the Tailscale overlay.
- Make safely and truthfully measurable signals extensible through versioned capability modules.
- Preserve purpose-built information design and selective customization rather than a blank dashboard.
- Permit aggregate operational network measurement while excluding communication content and identity histories from ordinary monitoring.
- Keep defensive threat detection separate, explicit, evidence-based, locally processed, permissioned, and bounded.
- Preserve historical time travel, truthful freshness, and functionality-first delivery.

## Accepted implementation baseline

- C# on .NET 10 LTS.
- Standard gRPC over TLS and HTTP/2 with versioned Protocol Buffers.
- One shared peer-service core with platform-specific host, collector, key-storage, and lifecycle boundaries.
- File-backed SQLite in WAL mode on every peer.
- Avalonia remains reserved for the later shared experience; the functional client may be plain.
- Native Windows Service and `systemd` packages for desktop and Linux peers.
- Android uses only platform-supported foreground/background mechanisms and reports when service continuity is unavailable.
- Signed release manifests and platform-specific verified update application: `MonitorApp.Update` stages verified artifacts and applies them through `IUpdateApplier` (Linux package, Windows side-by-side, Android offer) with a persisted state machine; the peer node/service calls it (wired into the node; Windows apply uses a detached SYSTEM helper task, T043).

## Functional system shape

```text
Peer A                                              Peer B
|- local experience                                |- local experience
|- local API                                       |- local API
|- platform collectors                             |- platform collectors
|- adapter host                                    |- adapter host
|- local origin log                                |- local origin log
|- retained replicas                               |- retained replicas
|- peer directory + pinned key                     |- peer directory + pinned key
`- SQLite WAL                                      `- SQLite WAL
          \                                        /
           `-- direct authenticated peer session --'
                 over a configured Tailscale IP

Observed devices and components
  UPS / audio interface / managed switch / services / sensors
       | collected through a purpose-built adapter
       `-- explicit collector ownership on a selected peer
```

No peer has global authority. A peer may retain more data or be online more often without becoming a controller.

## Entity model

### Universal object and state planes

The monitoring entities below are the first operational subset of a broader universal object model. The shared envelope supplies stable identity, type, name, provenance, validity, attachments, and typed relationships. Capability modules add domain fields rather than expanding one nullable universal record.

The model keeps two state planes explicit:

- **Actual state** represents externally real or possessed objects, actual locations and connections, and observed current/historical operation. Possession state and operational state are separate: owned does not imply installed or running.
- **Design state** belongs to a named project/scenario revision and represents proposed objects, relationships, requirements, alternatives, assumptions, and calculations.

A design can reference an actual object. A design-only object or relationship cannot silently enter actual inventory. Acquisition, receipt, installation, connection, activation, removal, and retirement are explicit events. Promotion creates or links an actual instance while retaining the plan, candidate, offer, and decision history. Queries, totals, constraint findings, automation inputs, and UI projections declare the plane they use.

Additional object families include spaces/positions, racks/mounts, cables/connectors, power and signal paths, projects/requirements/alternatives, canonical products/variants, seller offers, and actual equipment instances. Product, offer, and physical-instance identity remain separate.

### Peer node

A computer or phone that runs Monitor App. Its stable identity is an application key, not its IP address. It has a platform, capabilities, configured Tailscale endpoints, local origin streams, replication cursors, and lifecycle/freshness state.

### Observed device

A physical or logical entity that may not run Monitor App, including a UPS, audio interface, managed switch, sensor, service, or storage appliance. It keeps a stable identity and history independent of the peer currently collecting it.

Strong identity may come from a serial number, stable chassis identifier, chassis MAC, or another adapter-specific identifier. An IP address alone is not sufficient to merge observed-device records.

### Component

A nested part of a peer or observed device, such as a filesystem, battery, switch port, audio endpoint, UPS battery bank, VLAN, or service. Component existence must be supported by a peer report, adapter report, or evidence-qualified inference.

### Relationship

Typed relationships include paired-with, reachable-over-overlay, physically-attached-to, collected-by, hosted-on, connected-to-port, member-of-VLAN, uplink-to, depends-on, and synchronized-with.

Every relationship preserves source, validity interval, quality, confidence, and whether it was reported or inferred. Physical-LAN relationships and Tailscale-overlay relationships never collapse into one ambiguous edge type.

Planning adds actual-versus-design scope and relationship types such as proposed-in, candidate-for, satisfies, installed-in, connected-to, powered-by, sends-signal-to, cools, blocks, offered-as, replaces, and derived-from. A relationship may be directional and may carry unit-bearing constraint inputs.

### Cable

A cable is either an individually identified physical item or a counted batch plus a shared specification. It has two structured connector ends, nominal and optionally measured length, units and tolerance, active/passive or conversion behavior, supported signal/protocol, electrical and performance ratings, physical/environmental properties, condition/test evidence, storage location or current endpoint connections, custody/reservation, and project assignment.

Connector ends preserve exact standard/form factor, plug/socket or gender when meaningful, role/direction, keying/orientation, pin/pole count, locking, version/category, and supported signal. Reversible and directional cables are distinct. Cable identity is stable and is not derived from mutable connector, length, or location text.

The entry path uses searchable reusable connector-pair and length templates with keyboard/touch-friendly manual entry. QR, phone-camera, and dedicated scanner adapters are outside the required cable path. A planned cable requirement can be assigned to a suitable actual cable without merging the records.

## Pairing and trust

The initial personal-scale system uses explicit pairwise trust.

1. Each peer generates and protects a P-256 application identity key.
2. A user enters another peer's Tailscale IP and peer port.
3. The contacted peer creates a short-lived bilateral pairing challenge.
4. Both peers prove key possession and bind approval to both identities and the handshake transcript.
5. The user verifies a short authentication string, QR representation, or equivalent identity confirmation.
6. Both peers store the other application identity, pinned key, approved endpoint, capabilities, and trust state.
7. Every later session uses a fresh replay-resistant proof.

Tailscale IP and tailnet membership are bootstrap and reachability evidence only. Address changes require an authenticated update or explicit repair. Trust does not automatically become transitive, and peer introductions remain deferred until their authorization semantics are designed.

Both peers may initiate a connection. If both initiate simultaneously, a deterministic rule selects one logical session. Reconnection uses bounded backoff and must distinguish service outage, Tailscale path failure, identity rejection, and protocol incompatibility.

## Origin-owned observation model

Each peer is the sole writer of an ordered origin stream. An observation carries:

- origin peer and origin sequence;
- observed entity and component identity;
- capability module and adapter source;
- source and ingestion times;
- numeric value, counter, state, event, fact, error, or topology evidence;
- unit and temporal behavior;
- quality and freshness;
- privacy, importance, and collection-cost categories;
- schema/module versions.

Only the origin advances its sequence. Receivers validate and retain records without rewriting them. This avoids global consensus for telemetry. Identical retries are idempotent; conflicting bytes at one origin sequence fail closed; gaps remain explicit.

Shared metadata such as annotations, display names, incident pins, topology corrections, or retention protection requires separately defined conflict semantics before it replicates.

## Replication

Direct peer sessions exchange per-origin cursors and bounded batches. The protocol must support:

- resume after either service or network restart;
- gap detection and bounded range requests;
- byte-identical retry and conflict rejection;
- backpressure and storage/power budgets;
- raw local retention and optional replicated summaries;
- explicit summary fidelity and rollup provenance;
- last synchronization time and partial-history visibility;
- schema/module compatibility negotiation.

### Live sessions

A held-open `PeerSession` is the real-time path between peers; the phone is outbound-only, so everything rides the session it dials (one long-lived session per paired desktop, capped-backoff reconnect resuming from cursors). After authentication and the ordinary lock-step catch-up, the dialer announces `LiveSubscribe` (the server then applies a per-minute message bound and an idle timeout instead of a lifetime total). Each side wakes on `PeerStore.Live` (an in-process "record committed" signal for local and replicated records), reads the next unsent range of its own origin stream, and sends it as the usual signed `OriginRecordBatch`; the receiver applies it idempotently and returns `OriginAck`. At most a few batches are in flight; an acknowledgement below what was sent (a gap) rewinds the sender to the receiver's cursor. Live push is therefore an optimization over the same per-origin replication, never a separate unsigned channel. Heartbeats detect dead sessions (the dialer drops after about three missed intervals; the server closes idle live sessions).

Adaptive sampling: a peer declares `LiveInterest` (subject = the receiver, optional signals, lease seconds, 0 withdraws) while a user is viewing; the receiving origin's collection scheduler samples at 1 s while any unexpired interest exists and returns to its normal interval when it lapses. Interest is bounded (maximum lease, concurrent interests, signals), only honoured for the receiver's own data from trusted authenticated peers, and a local UI declares it in-process (`BeginViewing`) or, over the loopback API, through renewable leases (`/api/v1/live/interest`) that lapse unless renewed. UIs subscribe to `PeerStore.Live` (`IAsyncEnumerable`) or, on desktop, `GET /api/v1/live` (SSE, loopback only).

### Remote monitoring control

`MonitorApp.Peer.Control` holds the portable pieces: `ControlPolicy` (tiers and bounds), `ControlCanonical` (signing input), `ControlCommandSigner`/`ControlRequest` (phone side, with `IUserPresenceSigner`), `ControlStore` (grants, presence-key enrollments, command results, append-only audit in `peer.db`) and `ControlService` (desktop verification and execution). A phone opens a short authenticated `PeerSession` (`PeerGrpcConnector.SendControlAsync`; the session id is chosen before connecting so the biometric prompt happens first), sends one `ControlCommand` signed for that session and reads the `ControlResult`. The desktop's `ProductionPeerSessionCoordinator.ExecuteControlAsync` routes it to `ControlService`, which applies effects through the existing owners: `MonitoringControlState` read by `NodeCollectionScheduler`/`AdaptiveSampler` (pause skips scheduled collection; an interval override replaces the normal interval while live interest can still make sampling faster), `AlertStore` (mutes filter `ReadPendingAsync`; unmute wakes live sessions; operator acknowledgements), and `PeerPairingCoordinator` (same approve/reject path as the admin page). Phones read pending pairings through `PendingPairingsRequest`. The loopback API adds `/api/v1/audit`, `/api/v1/control`, grants and presence-enrollment decisions. A Controls UI section is a later step.

An acknowledgement means the receiving peer durably committed the record. It does not authorize the origin to delete its only copy. Replicas may be replaceable; origin data is not.

## Capability modules and observation catalog

A module uses a versioned, namespaced contract and declares:

- entity and component types;
- observation definitions;
- typed relationships and evidence semantics;
- supported discovery and strong-identity evidence;
- sampling and retention metadata;
- permissions and local credentials;
- importance, privacy, and collection-cost categories;
- quality, freshness, unsupported, denied, and error behavior;
- compatibility range;
- purpose-built inspector descriptors and truthful fallback fields.

The canonical observation catalog classifies signals by domain, entity, subsystem, source, unit, temporal behavior, importance, privacy, and cost. It is the source of truth for search, domain lenses, retention, replication summaries, and generated fallback inspectors.

Important integrations receive purpose-built inspectors. An unknown compatible module may receive a truthful generated inspector. Users may enable/disable modules, change retention, and pin/hide designed content; they do not construct a blank dashboard.

## Breadth, sampling, and retention

Anything safely and truthfully measurable should be measurable through a supported capability module. This includes numeric values, counters, state, events, inventory/configuration facts, errors, and topology evidence.

Breadth is bounded by truthfulness, privacy, collection cost, platform power, local storage, and replication budgets. Sampling adapts to change rate and cost. A peer may keep raw high-resolution detail locally while replicating summaries or requested ranges. Unsupported, denied, stale, unavailable, unknown, and error states remain explicit and never become zero.

Secrets, message/file content, and unrelated content payloads remain excluded by default. Sensitive or high-cost modules are explicit opt-in and visible in collection and retention settings.

### Tiered retention (working decision)

Peer.Application `PeerRetentionService.RunAsync` is the single periodic pass: (1) roll up completed UTC hours per (origin, subject, signal) using the production envelope decoder; (2) prune raw records older than the hour-aligned raw cutoff; (3) prune rollups older than their retention. Rollups count availability states (valid, estimated, unavailable, permission denied, error) and keep numeric min/max/sum only over Valid/Estimated numeric samples; states are never averaged. Hours older than the raw cutoff are finalised once; hours still inside raw retention are recomputed when late arrivals change their record count.

Raw pruning keeps: records inside user-pinned windows; "transition" records (first record of a series, or quality differs from the previous record of that series) while younger than event retention; and locally originated records that any active trusted peer has not acknowledged (replication cursor below the sequence) until the replication hard cap. Removed sequence runs are stored in `pruned_origin_ranges`; `OriginReplicationCoordinator.ReadAsync` throws `OriginRangePrunedException` (wire code `RANGE_PRUNED`) for a request starting in such a range and stops batches before one, so replicas never see a silent hole. The replica records such a range (`ProtocolError.pruned_range` on a pull, `OriginRecordBatch.pruned_range` on a push) in its own `pruned_origin_ranges` after checking that the stream's active trusted owner sent it and that it starts no later than the next sequence needed; the cursor then skips it, so there is neither a retry loop nor a perpetual gap. `NodePeriodicTask` schedules the pass (startup, then hourly, `TimeProvider`-driven); pins are managed through `GET /api/v1/incidents`, `POST /api/v1/incidents/pin` and `POST /api/v1/incidents/{id}/unpin` and are local to the peer. History queries serve hourly points when the window starts before raw retention (or on request); pruned raw detail never reappears.

## Network boundaries

### Allowed ordinary monitoring

- interface, switch, and port identity;
- link state and negotiated capability/speed;
- VLAN and administrative configuration;
- PoE state and power;
- explicitly reported LLDP relationships;
- aggregate bandwidth, throughput, utilization, byte/packet totals, errors, drops, discards, retries, and queue health;
- latency and reachability probes;
- historical aggregate trends;
- management and adapter health.

### Prohibited ordinary monitoring

- packet headers or payload capture;
- individual packet records;
- flow, session, connection, or socket histories;
- source/destination or DNS/query histories;
- per-application or per-process traffic inspection;
- content classification or DPI.

Module registration and adapter review reject prohibited observations before collection, rather than merely hiding them in UI.

### Defensive threat detection

Defensive detection is a separate explicitly enabled capability. Baseline detection may use aggregate signals, identity/authentication failures, unexpected peer/config/link changes, unusual utilization, and summarized alerts already produced by an IDS, firewall, switch, or OS. Findings preserve evidence and confidence.

Prefer summarized alerts over raw traffic. No default payload interception, decryption credentials, exploit attempts, indiscriminate scanning, or monitoring outside owned/authorized networks. Deeper flow-derived or packet-derived modules require separate permissions, disclosure, local processing, bounded retention, and design/security review.

## Observed-device adapters and collector ownership

Adapters are implemented case by case using supported mechanisms such as USB/HID, NUT, SNMP, OS audio APIs, vendor APIs/SDKs, serial, REST, SSH, LLDP, or forwarding databases. Listing a mechanism is not a promise of support.

The user explicitly assigns a collector peer. Adapter credentials remain local and do not replicate. Another peer may replicate observations but does not poll the device automatically. Failover occurs only after another peer is explicitly provisioned with the adapter, permissions, and credentials.

When multiple peers observe one device or topology, records reconcile around strong identity while preserving each source and evidence. Contradictions remain visible rather than being silently overwritten.

### Physical LAN self-observation

Physical LAN entities (peer, network interface, overlay interface, segment, VLAN, gateway, switch, port, peripheral, unknown upstream segment) and relationships (attached_to, member_of_segment, member_of_vlan, routes_via, uplink_to, connected_to_port, probable_same_segment) are typed in `MonitorApp.Contracts.LanTopology` with validation; overlay interfaces cannot join segments, VLANs, or gateways. Without a managed switch, each peer models only itself: its own interfaces, link attributes, own MAC (strong claim for physical interfaces only), own subnets and default-gateway IP (weak claims), and Linux VLAN sub-interfaces. Segments are per-observer entities; two peers with matching subnet (and gateway) claims get an inferred `probable_same_segment` edge (confidence 0.3 subnet only, 0.6 with gateway, never 1.0) rather than a merge. Switch and port entities are only filled by a future adapter; today each segment gets an inferred unknown-upstream placeholder. Collection code is `Peer.Collection.Lan`; the node runs `SelfLanCollector` at startup and every 60 s (`NodePeriodicTask`), ends relationships (`ValidTo`) for interfaces or links that vanished, and emits each interface's link up/speed/full-duplex/MTU as signed observations (`lan.link.{interface}.*`, collector `monitor.lan.self`) through the same ingestion bridge as system metrics; a failed or unsupported read changes nothing.

## Persistence

Every peer stores local identity, paired-peer directory, local origin records, retained replicas, per-origin cursors and gaps, capability/adaptor configuration, observed-device identity and collector assignments, physical and overlay relationships with evidence, security audit events, and local UI/retention policy.

SQLite WAL remains the personal-scale starting point. Schema migrations, backup, restore, retention, replica eviction, and identity recovery require real file-backed tests. Restoring a backup must not silently fork one peer identity or origin sequence.

## Experience clients

The local experience renders the same domain model available to replication and query. The living schematic summarizes relationships and state. Domain lenses expose power, audio, storage, network infrastructure, thermal, security, and other coherent subsets. Search reaches exact signals. The final scene remains a projection of the domain model, never the source of truth.

The first functional client may be plain but must expose pairing, peer reachability, synchronization/freshness, observed-device and adapter health, exact values, and provenance. Android must state when lifecycle restrictions prevent continuous peer availability.

The shared client is `MonitorApp.Client.Core` (view models plus `IPeerDataSource` and optional capability interfaces: alerts, viewing interest, pairing, host-service control, remote monitoring control via `IControlSource`, implemented by the phone source over `MobilePeerRuntime.SendControlAsync` with tier mapping in `MonitorApp.Client.Mobile`), `MonitorApp.Client.Ui` (Avalonia views, styles, and the adaptive `MainView` shell used unchanged by every head), and thin heads: `MonitorApp.Client.Desktop` (window plus the loopback HTTP adapter) and `MonitorApp.Android` (`AvaloniaMainActivity` over `MonitorApp.Client.Mobile.InProcessPeerDataSource`, which reads the in-process `MobilePeerRuntime` and its store directly). The home screen is Devices (every known peer with online state and headline metrics); views that stay on screen declare viewing interest so viewed peers sample fast. Live views update by push when the source supports it and poll otherwise. See T036, T040.
The shared client is `MonitorApp.Client.Core` (view models plus `IPeerDataSource` and optional capability interfaces: alerts, viewing interest, pairing, QR-scan pairing, host-service control), `MonitorApp.Client.Ui` (Avalonia views, styles, and the adaptive `MainView` shell used unchanged by every head), and thin heads: `MonitorApp.Client.Desktop` (window plus the loopback HTTP adapter) and `MonitorApp.Android` (`AvaloniaMainActivity` over `MonitorApp.Client.Mobile.InProcessPeerDataSource`, which reads the in-process `MobilePeerRuntime` and its store directly). The home screen is Devices (every known peer with online state and headline metrics); views that stay on screen declare viewing interest so viewed peers sample fast. Live views update by push when the source supports it and poll otherwise. See T036, T040.

The client also keeps actual and design contexts visually and semantically distinct. The default current view shows actual state. A named plan or deliberate overlay may compare actual and design state, with separate costs, quantities, loads, cable availability, connections, freshness, and constraint findings. Historical time applies to recorded actual state; plan revisions use their own revision history and must not masquerade as observed history.

## Planning and constraint services

Constraint modules consume typed objects, relationships, unit-bearing values, and provenance, then emit structured findings with rule/version, inputs, assumptions, confidence, severity, and suggested remediation. Cable, audio, RF, electrical, thermal, dimensional, mounting, and environmental rules remain separate modules over the same model. Unknown or stale inputs yield unresolved findings rather than approval.

The first slice should combine cable matching with one bounded audio or electrical path. Regulatory and life-safety output is decision support, includes jurisdiction/source version when relevant, and does not claim professional certification.

## Sourcing and offer history

Canonical products/variants are separate from seller offers and actual instances. Offer observations preserve seller, SKU, region, currency, condition, price components, availability, source time, retrieval method, URL/provider, and quality. Source adapters use permitted APIs/feeds/retrieval or user entry, respect credentials and rate/access limits, and report blocked or failed retrieval as unavailable rather than zero price or out of stock.

## Automation and notification boundary

Automation is a deterministic peer service independent of UI and AI. Versioned event-condition-action rules persist schedule/evaluation state, debounce/cooldown/deduplication, escalation, expiration, idempotency, and audit evidence. Rules declare whether inputs come from actual state, a named design, sourcing observations, or history. Historical simulation never dispatches live actions.

Equal peers require explicit execution ownership so replicated events cannot produce duplicate notifications or actions. Implemented first slice (T037): the origin peer that collected an observation is its single evaluation owner and replicated copies are never evaluated; built-in rules (peer not seen, observation Unavailable/Error, typed numeric thresholds) raise and resolve idempotent alert episodes into a durable store with a per-recipient outbox, delivered to trusted phones through additive `AlertPull`/`AlertBatch`/`AlertAck` messages on `PeerSession` and shown as Android notifications. On a held-open live session the desktop pushes new alerts (unsolicited `AlertBatch`, woken by the outbox enqueue) and the phone keeps only a 30 s safety-net pull; rule hold durations and a re-fire cooldown debounce flapping (T042). The library is `MonitorApp.Peer.Alerts` with its own schema version table in `peer.db`. Lease/reassignment on collector failover, user-defined rules, escalation, and non-Android transports remain open; third-party and self-hosted push are not used. Operational actions retain a separate permission domain.

## Distributed local AI boundary

AI is an optional capability advertised by eligible peers. Capability metadata includes model/runtime version, supported tasks/tools, context and output limits, resource/energy policy, privacy, and availability. Initial routing sends a bounded job to one eligible peer; distributed tensor execution is not assumed.

AI accesses typed APIs through explicit tool grants. Derived output retains model, input-object/source references, time, and confidence where practical. AI cannot establish identity/trust, authoritatively mutate observations, own replication, run deterministic rules, deliver notifications, or bypass action approval.

## Delivery dependencies

1. Shared contracts, build, and two-peer persistence/replication proof.
2. Universal object envelope, actual/design separation, and plain inventory client.
3. Platform peers, historical reconstruction, and truthful operational monitoring.
4. Cable plus bounded physical constraint module; then sourcing/offer history.
5. Durable automation ownership, notification outbox, and delivery adapters.
6. Optional distributed local-AI routing and tools.
7. Living schematic, exploded architecture, historical replay, and advanced polish over proven domain APIs.

## Reusable current implementation

The controller alpha is not the target topology. These components remain reusable:

- truthful collectors and freshness semantics;
- typed Protobuf values and validation patterns;
- P-256 proof framing and replay-resistant challenges;
- SQLite WAL spool, idempotent sequence, and commit-before-ack behavior;
- Android Keystore identity work and normalization;
- signed update manifest and release tooling;
- real SQLite and loopback gRPC test infrastructure.

These components are superseded or require migration:

- `MonitorApp.Controller` as central authority and database;
- administrator-token enrollment authority;
- controller-owned device challenge lifecycle;
- controller inventory and current-metric API;
- agent-to-controller-only session roles;
- controller archive and Docker Compose release assumptions;
- Headscale adapter and automatic Headscale setup tasks.

The first peer-native components are now present:

- `monitor.v1` discovery, pairing, authenticated session, capability, origin range/batch, cursor, acknowledgement, and explicit incompatibility contracts;
- P-256 peer identity abstraction, bilateral transcript-bound pairing/reconnect proof, replay/expiry handling, protocol negotiation, and deterministic duplicate-session selection;
- versioned file-backed SQLite WAL peer storage for identity, trust/revocation, locators, pairing sessions, immutable origin records, current query, replication cursors/gaps, observed entities and evidence, adapters, capabilities, and collector assignments.
- a production-path collection bridge that signs Windows system snapshots into the local origin stream, a cursor-driven synchronization application, and persisted outbound reconnect scheduling in the Windows preview node.

The Windows engineering preview composes the listener/session coordinator, local API, DPAPI key storage, durable replay state, collection, reconnect, and two-peer convergence path. This is executable Windows/loopback evidence only, not a packaged peer release, Tailscale deployment proof, or non-Windows support claim.

Superseded code remains until the peer path replaces it with executable tests. It must not be packaged or described as the intended product.

## Migration vertical slice

The first decentralized slice must prove:

1. Two fresh peer stores start with independent P-256 identities.
2. One peer contacts the other at a configured loopback/Tailscale-style endpoint.
3. Bilateral short-lived pairing pins both identities.
4. Either peer restarts and reconnects without a controller.
5. Both peers collect local origin observations.
6. Each side replicates contiguous records and durable cursors.
7. Disconnect, additional collection, reconnect, and convergence introduce no gaps or duplicates.
8. A conflicting retry, wrong key, replay, unpaired tailnet member, and unauthorized address replacement fail closed.
9. Cached state exposes source time, last synchronization, quality, and summary fidelity.
10. A synthetic observed device keeps stable identity across collector attachment changes.
11. Multi-peer topology evidence reconciles by strong identity and preserves reported/inferred provenance.
12. Capability registration rejects ordinary-monitoring definitions that expose packet detail, communication identities, sessions, DNS/query history, per-process traffic, or content.

Only after this slice passes should controller code be removed or release artifacts be relabeled as peer packages.

### Always-on service hosting

Windows and Linux peers run as a system service. `Program.cs` parses `NodeOptions` and runs `NodeHost.Build` (Generic Host with `AddWindowsService` and `AddSystemd`); `PeerNodeHostedService` starts and disposes `PeerNodeRuntime`, and startup failures (identity, permission, platform) log critical and exit non-zero. In service mode the data directory defaults to `STATE_DIRECTORY` or `/var/lib/monitor-app` (Linux, owned by the `monitor-app` user, mode 0700) and `%ProgramData%\MonitorApp` (Windows). Configuration precedence is arguments, `MONITOR_APP_*` environment variables (the unit loads `/etc/monitor-app/node.conf`), defaults. The admin page is always loopback; the peer listener defaults to loopback and must be bound explicitly. Packaging lives in `eng/packaging/linux` (DEB script, RPM spec, hardened unit) and `eng/packaging/windows` (service scripts, virtual account `NT SERVICE\MonitorApp`, delayed auto-start, recovery actions). `WindowsDpapiIdentityProvider` uses DPAPI current-user scope, so the identity is bound to the service account and moving the service to another account makes it unreadable; this is untested on Windows. Status: see `TODO.md` and `docs/TESTING.md` for exactly what was run.

## Platform gates

### Windows

- Windows Service lifecycle and clean uninstall.
- Protected peer key, database, and adapter credentials.
- Tailscale-interface binding and firewall scope.
- Platform-local collectors and permission-denial behavior.

### Fedora

- RPM and `systemd` lifecycle.
- SELinux behavior without disabling enforcement.
- Protected identity, database, and adapter credentials.
- Platform-local collectors and Tailscale listener.

### Ubuntu

- DEB and `systemd` lifecycle.
- Supported-version install/upgrade/uninstall tests.
- Protected identity, database, and adapter credentials.
- Platform-local collectors and Tailscale listener.

### Android

- Outbound-only peer (T031): no listener; the phone dials the desktop and both origin streams replicate over that session. `MonitorApp.Peer.Mobile` (plain net10.0) composes PeerStore, signed-origin ingestion of Android readings (`monitor.android.device`), explicit pairing from a `PeerPairingInvitation`, and a reconnecting sync loop; the identity is injected (`ILocalPeerIdentityProvider`).
- Phone pairing (T033): `LocalApiHost` renders the "Add a phone" section (`PhonePairingService`, QR via QRCoder as inline SVG). `Peer.Core` holds `PairingShortCode`/`PairingShortCodeRegistry` (shared in-process by the admin page and the peer gRPC host); `Peer.Service` exposes `PeerPairingCodeService.Redeem`; `Peer.Transport.PeerPairingCodeClient` and `MobilePeerRuntime.PairWithCodeAsync` are the phone side, which then runs the normal bilateral pairing.
- `src/MonitorApp.Android` (outside the solution) is a thin shell: a user-started foreground service hosts the runtime (`Start`/`StopAsync`); the activity shows health, pairing, and an empty alert list.
- Android Keystore identity.
- Emulator tests plus one physical-device smoke test before support is claimed.

## Deferred architecture

- Transitive peer introduction or global membership authority.
- Shared-metadata merge semantics.
- Automatic collector failover or credential replication.
- Operational control actions.
- Multi-user roles.
- Notification transport selection and tested remote delivery (the alert capability itself is confirmed scope).
- Automation execution ownership, rule conflict semantics, and outbox reassignment.
- Retailer/source adapter selection and price-normalization policy.
- Local-AI model/runtime selection and resource budgets.
- Advanced retention rollups.
- Final rendering technology and game-like polish.
