; Inno Setup script for Drawing Register (C# / WPF / SQLite).
; Built by build-windows.ps1:  ISCC /DAppVersion=1.0.0 /DPublishDir=<publish folder> DrawingRegister.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{4C9E2F17-3B8A-4E61-9D2C-7A5B1E8F0C42}
AppName=Drawing Register
AppVersion={#AppVersion}
AppVerName=Drawing Register {#AppVersion}
AppPublisher=Drawing Register
DefaultDirName={autopf}\Drawing Register
DefaultGroupName=Drawing Register
DisableProgramGroupPage=yes
; Per-user install by default (no administrator rights needed); the user can choose all users.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=DrawingRegisterSetup-{#AppVersion}
SetupIconFile=..\src\DrawingRegister.App\app.ico
UninstallDisplayIcon={app}\DrawingRegister.exe
UninstallDisplayName=Drawing Register
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.xml"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Drawing Register"; Filename: "{app}\DrawingRegister.exe"
Name: "{autoprograms}\Uninstall Drawing Register"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Drawing Register"; Filename: "{app}\DrawingRegister.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\DrawingRegister.exe"; Description: "Start Drawing Register"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Messages]
FinishedLabel=Drawing Register has been installed. Click "Add project", then "Upload PDFs" or "Upload folder" (or drag PDFs onto the list). Your projects and drawings are stored in %LOCALAPPDATA%\DrawingRegister and are kept when you uninstall or upgrade.
