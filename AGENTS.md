# Monitor App Repository Instructions

These instructions apply to every person or agent changing this repository.

## Required preflight

Use a risk-based preflight. Do not reread unchanged documents already reviewed in the current task.

Always, before editing:

1. Read `docs/README.md` and `TODO.md`.
2. Inspect `git status --short --branch` and the relevant files for overlapping work.
3. Search `Monitor App Design Specification.txt` and `docs/DECISIONS.md` for the requirement or decision governing the task.
4. Read the relevant sections of `docs/ARCHITECTURE.md`, `docs/DEVELOPMENT.md`, `docs/TESTING.md`, and `docs/SECURITY.md` based on the files and boundaries affected.

Read the complete product specification and complete documentation set only when the change is product-wide, changes architecture or trust boundaries, changes platform/release claims, introduces a new domain model, or cannot be safely scoped by search. A narrow documentation correction may begin after the relevant sections and repository state are understood. No application code, configuration, schema, packaging, or test change may begin before its applicable preflight is complete.

Keep preflight output compact. Do not print entire documents merely to prove they were read; use targeted ranges and searches, and summarize conflicts or constraints that affect the work.

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
2. Peer identity, bilateral pairing, local storage, and local query.
3. Direct reconnection, origin-owned history replication, and convergence.
4. Windows, Fedora, Ubuntu, and Android peer services within platform limits.
5. Observed-device adapters, explicit collector ownership, and LAN topology.
6. Cross-platform installation, recovery, and automated updates.
7. Historical reconstruction and meaningful functional clients.
8. Advanced visuals, animation, and final UI polish.

Temporary interfaces may be plain, but they must be usable, truthful, and accessible enough to test the functionality they expose.

## Supported targets

The requested device targets are Android, Windows, Fedora, and Ubuntu. A feature is not cross-platform merely because shared code compiles. Document and test platform-specific behavior. Do not claim support for an operating system that has not passed its required test level in `docs/TESTING.md`.

## Implementation rules

- Treat Tailscale as user-managed reachability only; Monitor App identity must not trust an IP address or tailnet membership alone.
- Build equal peers, not a controller/agent topology. Bind listeners only to deliberately configured private interfaces; never expose public monitoring ports by default.
- Preserve origin ownership and idempotent per-origin replication; no global consensus is required for origin-owned streams.
- Keep adapter credentials local, collector ownership explicit, and observed-device identity independent of the current collection host.
- Enforce the aggregate-without-communication-identities network-data boundary and the separate opt-in boundary for defensive security modules.
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

For documentation-only changes that do not alter commands, contracts, configuration, packaging, or executable behavior, `git diff --check` plus targeted link/consistency checks is sufficient. Do not run the full build and test suite by default. Run broader verification only when the documentation changes executable instructions or when requested.

Read-only review, diagnosis, status reporting, or any task that leaves no tracked repository diff requires no build, test, or verification command.

## Documentation and todo contract

Every change must leave the documentation set accurate.

- Update `TODO.md` when starting, completing, splitting, or discovering short-term work.
- Update `docs/CHANGELOG.md` with user-visible or operational changes.
- Update `docs/ARCHITECTURE.md` when components, boundaries, data flow, or deployment change.
- Update `docs/TESTING.md` when test commands, fixtures, coverage obligations, or platform gates change.
- Update `docs/SECURITY.md` when trust boundaries, credentials, permissions, or data collection change.
- Update `docs/DECISIONS.md` for any decision that constrains future implementation.
- Keep completed todo history concise; durable history belongs in the change log and decision log.

## Multi-agent collaboration (Claude and ChatGPT)

Two AI agents, Claude and ChatGPT, work on this repository in parallel. Before any change, read [`docs/COLLABORATION.md`](docs/COLLABORATION.md), the shared work board, and follow it:

- Claim the task in its claims table, on `main`, before creating a branch or editing files. Do not edit an area the other agent has claimed.
- Work on your own branch (`claude/...` or `chatgpt/<task-id>-<topic>`) and open a pull request. Never push to `main`, and never force-push, rebase, or commit to the other agent's branch.
- Use your own task-ID range (Claude T058-T299, ChatGPT T300-T499) and claim shared version numbers (`.proto` fields, schema versions) in your claim row.
- Edit shared logs (`TODO.md`, `docs/CHANGELOG.md`, `docs/DECISIONS.md`) only for your own task, append-only.
- Ask the other agent for changes through a handoff note in `docs/COLLABORATION.md`, and release your claim when the work merges.

## Delegation within one agent


The primary coordinator should delegate only substantial, clearly separable implementation or test work when requested by the user. Do not create subagents for preflight, small documentation edits, or work that the coordinator can complete directly at lower coordination cost. Each delegated agent uses the same risk-based preflight, works within an explicit scope, and reports files changed and tests run. Agents share the same working tree, so avoid overlapping edits and preserve unrelated work.

Keep progress communication proportional: one update at the start, at a meaningful blocker or scope change, and before a long-running verification step. Prefer one bounded wait over repeated polling and do not stream routine command output unless it changes the result.

## Completion report

Every completed task report must state:

- What changed.
- Which tests actually ran and their results.
- Which platform behavior was verified.
- Which documentation and todo entries were updated.
- Any unverified assumption, deferred task, or new risk.
