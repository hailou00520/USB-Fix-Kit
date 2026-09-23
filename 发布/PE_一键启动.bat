@echo off
chcp 65001 >nul
cd /d "%~dp0"
title USB急救工具

if exist "%~dp0USB急救工具.exe" (
  start "" "%~dp0USB急救工具.exe"
  exit /b 0
)
echo 找不到 USB急救工具.exe
pause
