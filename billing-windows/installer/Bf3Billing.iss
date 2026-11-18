; Inno Setup script for BF#3 Billing. Built by CI (.github/workflows/billing-windows.yml):
;   iscc /DAppVersion=1.0.N installer\Bf3Billing.iss
; Installs the self-contained app; the data file in %LOCALAPPDATA%\Bf3Billing is never touched by setup or uninstall.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{6E1B3C7A-4D2F-4B8E-9C1A-B3F0D2E5A7C4}
AppName=BF#3 Billing
AppVersion={#AppVersion}
AppVerName=BF#3 Billing {#AppVersion}
AppPublisher=MCK – BF#3
DefaultDirName={autopf}\BF3 Billing
DefaultGroupName=BF#3 Billing
DisableProgramGroupPage=yes
; Installs for the current user without admin rights; an administrator can choose "all users".
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\publish
OutputBaseFilename=BF3-Billing-Setup-{#AppVersion}
SetupIconFile=..\src\Bf3Billing.App\app.ico
UninstallDisplayIcon={app}\Bf3Billing.exe
UninstallDisplayName=BF#3 Billing
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "..\publish\Bf3Billing\Bf3Billing.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; DestName: "README.md"; Flags: ignoreversion

[Icons]
Name: "{group}\BF#3 Billing"; Filename: "{app}\Bf3Billing.exe"
Name: "{group}\Read me"; Filename: "{app}\README.md"
Name: "{group}\Uninstall BF#3 Billing"; Filename: "{uninstallexe}"
Name: "{autodesktop}\BF#3 Billing"; Filename: "{app}\Bf3Billing.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Bf3Billing.exe"; Description: "Start BF#3 Billing now"; Flags: nowait postinstall skipifsilent
