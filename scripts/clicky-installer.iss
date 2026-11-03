; Clicky for Windows - Inno Setup Installer Script
; Requires Inno Setup 6 (https://jrsoftware.org/isinfo.php)
;
; Build with:
;   iscc.exe /DPublishDir=..\publish\win-x64 /DOutputDir=..\installer clicky-installer.iss
;
; Or via the release script:
;   powershell -ExecutionPolicy Bypass -File release.ps1

#ifndef PublishDir
  #define PublishDir "..\publish\win-x64"
#endif

#ifndef OutputDir
  #define OutputDir "..\installer"
#endif

#define AppName "Clicky"
#define AppVersion "2.0.4"
#define AppPublisher "Clicky"
#define AppExeName "Clicky.App.exe"
#define AppURL "https://github.com/julianjear/makesomething-mac-app"

[Setup]
AppId={{F3A2B1C4-5D6E-7F8A-9B0C-1D2E3F4A5B6C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=Setup_Clicky_{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
; Use PerMonitorV2 DPI awareness for the installer itself
SetupIconFile=compiler:SetupClassicIcon.ico
UninstallDisplayIcon={app}\{#AppExeName}
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startmenu"; Description: "Create a &Start Menu shortcut"; GroupDescription: "Additional shortcuts:"
Name: "autostart"; Description: "Start {#AppName} automatically when Windows starts"; GroupDescription: "Startup:"

[Files]
; Copy all published files to the install directory, excluding legacy appsettings.json
; (API keys are now stored in %APPDATA%\Clicky via SecretsStore/SettingsStore, not in appsettings.json)
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "appsettings.json"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Start Menu shortcut
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startmenu
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"; Tasks: startmenu

[Registry]
; Auto-start on login (user-level, matches AutoStartRegistration.cs)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "Clicky"; ValueData: """{app}\{#AppExeName}"""; \
  Flags: uninsdeletevalue; Tasks: autostart

[Run]
; Offer to launch after install
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; \
  Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Clean up any runtime-created files in the app directory
Type: filesandordirs; Name: "{app}"

[UninstallRun]
; Kill the app before uninstalling
Filename: "taskkill"; Parameters: "/F /IM {#AppExeName}"; Flags: runhidden; RunOnceId: "KillClicky"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    AppDataDir := ExpandConstant('{userappdata}\Clicky');
    if DirExists(AppDataDir) then
    begin
      if MsgBox('Do you also want to delete your Clicky settings and saved API keys?' + #13#10 +
                '(These are stored in ' + AppDataDir + ')',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DelTree(AppDataDir, True, True, True);
      end;
    end;
  end;
end;
