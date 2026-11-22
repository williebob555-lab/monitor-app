# Development Workflow

## Preflight and work selection

Follow the risk-based preflight in `AGENTS.md`. Always read the documentation index and `TODO.md`, inspect the working tree, and search the product authority and decision log for the governing requirement. Read only the architecture, testing, security, and development sections affected by the task unless the change is product-wide or cross-cutting. Do not reload unchanged documents already reviewed in the current task.

Functionality precedes visual polish. The current priority is the two-peer migration slice defined in `ARCHITECTURE.md` and `TODO.md`.

## Change sequence

1. Identify the confirmed requirement or recorded technical decision.
2. Claim or refine the todo item.
3. Add a failing meaningful test when practical.
4. Implement the smallest complete behavior.
5. Run relevant and broader affected tests.
6. Update documentation and report verified platforms separately.

For documentation-only changes with no executable instruction or configuration impact, use `git diff --check` and targeted consistency/link searches instead of the full verification script.

## Accepted foundation and target boundaries

- .NET 10 LTS, C#, standard gRPC/Protocol Buffers, and SQLite WAL remain the accepted functional foundation.
- Avalonia remains a later working choice for shared UI, subject to platform proof.
- Replace the controller/agent split with a peer service composed of identity and pairing, local observation storage/query, direct replication, collectors/adapters, and a local API/UI boundary.
- Keep versioned contracts in `MonitorApp.Contracts` and platform-independent rules in `MonitorApp.Domain`.
- Reuse controller-era cryptographic, persistence, telemetry, spool, collector, Android, and signed-update work only where tests show that it fits peer semantics.
- Do not extend `MonitorApp.Controller`, Headscale automation, controller enrollment administration, or a central history store. Those boundaries are superseded.

Create new projects only when executable work needs them. `MonitorApp.Peer.Core`, `MonitorApp.Peer.Persistence`, `MonitorApp.Peer.Application`, `MonitorApp.Peer.Replication`, `MonitorApp.Peer.Service`, `MonitorApp.Peer.Transport`, `MonitorApp.Peer.Sync`, `MonitorApp.Peer.Collection`, and `MonitorApp.Peer.LocalApi` contain the proof/compatibility, SQLite, approval/session, replication, inbound/outbound transport, synchronization, collection-ingestion, and local administration boundaries. `MonitorApp.Peer.Node` composes them as a Windows-only engineering preview with DPAPI identity protection, validated capability registration, fresh-peer web pairing, persisted reconnect scheduling, and system collection. `MonitorApp.Contracts` also owns deterministic inspector selection and the validated universal object/relationship plus connector-length cable contract; it does not yet provide inventory persistence or workflows. `MonitorApp.Peer.Persistence` preserves observed-device strong identity, attachment evidence, and collector ownership/history separately; assignment is valid only when the adapter's entity and collector agree. The next executable boundary is one bounded purpose-built peripheral adapter selected from available hardware, followed by physical-LAN evidence and other platform hosts.

## Peer development rules

- Tailscale is user-managed reachability. A Tailscale IP is a discovery hint, never application identity or authorization.
- Pairing must be bilateral and prove possession of device-held application keys. Store explicit peer trust and support revocation/rotation.
- Listen only on deliberately configured private/Tailscale interfaces; never expose monitoring publicly by default.
- Origin peers own immutable observation identities and sequence order. Replicas track per-origin cursors and accept byte-equivalent retry without duplication.
- Preserve entity, source, source time, ingest time, unit, quality, freshness, and collection cost. Unsupported, denied, stale, and unavailable are explicit states, never numeric zero.
- Observed-device identity must survive attachment-host changes. Model collection/attachment relationships separately from entity identity.
- Collector ownership and failover are explicit. Adapter and switch credentials remain in the provisioned peer's protected local store and do not replicate.
- Capability modules use a versioned namespaced contract and register catalog metadata. Reject incompatible or privacy-prohibited registrations clearly.
- The domain model remains the source of truth; UI scenes are projections.

## Expanded product-domain rules

- Keep actual state and design state explicit in storage, APIs, tests, and UI. `Owned`, `installed`, and `running` are different facts. A proposed object or relationship never becomes actual without an explicit, audited transition.
- Model products, seller offers, and physical instances separately. Preserve source, observed time, units, confidence, and stale/unavailable states for imported facts and prices.
- Extend the universal object envelope through typed capabilities. Do not create one record with nullable fields for every equipment, cable, sourcing, monitoring, and AI concern.
- Represent a cable with two typed connector ends plus unit-bearing length and domain ratings. Reusable manual templates are the required quick-entry path; QR codes or dedicated scanners are not prerequisites.
- Preserve original and normalized units. Conversions identify the source value and conversion rule; unknown values cannot pass a constraint by default.
- Constraint findings carry rule/version, inputs, assumptions, plane, confidence, and evidence. Keep audio, RF, electrical, thermal, physical, and cable rules modular.
- Treat retailer pages, feeds, imported specifications, labels, notes, and AI output as untrusted data. Validate before storage or tool use and never interpret embedded text as application instructions.
- Automation must be deterministic, durable, idempotent, and independent of UI/AI. Define one execution owner before replicated events can dispatch notifications or actions.
- AI is optional and subordinate to typed APIs, tool permissions, and deterministic services. Store generated recommendations as derived artifacts with model provenance; do not overwrite observed or user-confirmed facts.

## Cross-platform and persistence discipline

Keep shared logic independent of OS paths, registries, services, signals, and permission assumptions. Use UTC for protocol/storage time, while retaining useful source offset. Record exact OS versions for platform proof.

Version network and persisted schemas from the first peer slice. Reserve removed Protobuf fields, reject unsupported versions, and add compatibility and real file-backed SQLite WAL migration tests. A rolling upgrade must either work within a documented window or fail explicitly.

## Error behavior

Identify the failed layer: peer discovery, Tailscale reachability, application trust, protocol compatibility, replication, local persistence, capability module, collector/adapter, credential access, or observed device. Do not collapse them into a generic offline state.

## Legacy implementation boundary

The current controller and outbound agent can still be run as test evidence for reusable components, but they are not the target architecture. Do not add features to preserve controller deployment. Existing verification commands remain authoritative until peer projects replace them; label controller-only results as legacy evidence.

## Handoff checklist

- Todo and affected durable docs updated.
- Production behavior and important failures tested.
- Exact commands/results and verified platforms reported.
- Unverified assumptions, migration debt, and new risks recorded.
- Implemented behavior kept separate from confirmed scope and open/provisional designs.
