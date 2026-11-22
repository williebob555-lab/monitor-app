[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$resolvedOutput = [System.IO.Path]::GetFullPath(
    [System.IO.Path]::Combine($repositoryRoot, $OutputDirectory))
$repositoryPrefix = $repositoryRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if (-not $resolvedOutput.StartsWith($repositoryPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release output directory must be inside the repository.'
}

if (Test-Path -LiteralPath $resolvedOutput) {
    if (Get-ChildItem -LiteralPath $resolvedOutput -Force | Select-Object -First 1) {
        throw 'Release output directory must be empty.'
    }
}
else {
    New-Item -ItemType Directory -Path $resolvedOutput | Out-Null
}

$controllerProject = Join-Path $repositoryRoot 'src/MonitorApp.Controller/MonitorApp.Controller.csproj'
$agentProject = Join-Path $repositoryRoot 'src/MonitorApp.Agent.Service/MonitorApp.Agent.Service.csproj'
$peerProject = Join-Path $repositoryRoot 'src/MonitorApp.Peer.Node/MonitorApp.Peer.Node.csproj'
if (-not (Test-Path -LiteralPath $controllerProject)) {
    throw 'Controller project is required before alpha artifacts can be published.'
}
if (-not (Test-Path -LiteralPath $agentProject)) {
    throw 'Agent service project is required before alpha artifacts can be published.'
}
if (-not (Test-Path -LiteralPath $peerProject)) {
    throw 'Windows peer preview project is required before alpha artifacts can be published.'
}

$restoredRuntimeAssets = @{}

function Restore-RuntimeAssets {
    param(
        [Parameter(Mandatory)] [string] $Project,
        [Parameter(Mandatory)] [string] $Runtime
    )

    $restoreKey = "$Project|$Runtime"
    if ($restoredRuntimeAssets.ContainsKey($restoreKey)) {
        return
    }

    # A runtime-specific restore is required for self-contained publishing, but
    # it must not rewrite the repository's platform-neutral packages.lock.json.
    # NuGet rejects disabling locks while project lock files exist, so direct
    # runtime restore output to an ignored release-staging lock instead.
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($Project)
    $runtimeLockDirectory = Join-Path $resolvedOutput "runtime-locks/$projectName-$Runtime"
    New-Item -ItemType Directory -Path $runtimeLockDirectory -Force | Out-Null
    $runtimeLockPath = Join-Path $runtimeLockDirectory 'packages.lock.json'
    dotnet restore $Project `
        -r $Runtime `
        --force-evaluate `
        -p:NuGetLockFilePath=$runtimeLockPath `
        -p:RestoreLockedMode=false
    if ($LASTEXITCODE -ne 0) {
        throw "Restoring runtime assets for $Runtime failed."
    }

    $restoredRuntimeAssets[$restoreKey] = $true
}

function Publish-ZipArtifact {
    param(
        [Parameter(Mandatory)] [string] $Project,
        [Parameter(Mandatory)] [string] $Runtime,
        [Parameter(Mandatory)] [string] $ArtifactName
    )

    $stagingName = [System.IO.Path]::GetFileNameWithoutExtension($ArtifactName)
    $staging = Join-Path $resolvedOutput "staging/$stagingName"
    Restore-RuntimeAssets -Project $Project -Runtime $Runtime
    dotnet publish $Project `
        -c Release `
        -r $Runtime `
        --self-contained true `
        --no-restore `
        -p:Version=$Version `
        -p:PublishSingleFile=true `
        -p:DebugType=None `
        -o $staging
    if ($LASTEXITCODE -ne 0) {
        throw "Publishing $ArtifactName failed."
    }

    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath (Join-Path $resolvedOutput $ArtifactName)
}

function Publish-TarGzArtifact {
    param(
        [Parameter(Mandatory)] [string] $Project,
        [Parameter(Mandatory)] [string] $Runtime,
        [Parameter(Mandatory)] [string] $ArtifactName
    )

    $stagingName = $ArtifactName -replace '\.tar\.gz$', ''
    $staging = Join-Path $resolvedOutput "staging/$stagingName"
    Restore-RuntimeAssets -Project $Project -Runtime $Runtime
    dotnet publish $Project `
        -c Release `
        -r $Runtime `
        --self-contained true `
        --no-restore `
        -p:Version=$Version `
        -p:PublishSingleFile=true `
        -p:DebugType=None `
        -o $staging
    if ($LASTEXITCODE -ne 0) {
        throw "Publishing $ArtifactName failed."
    }

    & tar -C $staging -czf (Join-Path $resolvedOutput $ArtifactName) .
    if ($LASTEXITCODE -ne 0) {
        throw "Archiving $ArtifactName failed."
    }
}

Publish-TarGzArtifact `
    -Project $controllerProject `
    -Runtime 'linux-x64' `
    -ArtifactName "monitorapp-controller-linux-x64-$Version.tar.gz"
Publish-ZipArtifact `
    -Project $agentProject `
    -Runtime 'win-x64' `
    -ArtifactName "monitorapp-agent-windows-x64-$Version.zip"
Publish-ZipArtifact `
    -Project $peerProject `
    -Runtime 'win-x64' `
    -ArtifactName "monitorapp-peer-windows-x64-$Version.zip"
Publish-TarGzArtifact `
    -Project $agentProject `
    -Runtime 'linux-x64' `
    -ArtifactName "monitorapp-agent-fedora-x64-$Version.tar.gz"
Publish-TarGzArtifact `
    -Project $agentProject `
    -Runtime 'linux-x64' `
    -ArtifactName "monitorapp-agent-ubuntu-x64-$Version.tar.gz"

Write-Host "Created alpha artifacts in $resolvedOutput"
