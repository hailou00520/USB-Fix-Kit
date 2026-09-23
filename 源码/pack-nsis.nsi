; USB Fix Kit — NSIS solid LZMA one-file release (Nullsoft, same style as BoDian etc.)
; Build: stage files to C:\nsis-pack\files then makensis this script, or use 打包发布.bat

Unicode true
SetCompressor /SOLID lzma
SetCompressorDictSize 64
CRCCheck on
SetDatablockOptimize on

!ifndef STAGE
  !define STAGE "C:\nsis-pack"
!endif

Name "USB 急救工具"
OutFile "${STAGE}\USB急救工具_发布.exe"
Icon "${STAGE}\app.ico"
UninstallIcon "${STAGE}\app.ico"
RequestExecutionLevel admin
ManifestDPIAware true

SilentInstall normal
ShowInstDetails show
AutoCloseWindow true
InstallDir "$TEMP\USBFixKit"
InstallDirRegKey HKCU "Software\USBFixKit" "InstDir"

!include "MUI2.nsh"
!define MUI_ABORTWARNING
!define MUI_ICON "${STAGE}\app.ico"
!define MUI_UNICON "${STAGE}\app.ico"

!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

Section "Install"
  RMDir /r "$INSTDIR"
  SetOutPath "$INSTDIR"
  File /r "${STAGE}\files\*.*"
  WriteRegStr HKCU "Software\USBFixKit" "InstDir" "$INSTDIR"
  Exec "$INSTDIR\USB急救工具.exe"
SectionEnd
