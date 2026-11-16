; =====================================================================
;  BSL Project Control – Windows installer (NSIS 3, 64-bit)
;
;  Build:  makensis -DVERSION=1.1.0 -DSRC=..\publish\release BSLProjectControl.nsi
;
;  Components
;    * BSL Project Control (required) – self-contained app, no .NET needed
;    * SQL Server 2022 Express LocalDB – database on this PC (default)
;    * SQL Server 2022 Express (shared) – Windows service other PCs can use (optional)
;
;  SQL Server files are BUNDLED when present at build time:
;    redist\SqlLocalDB.msi          and   redist\SQLEXPR_x64_ENU.exe
;  (the GitHub build downloads them from Microsoft); otherwise sql-setup.ps1 downloads them
;  from Microsoft during installation.
;
;  Silent install:  Setup.exe /S            (app + LocalDB)
;                   Setup.exe /S /EXPRESS   (app + shared SQL Server Express)
;                   Setup.exe /S /NOSQL     (app only)
; =====================================================================
Unicode true
Target amd64-unicode          ; 64-bit installer – the app itself is x64 only
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "Sections.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"

!ifndef VERSION
  !define VERSION "1.1.0"
!endif
!ifndef SRC
  !define SRC "..\publish\release"
!endif
!define APPNAME   "BSL Project Control"
!define EXENAME   "BSLProjectControl.exe"
!define REGKEY    "Software\Microsoft\Windows\CurrentVersion\Uninstall\BSLProjectControl"
!define DBNAME    "BSL_ProjectControl"

!if /FileExists "redist\SqlLocalDB.msi"
  !define BUNDLE_LOCALDB
!endif
!if /FileExists "redist\SQLEXPR_x64_ENU.exe"
  !define BUNDLE_EXPRESS
!endif

Name "${APPNAME} ${VERSION}"
!ifdef BUNDLE_EXPRESS
  OutFile "BSLProjectControl-Setup-${VERSION}-with-SQL-Express.exe"
!else ifdef BUNDLE_LOCALDB
  OutFile "BSLProjectControl-Setup-${VERSION}-with-SQL-LocalDB.exe"
!else
  OutFile "BSLProjectControl-Setup-${VERSION}.exe"
!endif
InstallDir "$PROGRAMFILES64\BSL Project Control"
InstallDirRegKey HKLM "Software\BSLProjectControl" "InstallDir"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
BrandingText "${APPNAME} ${VERSION}"
ShowInstDetails show

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APPNAME}"
VIAddVersionKey "FileDescription" "${APPNAME} installer"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "BSL BF#3 Revamp"

Var HasExpress      ; 1 when instance SQLEXPRESS exists or was installed
Var SqlFailed

!define MUI_ICON "app.ico"
!define MUI_UNICON "app.ico"
!define MUI_ABORTWARNING
!define MUI_COMPONENTSPAGE_SMALLDESC
!define MUI_WELCOMEPAGE_TITLE "BSL PROJECT CONTROL ${VERSION}"
!define MUI_WELCOMEPAGE_TEXT "Drawings, BOM, contractors, work orders, BOQ, RA bills, notes and drawing uploads.$\r$\n$\r$\nThis setup can also install Microsoft SQL Server 2022 Express:$\r$\n  • LocalDB – database on this PC (recommended for one PC)$\r$\n  • SQL Server Express – shared database for several PCs$\r$\n$\r$\nClick Next to choose."
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXENAME}"
!define MUI_FINISHPAGE_RUN_TEXT "Start BSL Project Control"
!define MUI_FINISHPAGE_SHOWREADME "$INSTDIR\README.md"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "Open the user guide (README)"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!define MUI_PAGE_CUSTOMFUNCTION_PRE SqlLicensePre
!define MUI_LICENSEPAGE_CHECKBOX
!define MUI_PAGE_HEADER_TEXT "Microsoft SQL Server 2022 Express"
!define MUI_PAGE_HEADER_SUBTEXT "Licence terms for the SQL Server components you selected"
!insertmacro MUI_PAGE_LICENSE "sql-license.txt"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

