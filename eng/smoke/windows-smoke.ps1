#Requires -Version 7.0
#Requires -RunAsAdministrator
<#
Windows service lifecycle smoke. Usage: windows-smoke.ps1 -ZipPath <monitorapp-node-windows-x64-V.zip>
Extracts the preview zip, runs Install-MonitorApp.ps1 (service starts), then checks the service account, DPAPI identity,
local API readings, restart identity stability, the T043 side-by-side layout and update task, and uninstall.
Run on a GitHub windows runner (the runner user is an administrator).
#>
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $ZipPath)

$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:ProgramFiles 'MonitorApp'
$dataDir = Join-Path $env:ProgramData 'MonitorApp'
$api = 'http://127.0.0.1:55432'

function Log($m) { Write-Host "[smoke $(Get-Date -Format HH:mm:ss)] $m" }
function Fail($m) { Write-Host "::error::$m"; throw $m }
function Step($m) { Write-Host "::group::$m"; Log $m }
function EndStep { Write-Host '::endgroup::' }

function Get-PeerId {
    try { (Invoke-RestMethod -Uri "$api/api/runtime" -TimeoutSec 5).localPeer.peerId } catch { $null }
}
function Wait-Api([int] $Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $id = Get-PeerId
        if ($id) { return $id }
        Start-Sleep -Seconds 2
    }
    Fail "local API did not report a peer id within $Seconds s"
}
function Get-State { Invoke-RestMethod -Uri "$api/api/v1/state?lookbackSeconds=3600&limit=500" -TimeoutSec 10 }
function Wait-Readings([int] $Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    $last = $null
    while ((Get-Date) -lt $deadline) {
        try {
            $last = Get-State
            $valued = @($last.points | Where-Object { $null -ne $_.value })
            if ($valued.Count -gt 0) {
                Log "readings present: $($valued.Count) valued points"
                $valued | Select-Object -First 5 | ForEach-Object { Log ("{0} {1} {2} {3} {4}" -f $_.signalId, ($_.value | ConvertTo-Json -Compress), $_.unit, $_.quality, $_.freshness) }
                return
            }
        } catch { }
        Start-Sleep -Seconds 3
    }
    Log "last state: $($last | ConvertTo-Json -Depth 5 -Compress)"
    Fail "no valued readings from /api/v1/state within $Seconds s"
}
function Get-MaxSequence {
    $max = (@((Get-State).points) | ForEach-Object { [decimal]$_.sequence } | Measure-Object -Maximum).Maximum
    if ($null -eq $max) { return [decimal]0 }
    return [decimal]$max
}

$work = Join-Path $env:RUNNER_TEMP 'smoke-extract'
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
Step 'extract and install the zip'
Expand-Archive -LiteralPath $ZipPath -DestinationPath $work
Get-ChildItem $work | Format-Table Name
& (Join-Path $work 'Install-MonitorApp.ps1')
EndStep

Step 'service state and account'
$svc = Get-Service -Name MonitorApp
Log "service status: $($svc.Status)"
$cim = Get-CimInstance Win32_Service -Filter "Name='MonitorApp'"
Log "StartName=$($cim.StartName) StartMode=$($cim.StartMode) PathName=$($cim.PathName)"
if ($svc.Status -ne 'Running') { Fail 'service MonitorApp is not running' }
if ($cim.StartName -ne 'NT SERVICE\MonitorApp') { Fail "service runs as '$($cim.StartName)', expected the virtual account NT SERVICE\MonitorApp" }
$proc = Get-CimInstance Win32_Process -Filter "ProcessId=$($cim.ProcessId)"
Log "process: $($proc.Name) pid $($proc.ProcessId) path $($proc.ExecutablePath)"
EndStep

Step 'local API, identity and readings'
$peer1 = Wait-Api 90
Log "peer id: $peer1"
Wait-Readings 120
$seq1 = Get-MaxSequence
$identity = Join-Path $dataDir 'identity.dpapi'
if (-not (Test-Path $identity)) { Get-ChildItem $dataDir | Format-Table Name, Length; Fail "DPAPI identity file missing: $identity" }
$hash1 = (Get-FileHash $identity -Algorithm SHA256).Hash
Log "identity.dpapi $hash1; max sequence $seq1"
& icacls.exe $dataDir
EndStep

Step 'restart keeps identity'
Restart-Service -Name MonitorApp -Force
(Get-Service MonitorApp).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
$peer2 = Wait-Api 90
if ($peer1 -ne $peer2) { Fail "peer id changed on restart: $peer1 -> $peer2" }
if ((Get-FileHash $identity -Algorithm SHA256).Hash -ne $hash1) { Fail 'identity.dpapi changed on restart' }
Wait-Readings 120
$seq2 = Get-MaxSequence
if ($seq2 -lt $seq1) { Fail "origin sequence went backwards after restart ($seq1 -> $seq2)" }
Log "peer id stable ($peer2); sequence $seq1 -> $seq2"
EndStep

Step 'T043 side-by-side layout and update task'
$current = Get-Item -LiteralPath (Join-Path $installDir 'current')
Log "current -> $($current.Target) (LinkType $($current.LinkType))"
if ($current.LinkType -ne 'SymbolicLink') { Fail "'current' is not a directory symlink" }
$versions = @(Get-ChildItem (Join-Path $installDir 'versions') -Directory)
if ($versions.Count -lt 1) { Fail 'no versions\<version> directory' }
foreach ($path in @('current\node\MonitorApp.Peer.Node.exe', 'current\desktop\MonitorApp.Client.Desktop.exe', 'update-helper.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $installDir $path))) { Fail "missing $installDir\$path" }
}
$task = Get-ScheduledTask -TaskName MonitorAppUpdate -ErrorAction SilentlyContinue
if (-not $task) { Fail 'scheduled task MonitorAppUpdate is not registered' }
Log "task principal: $($task.Principal.UserId) run level $($task.Principal.RunLevel), state $($task.State)"
if ($task.Principal.UserId -notin @('SYSTEM', 'S-1-5-18')) { Fail "update task runs as $($task.Principal.UserId), expected SYSTEM" }
EndStep

Step 'uninstall keeps data, then purge'
& (Join-Path $work 'uninstall-service.ps1')
if (Get-Service -Name MonitorApp -ErrorAction SilentlyContinue) { Fail 'service still registered after uninstall' }
if (Get-ScheduledTask -TaskName MonitorAppUpdate -ErrorAction SilentlyContinue) { Fail 'update task still registered after uninstall' }
if (Test-Path $installDir) { Fail 'install directory still present after uninstall' }
if (-not (Test-Path $identity)) { Fail 'identity removed by a non-purge uninstall' }
& (Join-Path $work 'uninstall-service.ps1') -Purge
if (Test-Path $dataDir) { Fail 'data directory still present after -Purge' }
EndStep

Log 'windows lifecycle smoke passed'
