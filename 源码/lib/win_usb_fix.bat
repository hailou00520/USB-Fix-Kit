@echo off
:: 包装器：调用全面体检 PowerShell（开机自启 / 手动均可）
setlocal
chcp 65001 >nul

set "DIR=%~dp0"
set "PS1=%DIR%win_usb_fullcheck.ps1"
set "LOG=%SystemDrive%\usb_fix_log.txt"

if not exist "%PS1%" (
    echo [错误] 找不到 win_usb_fullcheck.ps1 >> "%LOG%"
    exit /b 1
)

if /i "%~1"=="/silent" (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%PS1%" -Silent
) else (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PS1%"
)

exit /b %ERRORLEVEL%
