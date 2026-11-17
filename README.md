# Monitor App

Monitor App is a self-hosted, read-only monitoring alpha for Android, Windows, Fedora, and Ubuntu. The long-term product is a living schematic with complementary exploded device views, historical time travel, and deliberately playful interaction. This repository currently concentrates on the functional foundation: secure enrollment, truthful collection, durable delivery, current-state reads, and signed update metadata.

## What works today

- A .NET 10 controller with health, owner-gated enrollment, inventory, and current-metric endpoints.
- P-256 application enrollment and a fresh proof bound to each bidirectional gRPC agent stream. Headscale membership is not treated as application identity.
- File-backed SQLite WAL persistence for enrollment, device identity, security audit evidence, metric ingestion, idempotent retry, and current state.
- A shared Windows/Fedora/Ubuntu agent that collects host identity, CPU, memory, storage, network, and collector health into a bounded durable spool, then uploads through an authenticated outbound stream.
- A plain foreground Android alpha for battery, charging, network/VPN state, freshness, controller health, and Android Keystore-backed enrollment.
- Signed update-manifest verification, immutable-tag GitHub release automation, and self-contained alpha archive creation.
- One verification command covering unit, contract, real SQLite, real loopback HTTP/2 gRPC, and Windows-local collector tests.

This is not a supported release yet. Headscale automation, historical reconstruction, native service installers, production TLS deployment, key rotation, Fedora/Ubuntu platform-local tests, Android emulator/device tests, and the final living-schematic UI remain incomplete. The Android APK built during development uses the Android debug certificate.

## Architecture

```text
Windows / Fedora / Ubuntu agent -- outbound authenticated gRPC --> controller --> SQLite WAL
Android foreground alpha       -- HTTPS health + enrollment ----/
owner diagnostic client        -- authenticated HTTPS ----------> inventory/current metrics

Headscale (later) coordinates the private network but does not own
Monitor App telemetry, history, device identity, or authorization.
```

The design authority is [Monitor App Design Specification.txt](Monitor%20App%20Design%20Specification.txt). Start with [the documentation index](docs/README.md), [architecture](docs/ARCHITECTURE.md), [testing](docs/TESTING.md), [security](docs/SECURITY.md), [release trust](docs/RELEASES.md), and [current tasks](TODO.md) before changing the project.

## Prerequisites

- .NET SDK `10.0.400` (the repository pin allows later patches in that feature band).
- PowerShell 7 for the repository scripts.
- Git and GitHub CLI (`gh`) for private-repository publication.
- To rebuild Android: the .NET Android workload `36.1.69`, Android API 36, and a supported JDK 21.

## Verify the repository

From the repository root:

```powershell
pwsh -NoProfile -File .\eng\verify.ps1
```

The script reports every unavailable suite rather than silently treating it as passing. See [docs/TESTING.md](docs/TESTING.md) for the exact coverage and platform gates.

## Run the controller locally

The bootstrap administrator token and enrollment-verifier pepper are development inputs, not final user authentication. The following creates ephemeral values for the current terminal only. Do not copy them into source control.

```powershell
$env:MonitorApp__DatabasePath = Join-Path $PWD 'local-data\monitor-app.db'
$env:MonitorApp__SecretPepper = [Convert]::ToBase64String(
    [Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:MonitorApp__AdminToken = [Convert]::ToBase64String(
    [Security.Cryptography.RandomNumberGenerator]::GetBytes(32))

# Windows development certificate; use explicitly configured production TLS elsewhere.
dotnet dev-certs https --trust
$env:ASPNETCORE_URLS = 'https://localhost:7443'
dotnet run --project .\src\MonitorApp.Controller\MonitorApp.Controller.csproj
```

In another PowerShell terminal, reuse the same administrator-token value and read the diagnostic endpoints:

