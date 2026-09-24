@echo off
chcp 65001 >nul
cd /d "%~dp0"
if exist "USB急救工具.exe" (
  start "" "%~dp0USB急救工具.exe" --autofix
) else if exist "..\发布\USB急救工具.exe" (
  start "" "%~dp0..\发布\USB急救工具.exe" --autofix
) else (
  echo 未找到 USB急救工具.exe
  pause
)
