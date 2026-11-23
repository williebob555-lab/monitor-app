#Requires -RunAsAdministrator
<#
Per-machine installer for the Monitor App preview zip (node service + desktop app). UNTESTED on Windows: written
for review; see docs/TESTING.md. Run from the extracted zip root (it contains node\, desktop\ and the scripts):
  pwsh .\Install-MonitorApp.ps1 [-PeerBind <tailnet address>]

Installs the node as the MonitorApp service (via install-service.ps1), copies the desktop app to
%ProgramFiles%\MonitorApp\current\desktop and adds a Start menu shortcut for all users. Re-running over an existing
install replaces the binaries and KEEPS the node identity and data. The service is started after install.
Layout is side by side (versions\<version>\node and \desktop, with 'current' pointing at the active one) and a
SYSTEM-run on-demand scheduled task, MonitorAppUpdate, lets the node install an approved, signed update in place
(stop, switch, start, health check, roll back on failure). The node still waits for your approval ("Ask me first").
Re-running this script from a newer zip also works and replaces the whole install, keeping identity and data.
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
    DesktopDirectory = $desktopSource
}
if ($PeerBind) { $installArguments.PeerBind = $PeerBind }
& (Join-Path $here 'install-service.ps1') @installArguments

# The desktop app lives in the active version directory and is reached through 'current', so updates replace it too.
# install-service.ps1 restricted the install directory ACL; let all users traverse and run the desktop app.
$desktopTarget = Join-Path $InstallDirectory 'current\desktop'
& icacls.exe $InstallDirectory '/grant:r' '*S-1-5-32-545:(RX)' | Out-Null
& icacls.exe (Join-Path $InstallDirectory 'versions') '/grant:r' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null

$shortcutDirectory = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path $shortcutDirectory 'Monitor App.lnk'))
$shortcut.TargetPath = Join-Path $desktopTarget 'MonitorApp.Client.Desktop.exe'
$shortcut.WorkingDirectory = $desktopTarget
$shortcut.Save()

if (-not $NoStart) { Start-Service -Name 'MonitorApp' }
Write-Host "Monitor App installed. Open 'Monitor App' from the Start menu (admin page: http://127.0.0.1:$AdminPort/)."
