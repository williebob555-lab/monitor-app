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
if (-not (Test-Path -LiteralPath $controllerProject)) {
    throw 'Controller project is required before alpha artifacts can be published.'
}
if (-not (Test-Path -LiteralPath $agentProject)) {
    throw 'Agent service project is required before alpha artifacts can be published.'
}

function Publish-ZipArtifact {
    param(
        [Parameter(Mandatory)] [string] $Project,
        [Parameter(Mandatory)] [string] $Runtime,
        [Parameter(Mandatory)] [string] $ArtifactName
    )

    $stagingName = [System.IO.Path]::GetFileNameWithoutExtension($ArtifactName)
    $staging = Join-Path $resolvedOutput "staging/$stagingName"
    dotnet publish $Project `
        -c Release `
        -r $Runtime `
        --self-contained true `
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
    dotnet publish $Project `
        -c Release `
        -r $Runtime `
        --self-contained true `
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
Publish-TarGzArtifact `
    -Project $agentProject `
    -Runtime 'linux-x64' `
    -ArtifactName "monitorapp-agent-fedora-x64-$Version.tar.gz"
Publish-TarGzArtifact `
    -Project $agentProject `
    -Runtime 'linux-x64' `
    -ArtifactName "monitorapp-agent-ubuntu-x64-$Version.tar.gz"

Write-Host "Created alpha artifacts in $resolvedOutput"
