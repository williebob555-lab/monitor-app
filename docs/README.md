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

The equal-peer foundation now includes generated contracts, bilateral P-256 proof and compatibility logic, a durable approval/replay coordinator, a real file-backed peer SQLite store, signed origin replication, and a fail-closed gRPC host tested over Windows loopback. The immediate milestone is wiring those pieces into one runnable peer plus a local administration/query surface, then proving two separate peers can pair, restart, reconnect, and converge without duplicate origin records. Observed-device adapters, universal object identity, and later product features build on that path. These foundations are not yet a peer release or platform support claim.
