; Inno Setup 6.x Script for Universal Backup Utility
; Produces signed x64 enterprise installer for Windows 10 & 11

#define MyAppName "Universal Backup Utility"
#define MyAppVersion "1.0.1"
#define MyAppPublisher "Universal Backup Project"
#define MyAppURL "https://github.com/gshuvam/Universal-Backup-Utility"
#define MyAppExeName "UniversalBackup.Desktop.exe"
#define MyAppCliExeName "UniversalBackup.Cli.exe"
#define MyAppHelperExeName "UniversalBackup.PrivilegedHelper.exe"

[Setup]
; Unique GUID identifying this application
AppId={{E68BC8E3-400A-492F-8A95-E58A46CE7B2B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\Universal Backup Utility
DefaultGroupName=Universal Backup Utility
AllowNoIcons=yes
LicenseFile=..\..\..\LICENSE
OutputDir=..\..\..\dist
OutputBaseFilename=UniversalBackup-{#MyAppVersion}-win-x64-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DisableDirPage=no
DisableProgramGroupPage=no
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "addtopath"; Description: "Add UniversalBackup.Cli and restic to user PATH environment variable"; GroupDescription: "System Integration:"; Flags: checkedonce

[Files]
; Primary Application Executables & Binaries (Published from Release\net10.0\win-x64\publish)
Source: "..\..\..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Bundled Pinned Runtimes (restic & rclone sidecars)
Source: "..\..\..\runtimes\win-x64\native\restic.exe"; DestDir: "{app}\runtimes\win-x64\native"; Flags: ignoreversion; Tasks: ; Flags: external skipifsourcedoesntexist
Source: "..\..\..\runtimes\win-x64\native\rclone.exe"; DestDir: "{app}\runtimes\win-x64\native"; Flags: ignoreversion; Tasks: ; Flags: external skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Universal Backup (CLI Terminal)"; Filename: "cmd.exe"; Parameters: "/K ""{app}\{#MyAppCliExeName}"" --help"; IconFilename: "{app}\{#MyAppCliExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Registry]
; Register application install path in PATH environment variable if task selected
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Tasks: addtopath; Check: NeedsAddPath(ExpandConstant('{app}'))

[Code]
// Checks if directory is already in user PATH to prevent duplicates
function NeedsAddPath(Param: string): boolean;
var
  OrigPath: string;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', OrigPath) then
  begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + UpperCase(Param) + ';', ';' + UpperCase(OrigPath) + ';') = 0;
end;

// Optional cleanup on uninstall
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Note: User catalog and backups are preserved by default for safety
  end;
end;