; ---------------------------------------------------------------- app
Section "BSL Project Control (required)" SecApp
  SectionIn RO
  SetShellVarContext all               ; shortcuts for every user of the PC
  SetOutPath "$INSTDIR"
  File "${SRC}\${EXENAME}"
  File "app.ico"
  File "..\README.md"
  File "sql-setup.ps1"
  SetOutPath "$INSTDIR\database"
  File "..\database\*.sql"
  SetOutPath "$INSTDIR\fonts"          ; optional: drop .ttf files here if a PC has no Arial (PDF output)
  SetOutPath "$INSTDIR"

  CreateDirectory "$SMPROGRAMS\${APPNAME}"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\${EXENAME}" "" "$INSTDIR\app.ico"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\User guide.lnk" "$INSTDIR\README.md"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\Uninstall.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${EXENAME}" "" "$INSTDIR\app.ico"

  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKLM "Software\BSLProjectControl" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "${REGKEY}" "DisplayName" "${APPNAME}"
  WriteRegStr HKLM "${REGKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "${REGKEY}" "Publisher" "BSL BF#3 Revamp"
  WriteRegStr HKLM "${REGKEY}" "DisplayIcon" "$INSTDIR\app.ico"
  WriteRegStr HKLM "${REGKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${REGKEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKLM "${REGKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${REGKEY}" "NoRepair" 1
SectionEnd

; ---------------------------------------------------------------- SQL Server
SectionGroup /e "Microsoft SQL Server 2022 Express" SecSqlGroup

Section "LocalDB – database on this PC" SecLocalDB
  AddSize 250000
  DetailPrint "Installing SQL Server Express LocalDB ..."
  InitPluginsDir
  StrCpy $0 ""
  !ifdef BUNDLE_LOCALDB
    SetOutPath "$PLUGINSDIR\sql"
    File "redist\SqlLocalDB.msi"
    StrCpy $0 "$PLUGINSDIR\sql\SqlLocalDB.msi"
    SetOutPath "$INSTDIR"
  !endif
  nsExec::ExecToLog 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\sql-setup.ps1" -Mode LocalDB -Media "$0" -Log "$INSTDIR\sql-setup.log"'
  Pop $1
  ${If} $1 != "0"
    StrCpy $SqlFailed 1
    MessageBox MB_ICONEXCLAMATION|MB_OK "SQL Server LocalDB could not be installed (code $1).$\r$\nDetails: $INSTDIR\sql-setup.log$\r$\n$\r$\nThe program is installed; you can run sql-setup.ps1 later or use another SQL Server." /SD IDOK
  ${EndIf}
SectionEnd

Section /o "SQL Server Express – shared by several PCs" SecExpress
  AddSize 1100000
  DetailPrint "Installing SQL Server Express (instance SQLEXPRESS) – this can take 5 to 15 minutes ..."
  InitPluginsDir
  StrCpy $0 ""
  !ifdef BUNDLE_EXPRESS
    SetOutPath "$PLUGINSDIR\sql"
    File "redist\SQLEXPR_x64_ENU.exe"
    StrCpy $0 "$PLUGINSDIR\sql\SQLEXPR_x64_ENU.exe"
    SetOutPath "$INSTDIR"
  !endif
  nsExec::ExecToLog 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\sql-setup.ps1" -Mode Express -Media "$0" -Database ${DBNAME} -Log "$INSTDIR\sql-setup.log"'
  Pop $1
  ${If} $1 == "0"
    StrCpy $HasExpress 1
  ${Else}
    StrCpy $SqlFailed 1
    MessageBox MB_ICONEXCLAMATION|MB_OK "SQL Server Express could not be installed (code $1).$\r$\nDetails: $INSTDIR\sql-setup.log" /SD IDOK
  ${EndIf}
SectionEnd

SectionGroupEnd

; ---------------------------------------------------------------- connection the app uses on first start
Section "-WriteDefaults"
  ; JSON: backslashes are doubled
  ${If} $HasExpress == 1
    StrCpy $2 "Server=.\\SQLEXPRESS;Database=${DBNAME};Trusted_Connection=True;TrustServerCertificate=True"
  ${Else}
    StrCpy $2 "Server=(localdb)\\MSSQLLocalDB;Database=${DBNAME};Trusted_Connection=True;TrustServerCertificate=True"
  ${EndIf}
  FileOpen $3 "$INSTDIR\defaults.json" w
  FileWrite $3 '{$\r$\n  "ConnectionString": "$2",$\r$\n  "CompanyName": "BSL BF#3 Revamp - Project Control",$\r$\n  "HighDpiMode": "SystemAware"$\r$\n}$\r$\n'
  FileClose $3
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecApp} "The BSL Project Control program (self-contained, .NET is not needed)."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecSqlGroup} "Free Microsoft database server used by the program."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecLocalDB} "Recommended for ONE PC. Small, no Windows service; the database is created on first start."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecExpress} "For SEVERAL PCs: installs the service .\SQLEXPRESS on this PC (server), opens the firewall and creates the database. Other PCs install the program only and connect to THIS-PC\SQLEXPRESS."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ---------------------------------------------------------------- init: detect what is already installed
Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "BSL Project Control needs 64-bit Windows." /SD IDOK
    Abort
  ${EndIf}
  SetRegView 64
  StrCpy $HasExpress 0
  StrCpy $SqlFailed 0

  ; LocalDB already installed? -> nothing to do
  EnumRegKey $0 HKLM "SOFTWARE\Microsoft\Microsoft SQL Server Local DB\Installed Versions" 0
  ${If} $0 != ""
    !insertmacro UnselectSection ${SecLocalDB}
    SectionSetText ${SecLocalDB} "LocalDB – already installed ($0)"
  ${EndIf}

  ; instance SQLEXPRESS already installed? -> use it
  ReadRegStr $1 HKLM "SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL" "SQLEXPRESS"
  ${If} $1 != ""
    StrCpy $HasExpress 1
    !insertmacro UnselectSection ${SecExpress}
    SectionSetText ${SecExpress} "SQL Server Express – already installed (.\SQLEXPRESS)"
    !insertmacro UnselectSection ${SecLocalDB}
  ${EndIf}

  ; silent-install switches
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/EXPRESS" $R1
  ${IfNot} ${Errors}
    ${If} $HasExpress != 1
      !insertmacro SelectSection ${SecExpress}
    ${EndIf}
    !insertmacro UnselectSection ${SecLocalDB}
  ${EndIf}
  ClearErrors
  ${GetOptions} $R0 "/NOSQL" $R1
  ${IfNot} ${Errors}
    !insertmacro UnselectSection ${SecLocalDB}
    !insertmacro UnselectSection ${SecExpress}
  ${EndIf}
