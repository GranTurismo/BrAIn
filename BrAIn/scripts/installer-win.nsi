; BrAIn Windows installer (NSIS 3.x — builds cross-platform via makensis).
; Produces dist/BrAIn-Setup-x64.exe from the win-x64 publish output.
;
;   dotnet publish BrAIn.csproj -c Release -r win-x64 --self-contained true ^
;     -p:PublishSingleFile=true -o dist/win-publish
;   makensis scripts/installer-win.nsi

!define APPNAME   "BrAIn"
!ifndef VERSION
  !define VERSION "1.0.0"
!endif
!define PUBLISHER "BrAIn"
!define EXE       "BrAIn.exe"
!define REG_UNINST "Software\Microsoft\Windows\CurrentVersion\Uninstall\BrAIn"
; Microsoft Edge WebView2 Runtime (Evergreen) product id.
!define WV2GUID   "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"

Unicode true
SetCompressor /SOLID lzma

Name "${APPNAME} ${VERSION}"
OutFile "..\dist\BrAIn-Setup-${VERSION}-x64.exe"
InstallDir "$PROGRAMFILES64\${APPNAME}"
InstallDirRegKey HKLM "Software\${APPNAME}" "InstallDir"
RequestExecutionLevel admin

!include "MUI2.nsh"
!define MUI_ABORTWARNING
!define MUI_ICON   "..\dist\win-publish\Assets\icon.ico"
!define MUI_UNICON "..\dist\win-publish\Assets\icon.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Launch ${APPNAME}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

Section "Install"
  SetOutPath "$INSTDIR"
  ; Publish output; skip debug symbols.
  File /r /x "*.pdb" "..\dist\win-publish\*.*"

  WriteRegStr HKLM "Software\${APPNAME}" "InstallDir" "$INSTDIR"

  ; Add/Remove Programs entry.
  WriteRegStr HKLM "${REG_UNINST}" "DisplayName"     "${APPNAME}"
  WriteRegStr HKLM "${REG_UNINST}" "DisplayVersion"  "${VERSION}"
  WriteRegStr HKLM "${REG_UNINST}" "Publisher"       "${PUBLISHER}"
  WriteRegStr HKLM "${REG_UNINST}" "DisplayIcon"     "$INSTDIR\${EXE}"
  WriteRegStr HKLM "${REG_UNINST}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${REG_UNINST}" "UninstallString" "$INSTDIR\Uninstall.exe"
  WriteRegDWORD HKLM "${REG_UNINST}" "NoModify" 1
  WriteRegDWORD HKLM "${REG_UNINST}" "NoRepair" 1

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\${APPNAME}"
  CreateShortcut  "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk"     "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0
  CreateShortcut  "$SMPROGRAMS\${APPNAME}\${APPNAME} tab self-test.lnk" "$INSTDIR\${EXE}" "--selftest-tabs" "$INSTDIR\${EXE}" 0
  CreateShortcut  "$SMPROGRAMS\${APPNAME}\Uninstall ${APPNAME}.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut  "$DESKTOP\${APPNAME}.lnk"                   "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0

  ; WebView2 runtime — Photino renders through it. Present on most Win10/11
  ; machines; install the Evergreen bootstrapper only when the registry
  ; doesn't list the runtime for either machine or user scope.
  Call EnsureWebView2
SectionEnd

Function EnsureWebView2
  SetRegView 64
  ClearErrors
  ReadRegStr $0 HKLM "SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\${WV2GUID}" "pv"
  IfErrors 0 done
  ReadRegStr $0 HKLM "SOFTWARE\Microsoft\EdgeUpdate\Clients\${WV2GUID}" "pv"
  IfErrors 0 done
  ReadRegStr $0 HKCU "SOFTWARE\Microsoft\EdgeUpdate\Clients\${WV2GUID}" "pv"
  IfErrors 0 done

  DetailPrint "WebView2 runtime not found — installing..."
  SetOutPath "$PLUGINSDIR"
  File "..\installer\MicrosoftEdgeWebview2Setup.exe"
  ExecWait '"$PLUGINSDIR\MicrosoftEdgeWebview2Setup.exe" /silent /install' $1
  DetailPrint "WebView2 bootstrapper exited with $1"
done:
FunctionEnd

Section "Uninstall"
  ; Kill a running instance so the exe/dlls aren't locked.
  ExecWait 'taskkill /F /IM ${EXE}' $0
  Sleep 500

  Delete "$DESKTOP\${APPNAME}.lnk"
  RMDir /r "$SMPROGRAMS\${APPNAME}"
  RMDir /r "$INSTDIR"

  DeleteRegKey HKLM "${REG_UNINST}"
  DeleteRegKey HKLM "Software\${APPNAME}"
  ; User profile data (%APPDATA%\BrAIn) is intentionally left intact.
SectionEnd
