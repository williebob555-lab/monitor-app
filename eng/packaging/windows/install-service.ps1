#Requires -RunAsAdministrator
<#
Installs the Monitor App peer node as an always-on Windows service running as the virtual account
NT SERVICE\MonitorApp (no password, least privilege). UNTESTED on Windows: written for review; see
docs/TESTING.md. Usage:
  pwsh eng/packaging/windows/install-service.ps1 -PublishDirectory <dir containing MonitorApp.Peer.Node.exe>
  (publish with: dotnet publish src/MonitorApp.Peer.Node -c Release -r win-x64 --self-contained -o <dir>)

Identity protection: the node's identity key is wrapped with DPAPI *current-user* scope, so it is
bound to NT SERVICE\MonitorApp. Only that account can decrypt it; changing the service account makes
the key unreadable (decryption fails; startup behaviour in that case is not verified on Windows).
The data directory ACL is restricted to that account, SYSTEM and Administrators.
Layout (side-by-side, so the node can update itself in place):
  <InstallDirectory>\versions\<version>\node   (+ \desktop when -DesktopDirectory is given)
  <InstallDirectory>\current                    directory symlink to the active version; the service runs from here
  <InstallDirectory>\update-helper.json         marker: the detached update helper is registered
The scheduled task MonitorAppUpdate (SYSTEM, no triggers, on-demand) runs the node binary with --apply-update to
install an approved, signed, staged update. Only SYSTEM and Administrators can change the task or the binaries; the
service account can merely start the task (read/execute on the task). Without that task the node never offers approval.
The peer listener stays on 127.0.0.1 unless -PeerBind is given an explicit address. The admin page is
always loopback-only.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PublishDirectory,
    [string] $InstallDirectory = (Join-Path $env:ProgramFiles 'MonitorApp'),
    [string] $DataDirectory = (Join-Path $env:ProgramData 'MonitorApp'),
    [string] $PeerBind,
    [int] $PeerPort = 55431,
    [int] $AdminPort = 55432,
    [string] $DesktopDirectory,
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$name = 'MonitorApp'
$account = "NT SERVICE\$name"
$taskName = 'MonitorAppUpdate'
$exe = Join-Path $InstallDirectory 'current\node\MonitorApp.Peer.Node.exe'

