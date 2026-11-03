; Inno Setup script -> dist\MailAttachmentAgent-Setup-<version>.exe
#define MyAppName "Mail Attachment Agent"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

[Setup]
AppId={{8C1B2E4A-5D7F-4E2B-9A61-3F0C7D5B1E92}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=parthag
DefaultDirName={autopf}\Mail Attachment Agent
DefaultGroupName=Mail Attachment Agent
DisableProgramGroupPage=yes
; Installs for the current user - no administrator rights needed
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist
OutputBaseFilename=MailAttachmentAgent-Setup-{#MyAppVersion}
SetupIconFile=icon.ico
UninstallDisplayIcon={app}\MailAgent.exe
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist\MailAgent\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Mail Attachment Agent"; Filename: "{app}\MailAgent.exe"
Name: "{group}\Mail Attachment Agent (demo mailbox)"; Filename: "{app}\MailAgent.exe"; Parameters: "desktop --demo"
Name: "{group}\Uninstall Mail Attachment Agent"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Mail Attachment Agent"; Filename: "{app}\MailAgent.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MailAgent.exe"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  WV2Key = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function HasWebView2(): Boolean;
var v: String;
begin
  Result := (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) and (v <> '') and (v <> '0.0.0.0'))
         or (RegQueryStringValue(HKLM, WV2Key, 'pv', v) and (v <> '') and (v <> '0.0.0.0'))
         or (RegQueryStringValue(HKCU, WV2Key, 'pv', v) and (v <> '') and (v <> '0.0.0.0'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (not HasWebView2()) and (not WizardSilent()) then
    MsgBox('Microsoft Edge WebView2 Runtime was not found. Windows 10/11 normally include it.' + #13#10 +
           'If the app window stays blank, install it from:' + #13#10 +
           'https://go.microsoft.com/fwlink/p/?LinkId=2124703', mbInformation, MB_OK);
end;
