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
#define AppVersion   "2.0.0"
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
; App-is-running detection: must match the exact mutex name App.xaml.cs's OnLaunched() creates
; ("AviatesAirTracker-AppMutex"), held for the app's whole lifetime. Without this, a silent
; update install (UpdateService.DownloadAndInstallAsync, launched with /VERYSILENT
; /SUPPRESSMSGBOXES) had nothing telling Setup the old process was still holding the app's DLLs
; open -- it would race a fixed 500ms shutdown delay against Setup's own file copy, silently lose
; that race often enough to matter (self-contained .NET ships dozens of loose runtime/SDK DLLs,
; not just the one EXE), and /SUPPRESSMSGBOXES swallowed the resulting file-in-use failures
; entirely -- the update appeared to run but silently never actually replaced the files.
; CloseApplications (default yes, made explicit here) makes Setup use the Windows Restart
; Manager to close whatever process is holding this mutex before it starts copying -- the
; correct fix for this whole race, rather than trying to out-guess it with a longer delay.
AppMutex                  = AviatesAirTracker-AppMutex
CloseApplications         = yes
; The app already relaunches itself via [Run] below post-install; RestartApplications would be
; Restart Manager's OWN "reopen whatever Setup closed" mechanism, which would just be a second,
; redundant relaunch racing the one in [Run].
RestartApplications       = no
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

; Everything else the publish produced. This became necessary with the WinUI 3 migration:
; PublishSingleFile is unsupported by the Windows App SDK, so unlike the old WPF build,
; dist\ is a genuine multi-hundred-file folder tree (the self-contained .NET runtime,
; WindowsAppSDK runtime incl. its .winmd/.pri resource files, ASP.NET Core assemblies for
; the embedded EFB companion server, remaining wwwroot\images\ assets, the SimConnect DLLs
; under Libs\ and Libs\MSFS2020\) rather than one self-extracting EXE. The narrow,
; hand-picked entries this replaced (a specific staticwebassets json, wwwroot\*, a literal
; "SimConnect.dll" path that no longer exists post-migration, and a flattening *.dll
; wildcard) silently dropped AviatesAirTracker.deps.json, .runtimeconfig.json and .pri —
; the .NET/WinUI runtime bootstrap and resource-lookup files — from every installed build.
; createallsubdirs preserves the real directory structure (Libs\ vs Libs\MSFS2020\
; specifically matters: SimConnectManager.cs distinguishes the two SDK versions by that
; exact path).
Source: "{#SourceDir}\*"; Excludes: "{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Documentation
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
; Start menu
; IconFilename is explicit here (not just relying on {#AppExeName}'s own embedded icon) so the
; shortcuts still look right even if a stripped/trimmed build of the exe ever lost its icon
; resource for some reason -- belt-and-suspenders alongside AviatesAirTracker.csproj's own
; <ApplicationIcon>, which is what actually gives the exe (and therefore its taskbar/window
; icon) a real icon in the first place.
Name: "{group}\{#AppName}";           Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

; Desktop (optional task)
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Startup entry (optional task)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExeName}"" --minimized"; Flags: uninsdeletevalue; Tasks: startupicon

; App registration
Root: HKCU; Subkey: "Software\AviatesAir\FlightTracker"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall

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
