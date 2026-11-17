# Development Workflow

## Preflight and work selection

Follow `AGENTS.md` before every change. Read the product authority, documentation set, decision log, change log, and `TODO.md`; then inspect the working tree for overlapping work. Select or refine a short-term todo with one observable result and its proof.

Functionality precedes visual polish. The current priority is the two-peer migration slice defined in `ARCHITECTURE.md` and `TODO.md`.

## Change sequence

1. Identify the confirmed requirement or recorded technical decision.
2. Claim or refine the todo item.
3. Add a failing meaningful test when practical.
4. Implement the smallest complete behavior.
5. Run relevant and broader affected tests.
6. Update documentation and report verified platforms separately.

## Accepted foundation and target boundaries

- .NET 10 LTS, C#, standard gRPC/Protocol Buffers, and SQLite WAL remain the accepted functional foundation.
- Avalonia remains a later working choice for shared UI, subject to platform proof.
- Replace the controller/agent split with a peer service composed of identity and pairing, local observation storage/query, direct replication, collectors/adapters, and a local API/UI boundary.
- Keep versioned contracts in `MonitorApp.Contracts` and platform-independent rules in `MonitorApp.Domain`.
- Reuse controller-era cryptographic, persistence, telemetry, spool, collector, Android, and signed-update work only where tests show that it fits peer semantics.
- Do not extend `MonitorApp.Controller`, Headscale automation, controller enrollment administration, or a central history store. Those boundaries are superseded.

Create new projects only when executable work needs them. Likely target responsibilities are `MonitorApp.Peer.Core`, `MonitorApp.Peer.Service`, local persistence, capability modules/adapters, and platform hosts; names are not a product requirement.

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
