; Inno Setup script for Signal Quality: a per-user install (no admin prompt) with a Start
; menu shortcut, an optional start-with-Windows entry, and an uninstaller in Settings > Apps.
;
; Build (Inno Setup 6), after building the app:
;   iscc /DAppVersion=1.2.3 installer\SignalQuality.iss
;   iscc /DAppVersion=1.2.3 /DSourceExe=..\SignalQuality.exe installer\SignalQuality.iss   (exe from build.cmd)
; Test: installer\test-installer.ps1 -Setup installer\out\SignalQuality-Setup-1.2.3.exe

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\bin\Release\SignalQuality.exe"
#endif

[Setup]
; Never change AppId: it's how Windows recognises a new version as an upgrade of this app.
AppId={{B45A6A47-29DF-4B13-BF27-9E1A8FF16938}
AppName=Signal Quality
AppVersion={#AppVersion}
AppVerName=Signal Quality {#AppVersion}
AppPublisher=Paul Flew
AppPublisherURL=https://github.com/paulflew/signal-quality
AppSupportURL=https://github.com/paulflew/signal-quality/issues
; Per-user: {autopf} resolves to %LOCALAPPDATA%\Programs, so no admin rights are needed.
PrivilegesRequired=lowest
DefaultDirName={autopf}\Signal Quality
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
MinVersion=10.0
WizardStyle=modern
SetupIconFile=..\assets\SignalQuality.ico
UninstallDisplayName=Signal Quality
UninstallDisplayIcon={app}\SignalQuality.exe
; The app lives in the tray with no window, so Restart Manager can't ask it to close;
; the [Code] section below stops it instead.
CloseApplications=no
OutputDir=out
OutputBaseFilename=SignalQuality-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes

[Tasks]
Name: startup; Description: "Start Signal Quality when I sign in to Windows"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Signal Quality"; Filename: "{app}\SignalQuality.exe"

[Registry]
; Same key and value the app's own "Start with Windows" menu item uses, so they stay in step.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SignalQuality"; ValueData: """{app}\SignalQuality.exe"""; Tasks: startup

[Run]
Filename: "{app}\SignalQuality.exe"; Description: "Start Signal Quality now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\SignalQuality"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

procedure StopRunningApp();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM SignalQuality.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);  { give Windows a moment to release the exe file }
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { Upgrading over a running copy would fail to replace the exe. }
  StopRunningApp();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopRunningApp();
    { The app can add this entry itself from its menu, so remove it whether or not setup did. }
    RegDeleteValue(HKEY_CURRENT_USER, RunKey, 'SignalQuality');
  end;
end;