FunctionEnd

; licence page only when a SQL Server component is selected
Function SqlLicensePre
  ${IfNot} ${SectionIsSelected} ${SecLocalDB}
  ${AndIfNot} ${SectionIsSelected} ${SecExpress}
    Abort
  ${EndIf}
FunctionEnd

; ---------------------------------------------------------------- uninstall
Section "Uninstall"
  SetShellVarContext all
  Delete "$DESKTOP\${APPNAME}.lnk"
  RMDir /r "$SMPROGRAMS\${APPNAME}"
  Delete "$INSTDIR\${EXENAME}"
  Delete "$INSTDIR\app.ico"
  Delete "$INSTDIR\README.md"
  Delete "$INSTDIR\sql-setup.ps1"
  Delete "$INSTDIR\sql-setup.log"
  Delete "$INSTDIR\defaults.json"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir /r "$INSTDIR\database"
  RMDir "$INSTDIR\fonts"
  RMDir "$INSTDIR"
  SetRegView 64
  DeleteRegKey HKLM "${REGKEY}"
  DeleteRegKey HKLM "Software\BSLProjectControl"
  ; SQL Server, the database and %AppData%\BSLProjectControl (settings, logo) are kept on purpose:
  ; remove SQL Server from Windows "Apps" if it is no longer needed.
SectionEnd
