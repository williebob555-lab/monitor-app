#Requires -Version 7.0
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
    [int] $AdminPort = 55432
)

$ErrorActionPreference = 'Stop'
$name = 'MonitorApp'
$account = "NT SERVICE\$name"
$exe = Join-Path $InstallDirectory 'MonitorApp.Peer.Node.exe'

if (-not (Test-Path (Join-Path $PublishDirectory 'MonitorApp.Peer.Node.exe'))) {
    throw "MonitorApp.Peer.Node.exe not found in $PublishDirectory"
}
if (Get-Service -Name $name -ErrorAction SilentlyContinue) {
    throw "Service $name already exists. Run uninstall-service.ps1 first."
}

New-Item -ItemType Directory -Force -Path $InstallDirectory, $DataDirectory | Out-Null
Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $InstallDirectory -Recurse -Force

$arguments = @('--data-dir', "`"$DataDirectory`"", '--peer-port', $PeerPort, '--admin-port', $AdminPort)
if ($PeerBind) { $arguments += @('--peer-bind', $PeerBind) }
$binaryPath = "`"$exe`" $($arguments -join ' ')"

# sc.exe create registers the virtual account NT SERVICE\MonitorApp implicitly when obj= is that name.
& sc.exe create $name binPath= $binaryPath start= delayed-auto obj= $account DisplayName= 'Monitor App peer node' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed ($LASTEXITCODE)" }
& sc.exe description $name 'Monitor App peer node (loopback admin page, DPAPI-protected identity).' | Out-Null
# Recovery: restart after 5s, 30s, 60s; reset the failure count after a day; also act on non-crash failures.
& sc.exe failure $name reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
& sc.exe failureflag $name 1 | Out-Null

# Binaries: read/execute for the service account. Data: only the service account, SYSTEM, Administrators.
& icacls.exe $InstallDirectory /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "${account}:(OI)(CI)RX" | Out-Null
& icacls.exe $DataDirectory /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "${account}:(OI)(CI)M" | Out-Null

Write-Host "Installed service '$name' as $account (delayed auto-start). Start it with: Start-Service $name"
Write-Host "Data directory: $DataDirectory (ACL restricted). Admin page: http://127.0.0.1:$AdminPort/ (loopback only)."
