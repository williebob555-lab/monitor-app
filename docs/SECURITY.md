# Security and Privacy Model

## Scope and trust model

Monitor App is a decentralized personal monitoring mesh. There is no trusted Monitor App controller. Each peer protects its own identity key, local history, trust records, adapter credentials, and UI/API boundary.

Tailscale provides private reachability only. A Tailscale address, tailnet membership, LAN presence, hostname, or claimed device id never authenticates or authorizes a Monitor App peer. Pairing must be bilateral, prove possession of application keys, show the user the identity being trusted, and persist explicit pairwise trust. Reconnection uses that application identity with fresh replay-resistant authentication.

The first mesh is pairwise trust for personal scale. Transitive trust and global membership authority are out of scope. Revocation and key rotation must be explicit. Losing one peer must not silently revoke or authorize another.

## Peer transport and local storage

- Use authenticated encrypted transport even over Tailscale; verify peer application identity and fail closed on mismatch.
- Bind proof to the intended peer/session and fresh challenge. Limit message sizes, rates, buffers, and parallel work.
- Listen only on intentionally configured private/Tailscale interfaces, never public interfaces by default.
- Store private keys in platform-protected storage where available. Never transmit private keys or log pairing secrets, challenges, signatures, or reusable credentials.
- Protect each peer's SQLite history, trust database, offline replication state, and backups with OS access controls. Diagnostics must be inspectable and redactable.
- Origin records retain immutable ids and sequence. Replication verifies origin identity, validates schemas/timestamps, is idempotent, and never grants control authority.

## Observed devices, adapters, and LAN infrastructure

Observed devices cannot authenticate as peers. A provisioned peer acts as collection gateway through a reviewed adapter. Every observation identifies the observed entity, collecting peer, adapter/module, source time, evidence, and quality.

Adapter and managed-switch credentials stay in the provisioned collector peer's protected local store and do not replicate. Collector ownership is explicit. Another peer may take over only after separate provisioning; automatic credential transfer or failover is prohibited. Prefer least-privilege, read-only accounts and isolate privileged collection where practical.

Topology evidence distinguishes reported facts from inference and carries source, time, quality, and confidence. Deduplicate switches with strong identity such as serial or chassis MAC, never IP alone.

## Collection breadth and privacy

“Anything that can be measured should be measured” means broad capability coverage with truthful categorization, not unlimited surveillance. Capability registration declares privacy class, cost, sampling/retention metadata, and required permissions. Secrets, authentication material, message/document contents, unrelated file contents, and unrelated payloads are excluded. Sensitive or high-cost categories require visible, explicit enablement.

Unsupported, denied, stale, and unavailable observations remain explicit; they must never be converted to zero. Raw detail stays local by default; replication uses declared summaries and budgets where appropriate.

## Network-data boundary

Ordinary monitoring may collect aggregate operational measurements without communication identities or content, including bandwidth/throughput, negotiated speeds, utilization, aggregate byte/packet totals, aggregate errors/drops/discards/retries/queue health, latency/reachability, and historical aggregate trends. Infrastructure facts such as interface/switch/port identity, link state, LLDP relationships explicitly reported by equipment, VLAN/admin configuration, PoE state/power, and management health are also allowed.

Ordinary monitoring must not collect packet captures, packet headers or payloads, individual packet records, flow/session/connection/socket logs, source/destination identities, DNS/query history, per-app/process traffic, content classification, or DPI. Module review and executable registration tests must reject prohibited observation declarations and permissions.

## Defensive security capability

Automated threat detection is a separate, explicitly enabled security capability. Baseline detection may use aggregate metrics, device identity/authentication failures, unexpected peer/config/link changes, unusual utilization, and existing summarized alerts from IDS, firewall, switch, or OS sources. Prefer summarized alerts over raw packets. Findings must preserve evidence, source, time, and confidence and must not claim compromise without support.

No default payload interception, credential/decryption collection, exploit attempt, indiscriminate scan, or monitoring outside networks the user owns or is authorized to monitor is allowed. Any deeper flow- or packet-derived security module requires separate permission, clear disclosure, explicit enablement, local processing, bounded retention, legal/authorization review, and additional security testing. Enabling it does not weaken the ordinary-monitoring prohibition.

## Action and update safety

The functional foundation is read-only. Future actions use a distinct permission domain; playful gestures never dispatch actions, historical mode cannot target live systems, material actions require preview/confirmation, commands expire, and results are audited.

Update manifests and artifacts remain signed and target/version/hash checked. Trusted release keys arrive through an authenticated installer or prior trusted release. Never introduce unsigned fallback, silent downgrade, or automatic overwrite as recovery.

## Required security tests

- Tailnet/LAN membership without a paired application identity fails.
- Wrong key, peer substitution, replay, expired challenge, and revoked peer fail closed without secret leakage.
- Replication rejects altered origin identity, conflicting duplicate sequence, incompatible schema, and unauthorized stream requests.
- Capability registration rejects prohibited network-data categories and undeclared elevated permissions.
- Adapter credentials never appear in replicated records, logs, exports, or module descriptors.
- Collector failover cannot occur until the second peer is explicitly provisioned.
- Security findings require evidence/confidence; deeper security modules cannot start without their separate enablement and retention policy.
- Tampered or incorrectly targeted update manifests/artifacts fail closed.
