; Windows installer for Draft Notes (NSIS 3).
;   makensis -DSRC=<folder with draft_notes.exe> -DVERSION=1.0.0 -DOUT=<setup.exe> draft_notes.nsi
; Installs for the current user only, so it needs no administrator rights.
; Notes are saved in Documents\draft_notes and are kept on uninstall.

Unicode true
!include "MUI2.nsh"

!ifndef VERSION
  !define VERSION "1.0.0"
!endif
!ifndef OUT
  !define OUT "DraftNotes-Setup.exe"
!endif
!define APP "Draft Notes"
!define EXE "draft_notes.exe"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\DraftNotes"

Name "${APP}"
OutFile "${OUT}"
InstallDir "$LOCALAPPDATA\Programs\${APP}"
InstallDirRegKey HKCU "${UNINST_KEY}" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
BrandingText "${APP} ${VERSION}"

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APP}"
VIAddVersionKey "FileDescription" "${APP} installer"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "parthag"

!define MUI_ICON "..\windows\runner\resources\app_icon.ico"
!define MUI_UNICON "..\windows\runner\resources\app_icon.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Open ${APP}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Section "Install"
  ; Close a running copy so its files can be replaced.
  nsExec::Exec 'taskkill /F /IM ${EXE}'
  SetOutPath "$INSTDIR"
  File /r "${SRC}\*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortcut "$SMPROGRAMS\${APP}.lnk" "$INSTDIR\${EXE}"
  CreateShortcut "$DESKTOP\${APP}.lnk" "$INSTDIR\${EXE}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName" "${APP}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher" "parthag"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${EXE}"
  WriteRegStr HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  nsExec::Exec 'taskkill /F /IM ${EXE}'
  Delete "$SMPROGRAMS\${APP}.lnk"
  Delete "$DESKTOP\${APP}.lnk"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKCU "${UNINST_KEY}"
SectionEnd
