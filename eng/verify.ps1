[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot

function Invoke-VerificationStep {
    param(
        [Parameter(Mandatory)]
        [string] $Name,

        [Parameter(Mandatory)]
        [scriptblock] $Action
    )

    Write-Host "`n==> $Name"
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

Push-Location $repositoryRoot
try {
    Invoke-VerificationStep "Restore locked dependencies" {
        dotnet restore MonitorApp.sln --locked-mode
    }

    Invoke-VerificationStep "Build with warnings as errors" {
        dotnet build MonitorApp.sln -c Release --no-restore -warnaserror
    }

    Invoke-VerificationStep "Run all unit and contract tests" {
        dotnet test MonitorApp.sln `
            -c Release `
            --no-build `
            --filter "Category=Unit|Category=Contract"
    }

    Invoke-VerificationStep "Run real SQLite persistence integration tests" {
        dotnet test tests/MonitorApp.Persistence.Tests/MonitorApp.Persistence.Tests.csproj `
            -c Release `
            --no-build `
            --filter "Category=SQLite"
    }

    Invoke-VerificationStep "Run durable agent spool integration tests" {
        dotnet test tests/MonitorApp.Agent.Persistence.Tests/MonitorApp.Agent.Persistence.Tests.csproj `
            -c Release `
            --no-build `
            --filter "Category=SQLite"
    }

    Invoke-VerificationStep "Run peer SQLite persistence integration tests" {
        dotnet test tests/MonitorApp.Peer.Persistence.Tests/MonitorApp.Peer.Persistence.Tests.csproj `
            -c Release `
            --no-build `
            --filter "Category=SQLite"
    }

    Invoke-VerificationStep "Run peer pairing application tests" {
        dotnet test tests/MonitorApp.Peer.Application.Tests/MonitorApp.Peer.Application.Tests.csproj `
            -c Release `
            --no-build
    }

    Invoke-VerificationStep "Run peer replication integration tests" {
        dotnet test tests/MonitorApp.Peer.Replication.Tests/MonitorApp.Peer.Replication.Tests.csproj `
            -c Release `
            --no-build
    }

    Invoke-VerificationStep "Run peer service loopback gRPC tests" {
        dotnet test tests/MonitorApp.Peer.Service.Tests/MonitorApp.Peer.Service.Tests.csproj `
            -c Release `
            --no-build
    }

    Invoke-VerificationStep "Run controller enrollment and loopback gRPC telemetry tests" {
        dotnet test tests/MonitorApp.Controller.IntegrationTests/MonitorApp.Controller.IntegrationTests.csproj `
            -c Release `
            --no-build `
            --filter "Category=Controller"
    }

    if ($IsWindows) {
        Invoke-VerificationStep "Run Windows collector integration tests" {
            dotnet test tests/MonitorApp.Agent.Core.Tests/MonitorApp.Agent.Core.Tests.csproj `
                -c Release `
                --no-build `
                --filter "Category=Windows"
        }
    }
    else {
        Write-Warning "NOT RUN: Windows collector integration tests require Windows."
    }

    Invoke-VerificationStep "Verify formatting" {
        dotnet format MonitorApp.sln --verify-no-changes --no-restore
    }

    Write-Warning "NOT RUN: separate-process agent/controller, Headscale, and two-peer convergence suites; those projects do not exist yet."
    Write-Warning "NOT RUN: Android device/instrumentation, Windows service lifecycle, Fedora, and Ubuntu platform suites. Android core tests run in the solution; the workload-specific APK build is a separate documented command."

    Write-Host "`nAvailable functional-foundation verification passed."
}
finally {
    Pop-Location
}
