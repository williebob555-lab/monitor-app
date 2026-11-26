; Monitor App installer for Windows (the lightweight rebuild).
; Per-user install. If the old .NET Monitor App is found, it is removed first
; (one Windows "Yes" prompt, since the old one was installed for all users).
; Built by .github/workflows/lite.yml with:
;   iscc /DAppVersion=x.y.z /DBundle=<flutter release dir> /DOutDir=<out> monitor-lite.iss
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6C1C9E2B-6E57-4F7B-9B54-3E1A8D7A5C21}
AppName=Monitor App
AppVersion={#AppVersion}
AppPublisher=Monitor App
DefaultDirName={localappdata}\Programs\Monitor App
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutDir}
OutputBaseFilename=monitor-app-{#AppVersion}-windows-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no
UninstallDisplayIcon={app}\monitor_lite.exe
UninstallDisplayName=Monitor App

[Files]
Source: "{#Bundle}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "remove-old-monitor-app.ps1"; DestDir: "{tmp}"; Flags: dontcopy

[Icons]
Name: "{userprograms}\Monitor App"; Filename: "{app}\monitor_lite.exe"

[Registry]
; The background collector starts when you sign in. It has no window.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MonitorAppAgent"; ValueData: """{app}\monitor-agent.exe"""; Flags: uninsdeletevalue

[Run]
Filename: "{app}\monitor-agent.exe"; Flags: nowait
Filename: "{app}\monitor_lite.exe"; Description: "Open Monitor App"; Flags: nowait postinstall

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM monitor-agent.exe"; Flags: runhidden; RunOnceId: "StopAgent"

[Code]
function OldAppPresent(): Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\MonitorApp')
    or DirExists(ExpandConstant('{commonpf64}\MonitorApp'))
    or DirExists(ExpandConstant('{commonappdata}\MonitorApp'));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  Script: String;
begin
  // Stop our own collector so its file can be replaced during an update.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM monitor-agent.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  // Replace the old .NET app completely.
  if OldAppPresent() then begin
    ExtractTemporaryFile('remove-old-monitor-app.ps1');
    Script := ExpandConstant('{tmp}\remove-old-monitor-app.ps1');
    if not ShellExec('runas', 'powershell.exe',
        '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + Script + '"',
        '', SW_HIDE, ewWaitUntilTerminated, Code) then
      Log('Removing the old Monitor App was not allowed; it stays installed.');
  end;
  Result := '';
end;
