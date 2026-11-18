; Inno Setup script for the Drawing Reader Windows installer.
; Built by desktop\build-windows.ps1:  ISCC /DAppVersion=1.0.0 /DDistDir=<bundle> installer.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef DistDir
  #define DistDir "..\dist\DrawingReader"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{8F3C2A51-6E4B-4C2D-9A7E-2B5D1F0C7A93}
AppName=Drawing Reader
AppVersion={#AppVersion}
AppVerName=Drawing Reader {#AppVersion}
AppPublisher=Drawing Reader
DefaultDirName={autopf}\Drawing Reader
DefaultGroupName=Drawing Reader
DisableProgramGroupPage=yes
; Per-user install by default (no administrator rights needed); the user can choose all users.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=DrawingReaderSetup-{#AppVersion}
SetupIconFile=icon.ico
UninstallDisplayIcon={app}\DrawingReader.exe
UninstallDisplayName=Drawing Reader
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "sampledata"; Description: "Load sample projects (3 projects, 100 drawings, 300 revisions)"; GroupDescription: "Data:"

[Files]
Source: "{#DistDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Drawing Reader"; Filename: "{app}\DrawingReader.exe"
Name: "{autoprograms}\Uninstall Drawing Reader"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Drawing Reader"; Filename: "{app}\DrawingReader.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\drawing-reader-cli.exe"; Parameters: "--seed-sample-data"; StatusMsg: "Loading sample projects (about a minute)..."; Flags: runhidden waituntilterminated runasoriginaluser; Tasks: sampledata
Filename: "{app}\DrawingReader.exe"; Description: "Start Drawing Reader"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM DrawingReader.exe"; Flags: runhidden; RunOnceId: "StopDrawingReader"

[Messages]
FinishedLabel=Drawing Reader has been installed. It runs in your web browser; sign in with admin / Admin@12345 and change the password from the account menu. Your drawings and database are kept in %LOCALAPPDATA%\DrawingReader (not removed on uninstall).
