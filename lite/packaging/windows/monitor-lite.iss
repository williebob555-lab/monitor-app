; Monitor Lite installer for Windows. Per-user install: no admin prompt.
; Built by .github/workflows/lite.yml with:
;   iscc /DAppVersion=x.y.z /DBundle=<flutter release dir> /DOutDir=<out> monitor-lite.iss
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6C1C9E2B-6E57-4F7B-9B54-3E1A8D7A5C21}
AppName=Monitor Lite
AppVersion={#AppVersion}
AppPublisher=Monitor Lite
DefaultDirName={localappdata}\Programs\Monitor Lite
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutDir}
OutputBaseFilename=monitor-lite-{#AppVersion}-windows-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no
UninstallDisplayIcon={app}\monitor_lite.exe
UninstallDisplayName=Monitor Lite

[Files]
Source: "{#Bundle}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{userprograms}\Monitor Lite"; Filename: "{app}\monitor_lite.exe"

[Registry]
; The background collector starts when you sign in. It has no window.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MonitorLiteAgent"; ValueData: """{app}\monitor-agent.exe"""; Flags: uninsdeletevalue

[Run]
Filename: "{app}\monitor-agent.exe"; Flags: nowait
Filename: "{app}\monitor_lite.exe"; Description: "Open Monitor Lite"; Flags: nowait postinstall

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM monitor-agent.exe"; Flags: runhidden; RunOnceId: "StopAgent"

[Code]
// Stop the running collector so its file can be replaced during an update.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM monitor-agent.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;
