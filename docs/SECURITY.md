# Security and Privacy Model

## Scope

Monitor App observes sensitive device and service state and may eventually execute administrative actions. A self-hosted deployment and private Headscale network reduce exposure but do not replace application authentication, authorization, encryption, or audit.

## Trust boundaries

### User to experience client

The client must authenticate the user before showing sensitive infrastructure data or creating enrollment material. The initial identity method remains a working decision.

### Experience client to controller

Use an authenticated application protocol over TLS. Do not give clients direct database credentials.

### Agent to controller

Private-network membership is not sufficient identity. The agent proves possession of a Monitor App device private key established during enrollment and bound to its expected device identity. The private key is generated and retained on the device and is never transmitted to the controller.

### Controller to Headscale

The Headscale adapter receives only the authority needed to create, inspect, expire, or revoke registration material and nodes required by enrollment. Headscale remains a network coordination dependency, not the Monitor App authorization authority.

### Agent to local operating system

Collectors run with least privilege. Platform-specific privileged collection should be isolated and optional where practical. Permission denial produces an unavailable capability with evidence, not a fabricated value.

The agent's bounded offline spool contains sensitive telemetry even though it contains no enrollment private key or reusable bearer credential. Native installers must create its data directory with operating-system-appropriate access controls before platform support is claimed. Reaching the configured spool limit preserves existing unacknowledged evidence and stops accepting new observations rather than silently deleting history.

### Controller to persistence

Persistence contains infrastructure history, identity, and audit data. Access is limited to controller services, backups are protected, and schema migrations are authenticated operational actions.

## Enrollment requirements

- Enrollment is short lived and single use.
- Monitor App stores a verifier or salted token hash rather than a reusable plaintext application token.
- Each enrollment returns a 32-byte cryptographically random proof challenge and accepts only the currently supported ECDSA P-256 with SHA-256 proof format.
- The device signs the domain-separated enrollment payload with its locally generated key and sends the public key in SubjectPublicKeyInfo form plus a fixed-width 64-byte IEEE P1363 signature.
- Successful completion stores the public key and SHA-256 fingerprint and returns stable node and agent ids; it does not issue or echo a reusable raw agent credential.
- Subsequent authentication uses a short-lived, single-use 32-byte device challenge bound to agent and challenge ids. A captured signature must not authenticate a different identity or challenge.
- Secrets do not appear in URLs when a safer code exchange is possible.
- Enrollment secrets, proof challenges, and signatures are redacted from safe errors, logs, crash reports, process arguments, and diagnostic exports.
- The transaction records intended platform and role.
- Completion binds application identity to the expected network node when that evidence is available.
- Expiration and cancellation revoke unused material.
- Reuse, identity mismatch, and unsupported version fail closed.
- Pairing success includes an end-to-end telemetry health check.

Implemented foundation behavior uses a required controller pepper plus a per-enrollment random salt to create an HMAC-SHA-256 verifier; the raw enrollment secret is never persisted. The controller fails startup when the pepper or administrator token is absent or shorter than 32 bytes. Create, status, and cancel are owner operations gated by the administrator token. Completion compares submitted platform and display name to persisted enrollment intent, validates the exact `prime256v1` curve, and atomically consumes one pending record. Device challenge creation is rate limited, capped at five live challenges per agent, and proof verification atomically consumes the challenge.

The Android alpha generates its P-256 private key inside Android Keystore with signing/verification purpose and SHA-256 only; application code exports only the SubjectPublicKeyInfo public key. Short-lived enrollment secret and proof-challenge inputs exist only in memory and visible form fields, are never written to preferences, and are cleared after success or failure. Private application preferences retain only the non-secret agent id, node id, public-key fingerprint, and completion timestamp. Android device behavior still requires emulator and physical-device validation, and the locally generated development-signed APK is not a production-trust artifact.

The administrator token is a functional bootstrap boundary, not the final user identity system. A deployable alpha still needs secure token provisioning and rotation. Each `AgentSession` proves the enrolled P-256 key inside that exact stream using a fresh, short-lived, atomically consumed challenge bound to agent, session, and challenge ids. A captured proof cannot authenticate another stream and no reusable agent bearer credential is issued. Production endpoints still require TLS; plaintext HTTP/2 is accepted only for loopback development and tests.

