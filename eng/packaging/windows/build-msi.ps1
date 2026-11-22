<#
Builds the Monitor App MSI from a staged release folder (the same folder the zip is made from: node\, desktop\
and the install scripts). Requires the .NET SDK; installs the WiX v5 tool locally if missing.
  pwsh eng/packaging/windows/build-msi.ps1 -StageDirectory <stage> -Version 0.1.9 -OutputPath out\monitorapp-windows-x64-0.1.9-setup.msi
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $StageDirectory,
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "MSI versions must be x.y.z (got '$Version')." }
$wixVersion = '5.0.2'
$tools = Join-Path ([System.IO.Path]::GetTempPath()) 'monitorapp-wix'
$wix = Join-Path $tools 'wix.exe'
if (-not (Test-Path $wix)) {
    dotnet tool install wix --version $wixVersion --tool-path $tools | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Installing the WiX tool failed.' }
}
& $wix extension add -g "WixToolset.Util.wixext/$wixVersion" | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Adding the WiX util extension failed.' }

$source = Join-Path $PSScriptRoot 'MonitorApp.wxs'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent ([System.IO.Path]::GetFullPath($OutputPath))) | Out-Null
& $wix build $source -arch x64 -ext WixToolset.Util.wixext -d "Version=$Version" -d "StageDir=$((Resolve-Path $StageDirectory).Path)" -o $OutputPath | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'wix build failed.' }
Write-Host "Built $OutputPath"
