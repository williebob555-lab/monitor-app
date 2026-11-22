# Monitor App Short-Term Work

This file contains active and near-term tasks only. Read it with the required documentation preflight before changing the repository.

## Current milestone: two equal peers

- [x] Record the confirmed decentralized product direction and supersede controller/Headscale deployment guidance.
- [x] Reconcile the agreed inventory/planning, actual-versus-design, cable, physical-constraint, sourcing, automation/notification, distributed-local-AI, and phased-roadmap scope across the documentation without claiming implementation.
- [x] Make repository preflight, collaboration, progress reporting, and documentation-only verification risk-based to reduce unnecessary usage.
- [ ] Inventory reusable controller-era contracts, P-256 proof code, SQLite stores, telemetry/spool logic, collectors, Android code, and update verification; identify exact extraction/deletion steps.
- [x] Define versioned peer contracts for discovery hint, bilateral pairing, authenticated reconnect, capability exchange, origin stream query, replication cursor, and explicit compatibility failure.
- [x] Define and implement the peer-local SQLite schema for application identity/trust, entities/relationships, origin observations, replicated observations, cursors, capability catalog, collector assignments, and adapter health.
- [x] Implement and test the peer host boundary, durable pairing/session coordinator, and signed origin-replication coordinator.
- [x] Compose a Windows engineering-preview node with DPAPI-protected identity, peer listener, outbound reconnect transport, and loopback administration/query page.
- [x] Implement and compose initial fresh-peer invitation/pairing through explicit initiating and contacted-peer approval with bilateral proof and trust persistence.
- [x] Complete production collection and local query/replication composition without a controller or central database.
- [x] Add the real two-peer integration test: separate file-backed SQLite stores; pair; exchange; disconnect; collect offline; restart; reconnect; converge; prove ordered origin identity and no duplicate/conflicting sequences.
- [x] Add security tests for network membership without app trust, wrong-key/substitution/replay/revocation, safe errors, private-interface binding, and credential redaction.

## Capability and collection foundation

- [x] Implement the versioned namespaced capability-module contract and canonical observation catalog metadata.
- [x] Enforce observation identity/source/time/unit/quality/freshness/cost and explicit unsupported/denied/stale/unavailable states.
- [x] Add deterministic purpose-built inspector selection with truthful generated fallback tests.
- [x] Add registration tests that allow aggregate operational network metrics but reject ordinary packet/content/flow/communication-identity declarations.
- [x] Add separately enabled defensive-security capability policy with evidence/confidence, permissions, local processing, and bounded retention.
- [x] Define and validate the smallest universal object envelope and typed relationship contract needed to keep actual state and named design scenarios separate; include distinct product/offer/instance identity and the connector-length cable slice without starting sourcing, automation, or AI services.

## Observed devices and LAN topology

- [x] Define and test stable observed-device/component identity, typed attachment evidence, and explicit collector-assignment history independent of collection host; IP addresses cannot act as strong merge identity.
- [x] Implement a provisional read-only Yamaha AG06MK2 Windows adapter spike from connected hardware; preserve configured identity/collector, driver and attachment evidence, freshness, explicit unavailable/permission/error states, and the tested host-reassignment boundary.
- [ ] Define physical LAN entities for switches, ports, VLANs, uplinks, peers, and peripherals separately from the Tailscale overlay.
- [ ] Implement one bounded managed-switch adapter spike selected from available equipment; preserve reported/inferred evidence and reconcile multi-peer reports by strong identity, not IP.
- [ ] Keep collector ownership explicit, credentials local, and failover disabled until another peer is separately provisioned; test these boundaries.

## Platform and release gates

- [x] Android peer slice: portable outbound-only runtime (`MonitorApp.Peer.Mobile`), pairing invitation payload, Android readings through the peer ingestion path, foreground-service host, real loopback two-store integration test (T031). APK build, emulator, and physical-device behavior are unverified.
- [ ] Android: QR rendering of the invitation on the desktop admin page (after LocalApi work) and camera scan on the phone; typed/pasted invitation works now.
- [ ] Android: device/emulator verification of the foreground service, Doze/battery-optimization behavior, Keystore identity, and cleartext-over-VPN gRPC; regenerate `src/MonitorApp.Android/packages.lock.json` with the workload; signed sideload APK on GitHub releases (release workflow untouched so far).
- [ ] Not implemented: adaptive sampling (about 1 s while viewed, about 30 s otherwise) and tiered retention (7 days full, hourly rollups 1 year, events for years, pinned incidents forever); alert rules and alert list content.

- [ ] Adapt Windows, Fedora, Ubuntu, and Android hosts to the peer service and prove platform-local identity storage, lifecycle, pairing, reconnect, collection, and query behavior.
  - Progress: Linux engineering preview, verified in an Ubuntu 24.04 container: the peer node targets `net10.0`, stores its identity in an owner-only `identity.p256` file, and runs the node tests on Linux. Fedora, packaging, `systemd`, SELinux, and a Linux service remain unproven.
- [ ] Always-on service decision (Windows and Linux peers run as a system service): host, data dir, DEB build, hardened unit, and Windows scripts are written. Verified only on an Ubuntu 24.04 container without systemd PID 1: host start/stop/restart tests, `.deb` build, `dpkg -i`, running as the `monitor-app` user, `dpkg -r` keeps data, `dpkg --purge` deletes data/config/user. Unverified: `systemctl` start/notify/hardening (`systemd-analyze security`), upgrade, RPM build and Fedora/SELinux, Windows service install/DPAPI under `NT SERVICE\MonitorApp`/ACLs/recovery, MSI, Tailscale-interface bind and firewall.
- [ ] Add clean install, upgrade, restart, recovery, and uninstall tests for Windows service, Fedora RPM, Ubuntu DEB, and Android package paths.
- [ ] Preserve signed update-manifest verification and implement tested platform-specific apply/rollback.
- [x] Add bounded historical query/reconstruction (versioned `/api/v1/history`, `/state`, `/catalog`; resolution field `raw`) and a plain test-only local page. Follow-up: tiered retention/rollups (7d raw, hourly to 1y) and the separate Avalonia client consuming these endpoints.

## Deferred until the functional peer foundation works

- Final living-schematic and exploded-architecture rendering.
- Game-like interactions and cinematic transitions.
- Full cable inventory UI beyond the bounded object-model slice; QR and dedicated scanner workflows are outside the required path.
- Physical audio/RF/electrical/thermal constraint engines and plan comparison.
- Product sourcing, seller adapters, price and availability history.
- Durable automation ownership, notification outbox, Android/desktop push delivery, and historical rule simulation.
- Distributed local-AI capability routing, tools, and derived-artifact provenance.
- Operational control actions, multi-user roles, transitive trust, automatic collector failover, and shared-metadata consensus.
