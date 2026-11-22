# Monitor App

Monitor App is becoming a decentralized monitoring, inventory, and design environment for Android, Windows, Fedora, and Ubuntu. Every phone or computer is an equal peer with local collection, storage, query, and UI. The user introduces a peer by Tailscale IP; bilateral Monitor App pairing then establishes application identity, and paired services reconnect directly whenever both run. Tailscale is user-managed reachability, not Monitor App identity or authorization.

The intended experience remains a living schematic paired with exploded device architecture, historical time travel, and deliberately enjoyable—even gratuitously game-like—interaction. Functionality and truthful data come first.

The confirmed product scope also includes a universal inventory for equipment, spaces, cables, projects, products, and offers; physical audio/RF/electrical/thermal design constraints; sourcing and price history; customizable automation and push alerts; and optional distributed local AI. Actual inventory and running state remain explicitly separate from named plans and design scenarios. Cable planning centers on the two connector ends, length, ratings, condition, location, and current/planned connections, with fast manual templates rather than required QR or scanner hardware.

## Current status

The repository contains a tested controller-era functional alpha plus an executable equal-peer foundation: versioned peer gRPC contracts; bilateral P-256 fresh pairing and reconnect with explicit approval and durable replay rejection; a versioned peer-local SQLite WAL store; signed, bounded, gap-aware origin replication; fail-closed inbound/outbound gRPC transport; and a loopback local administration page. A Windows preview node composes those pieces with a restart-stable DPAPI-protected identity, scheduled local system collection, persisted automatic reconnect, and origin-history synchronization. A real two-node web/gRPC/SQLite test proves two fresh nodes with separate databases can pair, collect while disconnected, restart, reconnect, and converge without duplicate or conflicting sequences. It remains an engineering preview, not a peer release: Fedora, Ubuntu, and Android peer hosts, native installers, update apply/rollback, LAN topology, and the historical/visual client are not yet built. The controller/agent deployment and automatic Headscale direction remain **superseded** migration evidence.

The two-equal-peer milestone is functionally complete on Windows; the next work is platform hosts, LAN topology, packaging, and a plain history client. The universal inventory, actual/design split, cable system, constraint modules, sourcing, automation, notifications, local AI, history reconstruction, and final UI are documented plans and are not implemented. See [TODO.md](TODO.md).

## Run the Windows preview

```powershell
dotnet run --project src/MonitorApp.Peer.Node/MonitorApp.Peer.Node.csproj
```

Open `http://127.0.0.1:55432/`. The page shows the local peer ID/fingerprint, fresh-peer invitation form, pending approvals, trust state, and stored-observation query guidance. The peer listener runs on `127.0.0.1:55431` by default. Use `--data-dir`, `--peer-bind`, `--peer-port`, and `--admin-port` to run another node with separate settings. Enter the second node's displayed ID/fingerprint and peer address in the first node, then approve the pending request on the second node. The administration page remains loopback-only.

## Target shape

```text
Windows / Fedora / Ubuntu / Android peer
  identity + bilateral trust
  local collectors, storage, query, and UI
  direct authenticated replication over Tailscale reachability
            <------------------------------>
another equal peer with the same responsibilities

Observed UPS/audio/switch/etc. -- purpose-built adapter --> provisioned host peer
```

Observed peripherals and managed LAN equipment are typed entities, not peers. A provisioned host collects them through case-specific adapters; stable identity/history survives attachment-host changes. Collector ownership is explicit and credentials remain local.

Versioned capability modules and a canonical observation catalog support broad, categorized measurement. Aggregate operational network data is allowed, but ordinary monitoring does not collect packet/content/flow/session/DNS/communication-identity histories. Defensive threat detection is a separate explicitly enabled capability with evidence, confidence, permissions, and bounded retention.

## Documentation and verification

The product authority is [Monitor App Design Specification.txt](Monitor%20App%20Design%20Specification.txt). Before changing the project, read [AGENTS.md](AGENTS.md), the [documentation index](docs/README.md), and [TODO.md](TODO.md). Architecture, testing, security, decisions, and release trust are maintained under [docs](docs/README.md).

The existing verification entry point remains:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\eng\verify.ps1
```

Until peer projects replace the legacy boundaries, passing controller/agent tests proves reusable components only; it does not prove the decentralized target or platform support. [docs/TESTING.md](docs/TESTING.md) lists the required peer and platform gates.

Private-repository publishing and signed-release preparation remain documented in [docs/RELEASES.md](docs/RELEASES.md). Native installation, update apply/rollback, and all four platform lifecycle paths remain release gates.
