# Monitor App Short-Term Work

This file contains active and near-term tasks only. Read it with the required documentation preflight before changing the repository.

## Current milestone: two equal peers

- [x] Record the confirmed decentralized product direction and supersede controller/Headscale deployment guidance.
- [x] Reconcile the agreed inventory/planning, actual-versus-design, cable, physical-constraint, sourcing, automation/notification, distributed-local-AI, and phased-roadmap scope across the documentation without claiming implementation.
- [x] Make repository preflight, collaboration, progress reporting, and documentation-only verification risk-based to reduce unnecessary usage.
- [ ] Inventory reusable controller-era contracts, P-256 proof code, SQLite stores, telemetry/spool logic, collectors, Android code, and update verification; identify exact extraction/deletion steps.
- [x] Define versioned peer contracts for discovery hint, bilateral pairing, authenticated reconnect, capability exchange, origin stream query, replication cursor, and explicit compatibility failure.
- [x] Define and implement the peer-local SQLite schema for application identity/trust, entities/relationships, origin observations, replicated observations, cursors, capability catalog, collector assignments, and adapter health.
- [ ] Implement a peer service and local API that collect, persist, query, and replicate without a controller or central database.
- [ ] Add the real two-peer integration test: separate file-backed SQLite stores; pair; exchange; disconnect; collect offline; restart; reconnect; converge; prove ordered origin identity and no duplicate/conflicting sequences.
- [ ] Add security tests for network membership without app trust, wrong-key/substitution/replay/revocation, safe errors, private-interface binding, and credential redaction.

## Capability and collection foundation

- [ ] Implement the versioned namespaced capability-module contract and canonical observation catalog metadata.
- [ ] Enforce observation identity/source/time/unit/quality/freshness/cost and explicit unsupported/denied/stale/unavailable states.
- [ ] Add compatibility tests for module versions, truthful fallback inspector descriptors, and purpose-built inspector selection.
- [ ] Add registration tests that allow aggregate operational network metrics but reject ordinary packet/content/flow/communication-identity declarations.
- [ ] Add separately enabled defensive-security capability policy with evidence/confidence, permissions, local processing, and bounded retention.
- [ ] Define the smallest universal object envelope and typed relationship contract needed to keep actual state and named design scenarios separate; include product/offer/instance identity and the connector-length cable slice without starting sourcing, automation, or AI services.

## Observed devices and LAN topology

- [ ] Define stable observed-device/component identity and typed attachment/collection relationships independent of collection host.
- [ ] Implement one bounded peripheral adapter spike selected from available hardware; test adapter health, freshness, permission denial, and host reassignment.
- [ ] Define physical LAN entities for switches, ports, VLANs, uplinks, peers, and peripherals separately from the Tailscale overlay.
- [ ] Implement one bounded managed-switch adapter spike selected from available equipment; preserve reported/inferred evidence and reconcile multi-peer reports by strong identity, not IP.
- [ ] Keep collector ownership explicit, credentials local, and failover disabled until another peer is separately provisioned; test these boundaries.

## Platform and release gates

- [ ] Adapt Windows, Fedora, Ubuntu, and Android hosts to the peer service and prove platform-local identity storage, lifecycle, pairing, reconnect, collection, and query behavior.
- [ ] Add clean install, upgrade, restart, recovery, and uninstall tests for Windows service, Fedora RPM, Ubuntu DEB, and Android package paths.
- [ ] Preserve signed update-manifest verification and implement tested platform-specific apply/rollback.
- [ ] Add bounded historical query/reconstruction and a plain usable local client before advanced visuals.

## Deferred until the functional peer foundation works

- Final living-schematic and exploded-architecture rendering.
- Game-like interactions and cinematic transitions.
- Full cable inventory UI beyond the bounded object-model slice; QR and dedicated scanner workflows are outside the required path.
- Physical audio/RF/electrical/thermal constraint engines and plan comparison.
- Product sourcing, seller adapters, price and availability history.
- Durable automation ownership, notification outbox, Android/desktop push delivery, and historical rule simulation.
- Distributed local-AI capability routing, tools, and derived-artifact provenance.
- Operational control actions, multi-user roles, transitive trust, automatic collector failover, and shared-metadata consensus.
