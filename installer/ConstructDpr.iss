; Windows installer for CONSTRUCT DPR (Inno Setup 6). Built by the GitHub workflow:
;   iscc /DMyVersion=2.1.0 /DSourceExe=publish\app\ConstructDpr.exe installer\ConstructDpr.iss
; Installs for the current user (no administrator rights needed), adds Start-menu and optional desktop shortcuts,
; and an uninstaller in Settings → Apps. Data stays in %LOCALAPPDATA%\ConstructDpr and is kept on uninstall.

#ifndef MyVersion
  #define MyVersion "2.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\publish\app\ConstructDpr.exe"
#endif

[Setup]
AppId={{AF91B2AD-B50F-4D15-9EEF-BD53F567F833}
AppName=CONSTRUCT DPR
AppVersion={#MyVersion}
AppVerName=CONSTRUCT DPR {#MyVersion}
AppPublisher=CONSTRUCT DPR
DefaultDirName={autopf}\CONSTRUCT DPR
DefaultGroupName=CONSTRUCT DPR
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\out
OutputBaseFilename=CONSTRUCT-DPR-windows-setup
SetupIconFile=..\src\ConstructDpr.App\Assets\app.ico
UninstallDisplayIcon={app}\ConstructDpr.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\CONSTRUCT DPR"; Filename: "{app}\ConstructDpr.exe"
Name: "{group}\Uninstall CONSTRUCT DPR"; Filename: "{uninstallexe}"
Name: "{autodesktop}\CONSTRUCT DPR"; Filename: "{app}\ConstructDpr.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ConstructDpr.exe"; Description: "Start CONSTRUCT DPR now"; Flags: nowait postinstall skipifsilent
