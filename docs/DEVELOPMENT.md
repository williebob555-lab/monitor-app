# Development Workflow

## Preflight

Follow `AGENTS.md` before every change. Read the design specification, documentation set, decision log, change log, and `TODO.md`. Inspect the working tree and avoid overlapping another agent's files.

## Work selection

Choose work from the current section of `TODO.md`. If the task is absent, add or refine it before implementation. A task should identify one observable result and the tests needed to prove it.

Functionality takes priority over advanced visuals. Do not begin final UI or animation work to avoid an unresolved functional or platform task.

## Change sequence

1. Identify the confirmed requirement or recorded working decision served by the change.
2. Update or claim the short-term todo item.
3. Add or adjust a failing test when practical.
4. Implement the smallest complete behavior.
5. Run the relevant local test level.
6. Run broader affected tests before handoff.
7. Update documentation, decisions, change log, and todo state.
8. Report verified and unverified platforms separately.

## Accepted toolchain

- .NET 10 LTS and C#.
- ASP.NET Core for the controller.
- Standard gRPC and Protocol Buffers for application contracts.
- A shared .NET worker agent with platform-specific service and collector boundaries.
- SQLite in WAL mode for the first persistence slice.
- Avalonia for the later shared client UI.
- Docker Compose for the first controller deployment.

Pin the .NET SDK in `global.json`, centralize package versions, and commit dependency lock files. The root build and verification entry points must remain usable from PowerShell on Windows and PowerShell or the documented shell wrapper on Linux.

Release and update work follows [`RELEASES.md`](RELEASES.md). Never place signing material in source control, weaken manifest verification for local convenience, or describe a self-contained archive as an installed-service package. A release is not verified merely because its workflow file parses; the protected workflow, produced artifacts, and platform install, upgrade, and rollback paths require their own evidence.

## Repository structure target

The repository should converge on responsibility boundaries similar to:

```text
src/
  MonitorApp.Contracts/       generated and shared protocol types
  MonitorApp.Domain/          platform-independent state and rules
  MonitorApp.Controller/      ASP.NET Core controller
  MonitorApp.Persistence/     SQLite schema, migrations, and repositories
  MonitorApp.Headscale/       isolated Headscale adapter
  MonitorApp.Agent.Core/      shared worker runtime and collector contracts
  MonitorApp.Agent.Service/   Windows Service and systemd host
  MonitorApp.Client.Core/     client state and history logic
  MonitorApp.App/             later shared Avalonia UI
  MonitorApp.Desktop/         Windows and Linux client entry point
  MonitorApp.Android/         Android entry point and platform services
contracts/                    versioned Protocol Buffer sources
deploy/                       Compose and native package definitions
tests/                        unit, integration, platform, packaging, and e2e tests
docs/                         durable project documentation
```

Do not create empty directories merely to resemble this target. Add a boundary when executable work needs it.

## Cross-platform discipline

- Keep platform-independent logic separate from operating-system collectors and installers.
- Make platform capabilities explicit in data, not hidden behind silent fallbacks.
- Use UTC for storage and protocol timestamps while preserving the source offset when useful.
- Do not rely on Unix path, signal, permission, or process assumptions in shared code.
- Do not rely on Windows registry or service behavior in shared code.
- Avoid shell-dependent test orchestration where the build tool can provide a portable command.
- Record the exact operating-system version used for platform verification.

## Dependencies

- Prefer maintained dependencies with clear licenses and a direct operational purpose.
- Pin reproducible versions for build and deployment inputs.
- Record a major dependency or framework choice in `DECISIONS.md`.
- Do not add a dependency solely to avoid writing a small, well-tested domain function.
- Do not copy credentials, personal device data, or generated secrets into the repository.

## Database and protocol changes

- Version persisted schemas and network contracts from the first executable version.
- Maintain backward compatibility across a documented rolling-upgrade window or fail with an explicit compatibility error.
- Add migration tests for every persisted-schema change.
- Add contract tests for every protocol change.
- Preserve original telemetry timestamps and identity through migrations.
- Run persistence integration tests against real file-backed SQLite with WAL enabled. Do not use the EF Core in-memory provider as evidence that production persistence works.
- Reserve removed Protobuf field numbers and reject unsupported protocol versions explicitly.

## Error behavior

Errors must identify the failed layer when possible: controller, Headscale, private network, agent, collector, integration, or monitored service. Avoid collapsing these into a generic offline state.

## Running the controller foundation

The controller requires three externally supplied settings:

- `MonitorApp__DatabasePath`: file-backed SQLite database path; defaults to `data/monitor-app.db`.
- `MonitorApp__SecretPepper`: base64 for at least 32 random bytes used only to derive enrollment-secret verifiers.
- `MonitorApp__AdminToken`: at least 32 UTF-8 bytes for the temporary owner enrollment boundary.

Run `dotnet run --project src/MonitorApp.Controller/MonitorApp.Controller.csproj` after setting those values through a local ignored secret mechanism. Never commit example production values. The health probe is `GET /health`. TLS termination and a real HTTP/2 gRPC process test remain deployment work; the current local process smoke test proves controller startup and health only.

## Handoff checklist

- Relevant todo updated.
- Production behavior implemented.
- Important failure paths tested.
- Test commands and results recorded in the handoff.
- Platform verification stated precisely.
- Affected documentation updated.
- New risks or follow-up work added to `TODO.md`.
