# Removes the old Monitor App (the .NET version) from this computer. Run by the
# new installer, elevated, only when traces of the old app are found.
# Removes: the MSI install, the MonitorApp Windows service, the
# MonitorAppUpdate scheduled task, its program folder, its data folder and
# Start menu shortcut. The new app keeps nothing from it.
$ErrorActionPreference = 'Continue'
$log = Join-Path $env:TEMP 'monitor-app-remove-old.log'
function Log($m) { "$(Get-Date -Format s) $m" | Add-Content -Path $log }
Log 'start'

# 1. MSI installs, by the old installer's UpgradeCode.
try {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    foreach ($code in @($installer.RelatedProducts('{6C0B5E7A-3F1D-4C9B-9E1A-5D2F8B7C4A10}'))) {
        Log "uninstalling MSI $code"
        $p = Start-Process msiexec.exe -ArgumentList "/x $code /qn /norestart" -Wait -PassThru
        Log "msiexec exit $($p.ExitCode)"
    }
} catch { Log "msi lookup failed: $_" }

# 2. Service and update task (also left by zip installs).
if (Get-Service -Name MonitorApp -ErrorAction SilentlyContinue) {
    Stop-Service -Name MonitorApp -Force -ErrorAction SilentlyContinue
    & sc.exe delete MonitorApp | Out-Null
    Log 'service MonitorApp removed'
}
Get-Process -Name 'MonitorApp*' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName MonitorAppUpdate -Confirm:$false -ErrorAction SilentlyContinue

# 3. Folders and shortcuts.
foreach ($d in @(
        (Join-Path $env:ProgramFiles 'MonitorApp'),
        (Join-Path $env:ProgramFiles 'Monitor App'),
        (Join-Path $env:ProgramData 'MonitorApp'),
        (Join-Path $env:LOCALAPPDATA 'MonitorApp'),
        (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Monitor App.lnk'))) {
    if (Test-Path -LiteralPath $d) {
        Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue
        Log "removed $d (still there: $(Test-Path -LiteralPath $d))"
    }
}
Log 'done'
