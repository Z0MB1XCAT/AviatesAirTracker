; ============================================================
; AVIATES AIR FLIGHT TRACKER - Inno Setup Installer Script
;
; Requires: Inno Setup 6.x  (https://jrsoftware.org/isinfo.php)
;
; Build steps:
;   1. Run:  .\build.ps1 -Release -Installer
;      (or manually: ISCC.exe installer\AviatesAirTracker_Setup.iss)
; ============================================================

#define AppName      "Aviates Air Flight Tracker"
#define AppVersion   "1.0.0"
#define AppPublisher "Aviates Air"
#define AppURL       "https://z0mb1xcat.github.io/avaitesairlol"
#define AppExeName   "AviatesAirTracker.exe"
#define SourceDir    "..\dist"

[Setup]
; Basic app info
AppId                     = {{A1B2C3D4-AVIA-TESA-IRTR-ACKER000001}
AppName                   = {#AppName}
AppVersion                = {#AppVersion}
AppPublisherURL           = {#AppURL}
AppSupportURL             = https://discord.gg/WjBubsD8E9
AppUpdatesURL             = {#AppURL}
DefaultDirName            = {autopf}\AviatesAir\FlightTracker
DefaultGroupName          = Aviates Air
AllowNoIcons              = yes
; License
LicenseFile               = LICENSE.txt
; Compression
Compression               = lzma2/ultra64
SolidCompression          = yes
; Output
OutputDir                 = ..\installer
OutputBaseFilename        = AviatesAirTracker_Setup_v{#AppVersion}
; Appearance
WizardStyle               = modern
SetupIconFile             = ..\AviatesAirTracker\Resources\Assets\aviates_icon.ico
UninstallDisplayIcon      = {app}\{#AppExeName}
; Privileges
PrivilegesRequired        = lowest
PrivilegesRequiredOverridesAllowed = dialog
; Architecture
ArchitecturesInstallIn64BitMode = x64compatible
ArchitecturesAllowed      = x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon";  Description: "{cm:CreateDesktopIcon}";   GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon";  Description: "Launch on Windows startup"; GroupDescription: "Startup"; Flags: unchecked

[Files]
; Main executable
Source: "{#SourceDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion

; Blazor static web assets — required for the UI to render
Source: "{#SourceDir}\AviatesAirTracker.staticwebassets.endpoints.json"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#SourceDir}\wwwroot\*"; DestDir: "{app}\wwwroot"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

; SimConnect DLLs (optional — enables live telemetry from MSFS)
Source: "{#SourceDir}\SimConnect.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#SourceDir}\*.dll";          DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist recursesubdirs

; Documentation
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
; Start menu
Name: "{group}\{#AppName}";           Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

; Desktop (optional task)
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Startup entry (optional task)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExeName}"" --minimized"; Flags: uninsdeletevalue; Tasks: startupicon

; App registration
Root: HKCU; Subkey: "Software\AviatesAir\FlightTracker"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\AviatesAir\FlightTracker\logs"
Type: files;          Name: "{app}\aviates_settings.json"

[Code]
// ============================================================
// Check MSFS is installed (informational only)
// ============================================================

function IsMSFSInstalled(): Boolean;
begin
  Result := RegKeyExists(HKLM, 'SOFTWARE\Microsoft\FlightSimulator') or
            RegKeyExists(HKCU, 'SOFTWARE\Microsoft\FlightSimulator') or
            DirExists(ExpandConstant('{pf}\WindowsApps\Microsoft.FlightSimulator_8wekyb3d8bbwe'));
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpWelcome then
  begin
    if not IsMSFSInstalled() then
      WizardForm.WelcomeLabel2.Caption :=
        WizardForm.WelcomeLabel2.Caption + #13#10 + #13#10 +
        'Note: Microsoft Flight Simulator does not appear to be installed.' + #13#10 +
        'The tracker requires MSFS 2020 or MSFS 2024 for live telemetry.' + #13#10 +
        'All other features (flight history, maps, statistics) work without it.';
  end;
end;