if (-not (Test-Path (Join-Path $PublishDirectory 'MonitorApp.Peer.Node.exe'))) {
    throw "MonitorApp.Peer.Node.exe not found in $PublishDirectory"
}
if (-not $Version) {
    # Informational version looks like 0.1.7+<sha>; the side-by-side directory name must be the plain release version.
    $Version = ((Get-Item (Join-Path $PublishDirectory 'MonitorApp.Peer.Node.exe')).VersionInfo.ProductVersion -split '[+-]')[0]
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Cannot determine a release version (got '$Version'); pass -Version x.y.z." }
if (Get-Service -Name $name -ErrorAction SilentlyContinue) {
    throw "Service $name already exists. Run uninstall-service.ps1 first."
}

$versionDirectory = Join-Path $InstallDirectory "versions\$Version"
New-Item -ItemType Directory -Force -Path (Join-Path $versionDirectory 'node'), $DataDirectory | Out-Null
Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination (Join-Path $versionDirectory 'node') -Recurse -Force
if ($DesktopDirectory) {
    New-Item -ItemType Directory -Force -Path (Join-Path $versionDirectory 'desktop') | Out-Null
    Copy-Item -Path (Join-Path $DesktopDirectory '*') -Destination (Join-Path $versionDirectory 'desktop') -Recurse -Force
}

$arguments = @('--data-dir', "`"$DataDirectory`"", '--peer-port', $PeerPort, '--admin-port', $AdminPort)
if ($PeerBind) { $arguments += @('--peer-bind', $PeerBind) }
$binaryPath = "`"$exe`" $($arguments -join ' ')"

# sc.exe create registers the virtual account NT SERVICE\MonitorApp implicitly when obj= is that name.
# binPath contains quotes. Windows PowerShell 5.1 and PowerShell 7 quote native arguments differently, so the
# command line is passed verbatim through Start-Process (sc.exe parses it with the standard rules: \" is a quote).
$scArguments = "create $name binPath= `"$($binaryPath -replace '"', '\"')`" start= delayed-auto obj= `"$account`" DisplayName= `"Monitor App peer node`""
$sc = Start-Process -FilePath sc.exe -ArgumentList $scArguments -Wait -PassThru -NoNewWindow
if ($sc.ExitCode -ne 0) { throw "sc.exe create failed ($($sc.ExitCode))" }
& sc.exe description $name 'Monitor App peer node (loopback admin page, DPAPI-protected identity).' | Out-Null
# Recovery: restart after 5s, 30s, 60s; reset the failure count after a day; also act on non-crash failures.
& sc.exe failure $name reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
& sc.exe failureflag $name 1 | Out-Null

# Tailscale-only inbound rule for the peer port. It allows nothing while the peer listener is on 127.0.0.1 (the default);
# it matters only after the person turns on "Let my phone reach this computer", which moves the listener to the
# Tailscale address. Remote addresses outside 100.64.0.0/10 stay blocked.
Get-NetFirewallRule -DisplayName 'MonitorApp-peer-Tailscale' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName 'MonitorApp-peer-Tailscale' -Direction Inbound -Protocol TCP -LocalPort $PeerPort -RemoteAddress '100.64.0.0/10' -Action Allow | Out-Null

# Binaries: read/execute for the service account. Data: only the service account, SYSTEM, Administrators.
# ACLs are applied before the 'current' link exists so nothing is propagated through it; new version directories
# created later by the SYSTEM helper inherit these entries from versions\.
& icacls.exe $InstallDirectory /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "${account}:(OI)(CI)RX" | Out-Null
& icacls.exe $DataDirectory /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "${account}:(OI)(CI)M" | Out-Null

New-Item -ItemType SymbolicLink -Path (Join-Path $InstallDirectory 'current') -Target $versionDirectory | Out-Null

# The node learns where the versioned root is from the service environment (REG_MULTI_SZ under the service key).
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$name" -Name Environment -Type MultiString -Value @("MONITOR_APP_INSTALL_ROOT=$InstallDirectory")

# Detached update helper: on-demand task as SYSTEM, no triggers. The service account may only start it.
$helperArguments = "--apply-update `"$(Join-Path $DataDirectory 'update')`" --install-root `"$InstallDirectory`""
$taskAction = New-ScheduledTaskAction -Execute $exe -Argument $helperArguments
$taskPrincipal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$taskSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 15) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $taskName -Action $taskAction -Principal $taskPrincipal -Settings $taskSettings -Force | Out-Null
$serviceSid = ([System.Security.Principal.NTAccount] $account).Translate([System.Security.Principal.SecurityIdentifier]).Value
# SYSTEM and Administrators full; the service SID read + execute (= may run the task, cannot change it).
$taskSddl = "D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;$serviceSid)"
$scheduler = New-Object -ComObject Schedule.Service
$scheduler.Connect()
$scheduler.GetFolder('\').GetTask($taskName).SetSecurityDescriptor($taskSddl, 0)
# Presence of this admin-owned marker is what lets the node offer "approve and install".
[System.IO.File]::WriteAllText((Join-Path $InstallDirectory 'update-helper.json'), (@{ taskName = $taskName; version = $Version } | ConvertTo-Json -Compress), (New-Object System.Text.UTF8Encoding $false))

Write-Host "Installed version $Version side by side; update helper task '$taskName' registered."
Write-Host "Installed service '$name' as $account (delayed auto-start). Start it with: Start-Service $name"
Write-Host "Data directory: $DataDirectory (ACL restricted). Admin page: http://127.0.0.1:$AdminPort/ (loopback only)."
