# Project Documentation

This directory is Monitor App's operational memory. Read it with `TODO.md` before changing the repository, and keep it accurate as part of every change.

## Authority and current direction

[`Monitor App Design Specification.txt`](../Monitor%20App%20Design%20Specification.txt) is the product authority. Its labels distinguish confirmed user direction from proposals and open exploration.

The confirmed deployment model is a decentralized personal mesh:

- There is no Monitor App controller, central database, or always-on Monitor App host.
- Every supported phone or computer is an equal peer with local collection, storage, query, and UI.
- A user introduces a peer by Tailscale IP, then application-level pairing establishes identity and trust. Tailscale provides reachability only.
- Paired services reconnect directly when both run and replicate origin-owned history with explicit freshness and eventual convergence.
- Host peers collect observed peripherals and LAN infrastructure through purpose-built adapters. Collector credentials remain local.
- Versioned capability modules and a canonical observation catalog provide broad but categorized collection without flattening the purpose-built experience.
- A universal object model will connect monitored equipment, physical inventory, cables, spaces, projects, candidate products, and offers while keeping actual/owned/running state separate from design scenarios.
- Planned later modules cover connector/length-centered cable management, physical audio/RF/electrical/thermal constraints, sourcing and price history, deterministic automation and notifications, and optional distributed local AI.

The earlier controller/agent and automatic self-hosted Headscale plan is superseded. Existing controller-era code is migration material, not the intended deployment architecture.

## Index

- [`ARCHITECTURE.md`](ARCHITECTURE.md): components, trust boundaries, data flow, and migration slice.
- [`DEVELOPMENT.md`](DEVELOPMENT.md): implementation workflow and platform obligations.
- [`TESTING.md`](TESTING.md): meaningful tests and release gates.
- [`SECURITY.md`](SECURITY.md): peer trust, adapter credentials, privacy, and defensive detection.
- [`DECISIONS.md`](DECISIONS.md): confirmed constraints and working technical decisions.
- [`CHANGELOG.md`](CHANGELOG.md): durable completed changes.
- [`RELEASES.md`](RELEASES.md): release and update trust.
- [`../TODO.md`](../TODO.md): active and near-term work only.
- [`../AGENTS.md`](../AGENTS.md): mandatory repository instructions.

## Current phase

The equal-peer foundation now includes generated peer, capability-module, and universal-inventory contracts; bilateral P-256 fresh-pairing/reconnect logic; durable approval/replay coordination; file-backed SQLite; signed origin replication; fail-closed inbound/outbound gRPC transport; and a loopback administration/query surface. A Windows preview node composes these with a DPAPI-protected persistent identity, a validated system capability declaration, scheduled system collection, persisted peer locators, and automatic reconnect. A real two-node test drives bilateral web approval, exchanges signed observations, collects while disconnected, restarts both nodes, and proves convergence in separate databases. Contract tests keep actual inventory separate from named design revisions, distinguish product/offer/physical-instance identities, validate typed relationships and the first connector/length cable slice, and select an installed purpose-built inspector or an honest generated fallback. Peer persistence preserves strong observed-device identity, attachment evidence, and collector-assignment history across host reassignment while refusing IP-only strong identity. A provisional read-only Yamaha AG06MK2 adapter proves the Windows peripheral boundary with explicit available, unavailable, denied, and error states. Release packaging now includes the Windows peer preview, but no current peer release has been tagged or published. This preview is not yet a supported cross-platform release.
