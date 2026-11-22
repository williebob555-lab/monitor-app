#Requires -Version 7.0
#Requires -RunAsAdministrator
<#
Removes the Monitor App Windows service. UNTESTED on Windows. By default the node identity and data
in %ProgramData%\MonitorApp are KEPT; pass -Purge to delete them (the identity cannot be recovered).
#>
[CmdletBinding()]
param(
    [string] $InstallDirectory = (Join-Path $env:ProgramFiles 'MonitorApp'),
    [string] $DataDirectory = (Join-Path $env:ProgramData 'MonitorApp'),
    [switch] $Purge
)

$ErrorActionPreference = 'Stop'
$name = 'MonitorApp'
$service = Get-Service -Name $name -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $name -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    & sc.exe delete $name | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed ($LASTEXITCODE)" }
}
if (Test-Path $InstallDirectory) { Remove-Item -Recurse -Force $InstallDirectory }
if ($Purge) {
    if (Test-Path $DataDirectory) { Remove-Item -Recurse -Force $DataDirectory }
    Write-Host "Purged $DataDirectory."
}
else {
    Write-Host "Kept identity and data in $DataDirectory (use -Purge to delete)."
}