## Transport

- Monitor App uses TLS even over the WireGuard-based private network.
- Server identity is verified; disabling verification is not a supported convenience path.
- Protocol messages have size limits and validated schemas.
- Live streams use bounded buffers and backpressure.
- Replay-sensitive operations include sequence or nonce protection appropriate to the protocol.
- Enrollment and device challenges are generated with a cryptographically secure random source, expire, and are atomically consumed once; the service also requires bounded creation and verification rates.

## Secrets

- No secrets in source control.
- Local development secrets use ignored files or an approved secret store.
- Production secrets use operating-system or deployment secret facilities.
- Credential rotation and device revocation are required before release.
- Backups containing secrets or sensitive history are encrypted and have a documented recovery-key process.
- Release private keys exist only in an approved secret store and the protected GitHub `release` environment. Workflows fail when signing values are missing or when configured public and private keys do not match.
- Common signing-store, Android-keystore, private-key, generated APK/AAB, and release-staging filenames are ignored by Git. Ignore rules are defense in depth, not permission to keep secret files in the repository directory.
- Android production releases require a protected signing key, reproducible release job, certificate continuity, and tested rotation/recovery. The current development signing key must never be treated as release identity or distributed as an update trust anchor.

## Telemetry privacy

Collect the minimum data needed for an enabled feature. The administration experience must list categories, sources, and retention.

Avoid collecting these by default:

- File names and paths beyond explicitly configured integration scope.
- Process command-line arguments.
- Environment variables.
- Message or document contents.
- Authentication tokens.
- Unrelated network destinations or packet contents.

Diagnostic exports must be inspectable and redactable before sharing.

## Action safety

The functional foundation is read-only. When actions are introduced:

- Read, configuration, and control permissions remain separate.
- Decorative or exploratory gestures never dispatch actions.
- Requests identify actor, target, operation, scope, expiration, and idempotency key.
- Material operations show a preview and require confirmation.
- Expired commands cannot execute after reconnect.
- Every acknowledgement and result is audited.
- Historical mode cannot silently target the live system.

## Logging and audit

Operational logs should support diagnosis without leaking secrets. Security audit records should cover:

- Enrollment creation, completion, expiration, cancellation, and reuse attempt.
- Device revocation and credential rotation.
- Authentication failure and authorization denial.
- Policy or retention changes.
- Future action requests, acknowledgements, and results.

Audit timestamps use UTC and preserve actor and target identity. Logs and audit data have separate retention needs.

## Dependency and update safety

- Pin a tested Headscale version for supported deployments.
- Verify installer and update signatures.
- Record major security-sensitive dependencies in `DECISIONS.md`.
- Scan dependencies in automated verification once the stack is selected.
- Define a rollback path that does not silently downgrade persisted data.
- Pin third-party GitHub Actions to reviewed immutable commit hashes.
- Sign canonical release-manifest bytes with ECDSA P-256/SHA-256 and a stable key id. Provision trusted public keys through an authenticated installer or prior trusted release, never by trusting a key downloaded beside the manifest.
- Reject an update before installation when its signature, channel, version direction, compatibility floor, target component, platform, architecture, byte size, or SHA-256 does not match the signed manifest.
- Never make unsigned fallback, disabled verification, or automatic overwrite of an existing release a recovery path.

## Threats requiring explicit tests

- Stolen or replayed enrollment material.
- Replayed enrollment or device-proof signatures, including use with a different agent, enrollment, or challenge id.
- Malformed, wrong-curve, or substituted device public keys.
- Tailnet member without Monitor App identity.
- Compromised or obsolete agent version.
- Malformed or oversized telemetry.
- Timestamp manipulation.
- Duplicate or reordered messages.
- Secret leakage through logs or installers.
- Unauthorized future action dispatch.
- Tampered update package.
- Forged, wrong-key, replayed-channel, downgraded, or incorrectly targeted update manifest.
- Restored backup with missing or incorrect keys.