```powershell
$headers = @{ 'x-monitor-admin-token' = '<the MonitorApp__AdminToken value from the controller terminal>' }
Invoke-RestMethod 'https://localhost:7443/health'
Invoke-RestMethod 'https://localhost:7443/api/inventory' -Headers $headers
Invoke-RestMethod 'https://localhost:7443/api/metrics/current' -Headers $headers
```

The controller has a real enrollment gRPC boundary, but this alpha does not yet include an owner-facing enrollment CLI. The Android app can complete enrollment after an authorized owner client supplies the short-lived enrollment id, secret, and challenge. Desktop enrollment is exercised through production boundaries in the integration suite; do not handcraft or persist pairing secrets as a workaround.

## Run an already-enrolled desktop agent

After enrollment, configure the returned agent id and the same endpoint-owned P-256 private key that created the enrollment proof:

```powershell
$env:MONITORAPP_AGENT_ID = '<enrolled agent id>'
$env:MONITORAPP_CONTROLLER_URL = 'https://localhost:7443'
$env:MONITORAPP_DEVICE_KEY_PATH = 'C:\secure\monitor-app-device-private.pem'
$env:MONITORAPP_SPOOL_PATH = Join-Path $PWD 'local-data\agent-spool.db'
$env:MONITORAPP_COLLECTION_INTERVAL_SECONDS = '15'

dotnet run --project .\src\MonitorApp.Agent.Service\MonitorApp.Agent.Service.csproj -- --once
```

`--once` collects, attempts one authenticated upload, reports the pending-spool count, and exits. Omit it for the current foreground loop. Native Windows Service and `systemd` installation are not implemented yet. Protect the PEM and spool with operating-system permissions; packaged platform key storage is still a release gate.

## Android alpha

The Android project is intentionally outside `MonitorApp.sln` so ordinary Windows/Linux verification does not require a mobile toolchain. Build it separately:

```powershell
dotnet restore .\src\MonitorApp.Android\MonitorApp.Android.csproj --locked-mode `
    -p:AndroidSdkDirectory="$env:ANDROID_SDK_ROOT" `
    -p:JavaSdkDirectory="$env:JAVA_HOME"
dotnet build .\src\MonitorApp.Android\MonitorApp.Android.csproj -c Release --no-restore -warnaserror `
    -p:AndroidSdkDirectory="$env:ANDROID_SDK_ROOT" `
    -p:JavaSdkDirectory="$env:JAVA_HOME"
```

Compilation and signature validation do not prove installation, Android Keystore behavior, lifecycle, VPN behavior, or controller connectivity. Those require emulator and physical-device tests before Android support is claimed.

## Build alpha archives

This produces self-contained controller and Windows/Fedora/Ubuntu agent archives. They are portable alpha files, not native installers or managed services.

```powershell
pwsh -NoProfile -File .\eng\release\Publish-AlphaArtifacts.ps1 `
    -Version 0.1.0 `
    -OutputDirectory artifacts\alpha-0.1.0
```

The `artifacts/` directory, APKs, signing stores, and private-key patterns are ignored by Git. The protected GitHub release workflow creates signed manifests and checksums; configure it according to [docs/RELEASES.md](docs/RELEASES.md).

## Publish the private GitHub repository

Commit the reviewed source first. Then authenticate GitHub CLI and run the guarded helper:

```powershell
gh auth login -h github.com -w
pwsh -NoProfile -File .\eng\publish-private-repository.ps1 -RepositoryName monitor-app
```

The helper refuses a dirty tree, a branch other than `main`, any existing `origin`, an existing repository with the requested owner/name, or a result that is not private. It does not create release-signing keys or upload local ignored artifacts.

## Current delivery boundary

The functional alpha proves secure application enrollment, stream authentication, durable collection/delivery, current-state reads, Android compilation, and release-manifest verification. The next operational priorities are listed in [TODO.md](TODO.md): Headscale integration, platform-local packaging/lifecycle tests, historical range/reconstruction, production TLS deployment, backup/restore, and a usable owner client. Advanced visuals come afterward.
