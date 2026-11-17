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

The project is migrating the functional foundation from a working controller-era alpha to equal peers. The next bounded milestone is two real peers that pair, authenticate independently of Tailscale identity, persist locally, disconnect, reconnect, and converge without duplicate origin records. Observed-device adapters, capability registration, and platform packaging build on that path. Advanced visuals follow functional proof.
