; Builds dist\PatchLight-Setup.exe from the published app in dist\PatchLight.
; Run scripts\publish.ps1 instead of calling this directly.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6E2F3B1A-8C4D-4F7E-9B2A-5D1C0E7F4A93}
AppName=PatchLight
AppVersion={#AppVersion}
AppVerName=PatchLight {#AppVersion}
VersionInfoProductName=PatchLight
VersionInfoVersion={#AppVersion}
VersionInfoDescription=PatchLight installer
AppPublisher=G4SBO
AppPublisherURL=https://g4sbo.com
AppSupportURL=https://github.com/G4SBO/PatchLight/issues
; Program Files: only administrators can change it, which the daily check needs.
DefaultDirName={autopf}\PatchLight
DisableDirPage=yes
DefaultGroupName=PatchLight
DisableProgramGroupPage=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
; Asks the person to close PatchLight (it may be in the tray) before installing or removing it.
AppMutex=PatchLight.App.Instance
CloseApplications=yes
SetupIconFile=..\src\PatchLight.App\Assets\app.ico
UninstallDisplayIcon={app}\PatchLight.exe
UninstallDisplayName=PatchLight
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir=..\dist
OutputBaseFilename=PatchLight-Setup

[Tasks]
Name: "startwithwindows"; Description: "Start PatchLight with Windows (it waits quietly in the tray and warns you about bad updates)"
Name: "desktopicon"; Description: "Add a shortcut to the desktop"; Flags: unchecked

[Registry]
; Same entry the app's "Start with Windows" switch uses, so the person can turn it off there too.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PatchLight"; ValueData: """{app}\PatchLight.exe"" --tray"; Flags: uninsdeletevalue; Tasks: startwithwindows

[Files]
Source: "..\dist\PatchLight\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\PatchLight"; Filename: "{app}\PatchLight.exe"
Name: "{autodesktop}\PatchLight"; Filename: "{app}\PatchLight.exe"; Tasks: desktopicon

[Run]
; Turns the hourly automatic check on (automatic pausing of red updates is on by default and needs it);
; a once-a-day check from 0.4 or older becomes the hourly one.
Filename: "{app}\PatchLight.exe"; Parameters: "--setup-daily-check"; Flags: runhidden waituntilterminated
; Opened as the person, not as administrator, so "Start with Windows" lands in their own account.
Filename: "{app}\PatchLight.exe"; Description: "Open PatchLight"; Flags: nowait postinstall skipifsilent runasoriginaluser
; After an update started from the app (/RELAUNCH). The app starts this installer as administrator, so
; runasoriginaluser would be administrator too; Explorer opens it as the person instead.
Filename: "{win}\explorer.exe"; Parameters: """{app}\PatchLight.exe"""; Flags: nowait; Check: RelaunchAfterUpdate

[UninstallRun]
; Resumes everything PatchLight paused, removes the automatic check, its "wait" policy, "Start with Windows" and its data.
Filename: "{app}\PatchLight.exe"; Parameters: "--uninstall-cleanup"; Flags: runhidden waituntilterminated; RunOnceId: "PatchLightCleanup"

[UninstallDelete]
; The person's own settings, saved news reports and error log (the cleanup above runs elevated and doesn't reach them).
Type: filesandordirs; Name: "{localappdata}\PatchLight"

[Code]
function RelaunchAfterUpdate: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/RELAUNCH') = 0 then
      Result := True;
end;
