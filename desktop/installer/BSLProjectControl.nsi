; =====================================================================
;  BSL Project Control – Windows installer (NSIS 3)
;  Build:  makensis -DVERSION=1.1.0 -DSRC=..\publish\release BSLProjectControl.nsi
;  Installs the self-contained app (no .NET needed), Start-menu and desktop
;  shortcuts, the SQL scripts and the README, and an uninstaller.
; =====================================================================
Unicode true
Target amd64-unicode          ; 64-bit installer – the app itself is x64 only
!include "MUI2.nsh"

!ifndef VERSION
  !define VERSION "1.1.0"
!endif
!ifndef SRC
  !define SRC "..\publish\release"
!endif
!define APPNAME   "BSL Project Control"
!define EXENAME   "BSLProjectControl.exe"
!define REGKEY    "Software\Microsoft\Windows\CurrentVersion\Uninstall\BSLProjectControl"

Name "${APPNAME} ${VERSION}"
OutFile "BSLProjectControl-Setup-${VERSION}.exe"
InstallDir "$PROGRAMFILES64\BSL Project Control"
InstallDirRegKey HKLM "Software\BSLProjectControl" "InstallDir"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
BrandingText "${APPNAME} ${VERSION}"

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APPNAME}"
VIAddVersionKey "FileDescription" "${APPNAME} installer"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "BSL BF#3 Revamp"

!define MUI_ICON "app.ico"
!define MUI_UNICON "app.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "BSL PROJECT CONTROL ${VERSION}"
!define MUI_WELCOMEPAGE_TEXT "Drawings, BOM, contractors, work orders, BOQ, RA bills, notes and drawing uploads.$\r$\n$\r$\nThe program needs Microsoft SQL Server 2017 or later (LocalDB, Express or a full server). On first start it asks for the server and creates the database by itself.$\r$\n$\r$\nClick Next to continue."
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXENAME}"
!define MUI_FINISHPAGE_RUN_TEXT "Start BSL Project Control"
!define MUI_FINISHPAGE_SHOWREADME "$INSTDIR\README.md"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "Open the user guide (README)"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED
!define MUI_FINISHPAGE_LINK "No SQL Server yet? Download SQL Server Express (free)"
!define MUI_FINISHPAGE_LINK_LOCATION "https://www.microsoft.com/sql-server/sql-server-downloads"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Section "Install"
  SetShellVarContext all               ; shortcuts for every user of the PC
  SetOutPath "$INSTDIR"
  File "${SRC}\${EXENAME}"
  File "app.ico"
  File "..\README.md"
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

Section "Uninstall"
  SetShellVarContext all
  Delete "$DESKTOP\${APPNAME}.lnk"
  RMDir /r "$SMPROGRAMS\${APPNAME}"
  Delete "$INSTDIR\${EXENAME}"
  Delete "$INSTDIR\app.ico"
  Delete "$INSTDIR\README.md"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir /r "$INSTDIR\database"
  RMDir "$INSTDIR\fonts"
  RMDir "$INSTDIR"
  DeleteRegKey HKLM "${REGKEY}"
  DeleteRegKey HKLM "Software\BSLProjectControl"
  ; the database and %AppData%\BSLProjectControl (settings, logo) are kept on purpose
SectionEnd
