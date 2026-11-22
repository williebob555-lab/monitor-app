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

## Inventory, planning, and cable data

Actual inventory and running state are security-relevant facts. Design scenarios, candidate products, and proposed connections must not silently change actual state, collector configuration, automation inputs, or device controls. Promotion from design to actual is explicit and audited. Plan overlays must remain clearly labeled so an operator cannot mistake a proposed power, signal, network, or cable path for an installed one.

Cable records may expose work locations, equipment relationships, custody, and operational topology. Apply local access control, minimize exports, and keep optional labels free of secrets. The primary cable workflow uses manual templates and does not require QR codes or dedicated scanning hardware.

## External sourcing and notification egress

Product/source adapters and notification transports create outbound data flows. Each adapter declares destinations, fields sent, credentials, rate limits, retention, and third-party exposure. Store retailer/API credentials and notification tokens in protected local storage; never replicate or export them by default. Minimize notification text shown on lock screens and provide a privacy-safe mode.

Treat retailer pages, feeds, product specifications, URLs, labels, and imported notes as untrusted data. Sanitize stored/displayed content, bound fetches and parsing, resist server-side request forgery, and never let source text choose tools, destinations, credentials, or actions. Respect provider terms and access restrictions. A retrieval failure is unavailable evidence, not price zero or out of stock.

Automation requires a durable execution owner and idempotency boundary so replicated events cannot duplicate notifications or actions. Rule changes, ownership transfer, destinations, and permission changes are audited. Notification permission never implies operational-control permission.

## Local AI safety

Models, prompts, retrieved text, embeddings, and generated output are untrusted. AI receives explicit task and tool grants and the minimum actual/design context required. Tool validation and authorization occur outside the model. Prompt or retrieved text cannot expand permissions, reveal credentials, alter peer trust, or bypass confirmation.

Generated facts and recommendations retain model/runtime version and source references and do not overwrite observed or user-confirmed facts. AI does not own replication, automation schedules, notification delivery, identity, or trust. Material actions use the ordinary permission, preview, confirmation, expiration, idempotency, and audit path regardless of who proposed them.

## Required security tests

- Tailnet/LAN membership without a paired application identity fails.
- Wrong key, peer substitution, replay, expired challenge, and revoked peer fail closed without secret leakage.
- Replication rejects altered origin identity, conflicting duplicate sequence, incompatible schema, and unauthorized stream requests.
- Capability registration rejects prohibited network-data categories and undeclared elevated permissions.
- Adapter credentials never appear in replicated records, logs, exports, or module descriptors.
- Collector failover cannot occur until the second peer is explicitly provisioned.
- Security findings require evidence/confidence; deeper security modules cannot start without their separate enablement and retention policy.
- Tampered or incorrectly targeted update manifests/artifacts fail closed.
- Design-only objects and relationships cannot be consumed as actual state without an explicit transition.
- Source and notification adapters protect credentials, restrict egress, redact diagnostics, and reject untrusted destinations and oversized/malicious input.
- Replicated automation inputs produce at most one owned action and cannot bypass action permissions.
- AI and retrieved content cannot expand tool grants, modify authoritative facts silently, expose secrets, or dispatch material actions without the ordinary approval path.

## Current peer-foundation evidence

On Linux the preview node has no DPAPI equivalent. Its P-256 identity key is a PKCS#8 file `identity.p256` in a 0700 data directory, created atomically at 0600 (temp file then no-overwrite rename), and the node refuses to load it if it is group/other accessible, a symlink, or owned by another user. This protects against other local users, not against root or the same user; keyring/TPM-backed storage is deferred. Linux evidence is an Ubuntu 24.04 container only.

The peer libraries now validate P-256 SPKI structure and SHA-256 fingerprints, bind bilateral fresh-pairing and reconnect proofs to both identities and the full session/challenge transcript, enforce fixed P1363 signatures and expiry, durably reject replay across restart, negotiate explicit compatibility, and persist trust/revocation and immutable origin records in SQLite WAL. Fresh pairing requires an initiating user action plus explicit approval on the contacted peer; pairing IDs are identity-bound and idempotent, and neither side stores trust before proof and completed confirmation. Origin replication verifies source trust, content hashes, signatures, bounds, conflicts, and durable cursors. Inbound/outbound transport fails closed on unsafe endpoints and requires protocol 1.1 bilateral proof. The administration surface is loopback-only with CSRF-protected mutations. The Windows preview stores its private identity key under CurrentUser DPAPI, keeps reconnect locators separate from application identity, and signs collected system observations before persistence. Credential references are omitted from replication descriptors and capability declarations cannot carry credential/private-key fields. Observed-device assignment validates that the selected local adapter names the same entity and collector peer; reassignment history is retained, adapter credential references remain local, and an IP/network address is rejected as strong identity. The Yamaha spike runs a fixed, non-interactive read-only PnP query with no user-provided script or credential input, a bounded timeout, safe reason codes, and no control path. Capability registration rejects prohibited ordinary network categories before persistence; defensive-security declarations require separate enablement, defensive privacy classification, evidence, confidence, and positive retention metadata. A real Windows loopback test proves separate-store reconnect and signed history convergence after offline collection. Real TLS/Tailscale deployment, non-Windows protected key stores, connection/rate limiting beyond per-session bounds, revocation during an established live session, and the remaining network-attack matrix remain unverified.
