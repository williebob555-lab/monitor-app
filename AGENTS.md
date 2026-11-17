# Monitor App Repository Instructions

These instructions apply to every person or agent changing this repository.

## Required preflight

Before making any change, read these files in order:

1. `Monitor App Design Specification.txt`
2. `docs/README.md`
3. `docs/ARCHITECTURE.md`
4. `docs/DEVELOPMENT.md`
5. `docs/TESTING.md`
6. `docs/SECURITY.md`
7. `docs/DECISIONS.md`
8. `docs/CHANGELOG.md`
9. `TODO.md`

Then inspect the current repository state and confirm that the planned work does not conflict with an existing task or uncommitted change. No application code, configuration, schema, packaging, or test change may begin before this preflight is complete.

## Product authority

`Monitor App Design Specification.txt` is the product authority. Its labels have distinct force:

- `CONFIRMED USER DIRECTION` is a requirement.
- `ASSISTANT PROPOSAL` is a working suggestion, not an approved requirement.
- `OPEN EXPLORATION` remains unresolved.
- `REJECTED OR DEPRIORITIZED` must not return as a primary direction without explicit user approval.

Do not silently promote a proposal or open question into a confirmed decision. Record working technical decisions in `docs/DECISIONS.md`.

## Delivery priority

Functionality comes before visual polish. Build and validate the operational path in this order:

1. Shared domain contracts and executable build system.
2. Controller health, storage, enrollment, and telemetry ingestion.
3. Windows, Fedora, and Ubuntu agents.
4. Android collection and pairing within platform limits.
5. Cross-platform installation and recovery.
6. Historical reconstruction and meaningful functional clients.
7. Advanced visuals, animation, and final UI polish.

Temporary interfaces may be plain, but they must be usable, truthful, and accessible enough to test the functionality they expose.

## Supported targets

The requested device targets are Android, Windows, Fedora, and Ubuntu. A feature is not cross-platform merely because shared code compiles. Document and test platform-specific behavior. Do not claim support for an operating system that has not passed its required test level in `docs/TESTING.md`.

## Implementation rules

- Keep Headscale network coordination separate from Monitor App telemetry, history, application identity, and action authorization.
- Prefer outbound agent connections. Do not expose public monitoring ports on endpoints by default.
- Treat read-only telemetry and operational actions as different permission domains.
- Preserve timestamps, source, unit, freshness, and quality with collected data.
- Keep the visual scene a projection of the domain model, never the source of truth.
- Avoid speculative frameworks and abstractions that do not serve a current todo item or recorded decision.
- Do not add advanced visual work while a prerequisite functional path in `TODO.md` is incomplete unless the user explicitly reprioritizes it.

## Test contract

Tests must execute and must protect behavior important to the app's operation. Follow `docs/TESTING.md`.

At minimum:

- Run the relevant tests before declaring work complete.
- Test real production code paths, not a duplicate implementation in the test.
- Do not use tautological assertions, unconditional passes, or mocks of the exact behavior under test.
- Integration tests must use the real protocol, persistence adapter, or process boundary named by the test.
- Every fixed defect needs a regression test when the behavior can be tested deterministically.
- If a required test cannot run in the current environment, report it as unverified. Do not describe it as passing.

## Documentation and todo contract

Every change must leave the documentation set accurate.

- Update `TODO.md` when starting, completing, splitting, or discovering short-term work.
- Update `docs/CHANGELOG.md` with user-visible or operational changes.
- Update `docs/ARCHITECTURE.md` when components, boundaries, data flow, or deployment change.
- Update `docs/TESTING.md` when test commands, fixtures, coverage obligations, or platform gates change.
- Update `docs/SECURITY.md` when trust boundaries, credentials, permissions, or data collection change.
- Update `docs/DECISIONS.md` for any decision that constrains future implementation.
- Keep completed todo history concise; durable history belongs in the change log and decision log.

## Collaboration

The primary coordinator should delegate bulk implementation and test work to lower agents, as requested by the user. Each agent must work within an explicit scope, read this documentation set first, and report files changed and tests run. Agents share the same working tree, so avoid overlapping edits and preserve unrelated work.

## Completion report

Every completed task report must state:

- What changed.
- Which tests actually ran and their results.
- Which platform behavior was verified.
- Which documentation and todo entries were updated.
- Any unverified assumption, deferred task, or new risk.
