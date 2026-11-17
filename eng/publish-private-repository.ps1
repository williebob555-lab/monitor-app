[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern('^[A-Za-z0-9_.-]+$')]
    [string] $RepositoryName = 'monitor-app',

    [Parameter()]
    [ValidatePattern('^[A-Za-z0-9-]+$')]
    [string] $Owner
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string] $Description,

        [Parameter(Mandatory)]
        [scriptblock] $Action
    )

    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw 'Git is required.'
}
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'GitHub CLI (gh) is required.'
}
if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot 'MonitorApp.sln')) -or
    -not (Test-Path -LiteralPath (Join-Path $repositoryRoot 'AGENTS.md'))) {
    throw 'The script must remain under the Monitor App repository eng directory.'
}

Push-Location $repositoryRoot
try {
    $gitRootOutput = & git rev-parse --show-toplevel 2>$null
    $gitRoot = if ($null -eq $gitRootOutput) { '' } else { "$gitRootOutput".Trim() }
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitRoot) -or
        -not [System.IO.Path]::GetFullPath($gitRoot).Equals(
            [System.IO.Path]::GetFullPath($repositoryRoot),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'The current Git repository root does not match the Monitor App repository.'
    }

    $status = & git status --porcelain=v1 --untracked-files=all
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to inspect the Git working tree.'
    }
    if ($status) {
        throw 'Commit or remove every tracked and untracked change before publishing.'
    }

    $branchOutput = & git branch --show-current
    $branch = if ($null -eq $branchOutput) { '' } else { "$branchOutput".Trim() }
    if ($LASTEXITCODE -ne 0 -or $branch -ne 'main') {
        throw "Publishing requires the checked-out main branch; current branch is '$branch'."
    }

    & git rev-parse --verify HEAD *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Create at least one local commit before publishing.'
    }

    $origin = & git remote get-url origin 2>$null
    if ($LASTEXITCODE -eq 0 -or $origin) {
        throw "An origin remote already exists ('$origin'). Refusing to replace it."
    }
    $global:LASTEXITCODE = 0

    Invoke-Checked 'GitHub authentication check' { gh auth status -h github.com }

    if ([string]::IsNullOrWhiteSpace($Owner)) {
        $ownerOutput = & gh api user --jq .login
        $Owner = if ($null -eq $ownerOutput) { '' } else { "$ownerOutput".Trim() }
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($Owner)) {
            throw 'Unable to resolve the authenticated GitHub owner. Pass -Owner explicitly.'
        }
    }

    $repository = "$Owner/$RepositoryName"
    $existingVisibility = & gh repo view $repository --json visibility --jq .visibility 2>$null
    if ($LASTEXITCODE -eq 0) {
        throw "GitHub repository '$repository' already exists with visibility '$existingVisibility'. Refusing to overwrite it."
    }
    $global:LASTEXITCODE = 0

    Invoke-Checked "Create and push private repository '$repository'" {
        gh repo create $repository --private --source $repositoryRoot --remote origin --push
    }

    $visibilityOutput = & gh repo view $repository --json visibility --jq .visibility
    $visibility = if ($null -eq $visibilityOutput) { '' } else { "$visibilityOutput".Trim() }
    if ($LASTEXITCODE -ne 0 -or $visibility -ne 'PRIVATE') {
        throw "Repository creation returned unexpected visibility '$visibility'. Inspect GitHub immediately."
    }

    Write-Host "Published private repository: https://github.com/$repository"
    Write-Host 'Next: protect the release environment and configure signing values as documented in docs/RELEASES.md.'
    Write-Host 'Do not create a v0.1.0 tag until the release environment is configured and reviewed.'
}
finally {
    Pop-Location
}
