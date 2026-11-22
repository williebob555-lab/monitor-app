#Requires -Version 7.0
#Requires -RunAsAdministrator
<#
Per-machine installer for the Monitor App preview zip (node service + desktop app). UNTESTED on Windows: written
for review; see docs/TESTING.md. Run from the extracted zip root (it contains node\, desktop\ and the scripts):
  pwsh .\Install-MonitorApp.ps1 [-PeerBind <tailnet address>]

Installs the node as the MonitorApp service (via install-service.ps1), copies the desktop app to
%ProgramFiles%\MonitorApp\Desktop and adds a Start menu shortcut for all users. Re-running over an existing
install replaces the binaries and KEEPS the node identity and data. The service is started after install.
On Windows the node only downloads and verifies updates (preview); install a newer build by re-running this script
from the new zip. Applying updates in place is a TODO.
#>
[CmdletBinding()]
param(
    [string] $InstallDirectory = (Join-Path $env:ProgramFiles 'MonitorApp'),
    [string] $DataDirectory = (Join-Path $env:ProgramData 'MonitorApp'),
    [string] $PeerBind,
    [int] $PeerPort = 55431,
    [int] $AdminPort = 55432,
    [switch] $NoStart
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$nodeSource = Join-Path $here 'node'
$desktopSource = Join-Path $here 'desktop'
foreach ($required in @((Join-Path $nodeSource 'MonitorApp.Peer.Node.exe'), (Join-Path $desktopSource 'MonitorApp.Client.Desktop.exe'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Missing $required. Run this script from the extracted preview zip." }
}

if (Get-Service -Name 'MonitorApp' -ErrorAction SilentlyContinue) {
    & (Join-Path $here 'uninstall-service.ps1') -InstallDirectory $InstallDirectory -DataDirectory $DataDirectory
}

$installArguments = @{
    PublishDirectory = $nodeSource
    InstallDirectory = $InstallDirectory
    DataDirectory    = $DataDirectory
    PeerPort         = $PeerPort
    AdminPort        = $AdminPort
}
if ($PeerBind) { $installArguments.PeerBind = $PeerBind }
& (Join-Path $here 'install-service.ps1') @installArguments

$desktopTarget = Join-Path $InstallDirectory 'Desktop'
New-Item -ItemType Directory -Force -Path $desktopTarget | Out-Null
Copy-Item -Path (Join-Path $desktopSource '*') -Destination $desktopTarget -Recurse -Force
# install-service.ps1 restricted the install directory ACL; let all users read and run the desktop app.
& icacls.exe $desktopTarget /grant:r '*S-1-5-32-545:(OI)(CI)RX' | Out-Null

$shortcutDirectory = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path $shortcutDirectory 'Monitor App.lnk'))
$shortcut.TargetPath = Join-Path $desktopTarget 'MonitorApp.Client.Desktop.exe'
$shortcut.WorkingDirectory = $desktopTarget
$shortcut.Save()

if (-not $NoStart) { Start-Service -Name 'MonitorApp' }
Write-Host "Monitor App installed. Open 'Monitor App' from the Start menu (admin page: http://127.0.0.1:$AdminPort/)."
