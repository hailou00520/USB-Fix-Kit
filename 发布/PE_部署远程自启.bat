@echo off
:: 仅部署远程软件自启（不把 exe 放进 Startup，避免安全警告）
setlocal EnableExtensions
chcp 65001 >nul
title 部署远程软件自启

echo.
echo  独立功能：复制绿版 → 清除 Zone 标记 → 只用 bat 自启
echo  请把绿色版放到: %~dp0Remote\
echo.

set "WIN="
for %%d in (C D E F G H I) do if exist "%%d:\Windows\System32\config\SOFTWARE" set "WIN=%%d:"
if not defined WIN ( echo 无系统盘 & pause & exit /b 1 )

dir /s /b "%~dp0Remote\*.exe" >nul 2>&1 || (
  echo 请把 ToDesk/向日葵绿色版放进 Remote 文件夹
  pause & exit /b 1
)

echo 建议直接运行「USB急救工具.exe」→「部署远程软件自启」
echo 本脚本做同等基础处理…
mkdir "%WIN%\Tools\RemoteFix" 2>nul
xcopy /E /I /Y "%~dp0Remote\*" "%WIN%\Tools\RemoteFix\" >nul

:: 清除下载标记（去掉安全警告）
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem '%WIN%\Tools\RemoteFix' -Recurse -Force -EA SilentlyContinue | Unblock-File -EA SilentlyContinue" >nul 2>&1

:: 删掉 Startup 里的远程 exe（弹窗元凶）
del /f /q "%WIN%\ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp\ToDesk*.exe" 2>nul
del /f /q "%WIN%\ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp\*Sunlogin*.exe" 2>nul

set "EXE="
for %%e in (ToDesk_Lite.exe ToDesk.exe SunloginClient.exe Sunlogin.exe) do if exist "%WIN%\Tools\RemoteFix\%%e" set "EXE=C:\Tools\RemoteFix\%%e"
if not defined EXE for /r "%WIN%\Tools\RemoteFix" %%f in (*.exe) do if not defined EXE set "EXE=%%f"

mkdir "%WIN%\Windows\USBFix" 2>nul
(
  echo @echo off
  echo powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem 'C:\Tools\RemoteFix' -Recurse -Force -EA SilentlyContinue ^| Unblock-File -EA SilentlyContinue" ^>nul 2^>^&1
  echo start "" "%EXE%"
) > "%WIN%\Windows\USBFix\start_remote.bat"

mkdir "%WIN%\ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp" 2>nul
(
  echo @echo off
  echo call "C:\Windows\USBFix\start_remote.bat"
) > "%WIN%\ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp\USB远程自启.bat"

reg load HKLM\OFFLINE_SOFT "%WIN%\Windows\System32\config\SOFTWARE" >nul 2>&1
reg add "HKLM\OFFLINE_SOFT\Microsoft\Windows\CurrentVersion\Run" /v USBRemoteAutostart /t REG_SZ /d "C:\Windows\USBFix\start_remote.bat" /f >nul
reg add "HKLM\OFFLINE_SOFT\Microsoft\Windows\CurrentVersion\Policies\Attachments" /v SaveZoneInformation /t REG_DWORD /d 2 /f >nul
reg unload HKLM\OFFLINE_SOFT >nul 2>&1

echo 完成。Startup 只有 bat，不应再弹安全警告。
pause
