# Project Documentation

This directory is the operational memory for Monitor App. Read it before changing the project and update it as part of the change, not as a later cleanup step.

## Authority and status

The product authority is [`Monitor App Design Specification.txt`](../Monitor%20App%20Design%20Specification.txt). It distinguishes confirmed user direction, assistant proposals, open exploration, and rejected directions. The documentation here may refine implementation, but it cannot silently change those statuses.

The user has added these confirmed execution requirements:

- Deliver the project for Android, Windows, Fedora, and Ubuntu devices.
- Implement functionality before advanced visuals and final UI polish.
- Delegate bulk implementation to lower agents.
- Test functionality with tests that execute and protect important behavior.
- Maintain durable documentation and a short-term `TODO.md`.
- Read both the documentation set and todo list before new changes.

## Document index

- [`ARCHITECTURE.md`](ARCHITECTURE.md) describes components, boundaries, data flow, and the provisional delivery architecture.
- [`DEVELOPMENT.md`](DEVELOPMENT.md) defines the implementation workflow and platform obligations.
- [`TESTING.md`](TESTING.md) defines meaningful test levels, commands to add, and release gates.
- [`SECURITY.md`](SECURITY.md) defines trust boundaries, enrollment safety, secrets, and action separation.
- [`DECISIONS.md`](DECISIONS.md) records confirmed constraints and working technical decisions without confusing them.
- [`CHANGELOG.md`](CHANGELOG.md) records completed user-visible and operational changes.
- [`RELEASES.md`](RELEASES.md) defines signed release manifests, protected signing configuration, update trust, and remaining platform release gates.
- [`../TODO.md`](../TODO.md) contains only active and near-term work.
- [`../AGENTS.md`](../AGENTS.md) contains mandatory repository instructions.

## Documentation rule

A change is incomplete if the affected documentation is stale. Update only documents that the change actually affects, but always inspect the full set first.

## Current phase

The project is in functional-foundation implementation. Durable application enrollment, stream-bound agent authentication, controller-side metric ingestion/current-state reads, the first Windows collector path, a bounded agent telemetry spool, the Android functional alpha, update-manifest verification, and their executable tests now exist. The next work is bounded historical reconstruction, Linux and Android device verification, Headscale integration, and native packaging before visual exploration.
