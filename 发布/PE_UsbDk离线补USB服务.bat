@echo off
:: PE 离线：UsbDk 专项 — 同时改 ControlSet001 + 002
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul
title PE UsbDk 离线补 USB（双 ControlSet）

echo.
echo  === UsbDk / hrdevmon 离线补服务（双 ControlSet）===
echo  重启 PE 后立刻运行，不要先开 regedit / Dism++
echo.

set "WIN="
for %%d in (C D E F G H I) do (
  if exist "%%d:\Windows\System32\config\SYSTEM" if exist "%%d:\Windows\System32\config\SOFTWARE" set "WIN=%%d:"
)
if not defined WIN ( echo 未找到系统盘 & pause & exit /b 1 )
echo 系统盘: %WIN%
pause

reg load "HKLM\OFFLINE_SYS" "%WIN%\Windows\System32\config\SYSTEM"
if errorlevel 1 (
  echo SYSTEM 被占用。重启 PE 后立刻再跑。仍占用 → 用远程自启或原地升级。
  pause & exit /b 1
)
reg load "HKLM\OFFLINE_SOFT" "%WIN%\Windows\System32\config\SOFTWARE" >nul 2>&1

for %%c in (ControlSet001 ControlSet002 ControlSet003) do (
  reg query "HKLM\OFFLINE_SYS\%%c\Services" >nul 2>&1
  if not errorlevel 1 (
    echo.
    echo ---- 修补 %%c ----
    for %%s in (UsbDk usbdk USBDk usbdkmon UsbDkHelper hrdevmon HRDevMon UsbDkRuntime) do (
      reg delete "HKLM\OFFLINE_SYS\%%c\Services\%%s" /f >nul 2>&1
    )
    reg add "HKLM\OFFLINE_SYS\%%c\Services\usbxhci" /v Start /t REG_DWORD /d 0 /f >nul
    reg add "HKLM\OFFLINE_SYS\%%c\Services\USBXHCI" /v Start /t REG_DWORD /d 0 /f >nul
    reg add "HKLM\OFFLINE_SYS\%%c\Services\usbhub"  /v Start /t REG_DWORD /d 1 /f >nul
    reg add "HKLM\OFFLINE_SYS\%%c\Services\usbhub3" /v Start /t REG_DWORD /d 1 /f >nul
    reg add "HKLM\OFFLINE_SYS\%%c\Services\usbccgp" /v Start /t REG_DWORD /d 1 /f >nul
    reg add "HKLM\OFFLINE_SYS\%%c\Services\USBSTOR" /v Start /t REG_DWORD /d 3 /f >nul
    reg add "HKLM\OFFLINE_SYS\%%c\Services\PlugPlay" /v Start /t REG_DWORD /d 2 /f >nul
    reg add "HKLM\OFFLINE_SYS\%%c\Services\HidUsb" /v Start /t REG_DWORD /d 3 /f >nul
    reg add "HKLM\OFFLINE_SYS\%%c\Services\mouhid" /v Start /t REG_DWORD /d 3 /f >nul
    reg add "HKLM\OFFLINE_SYS\%%c\Services\kbdhid" /v Start /t REG_DWORD /d 3 /f >nul
    reg add "HKLM\OFFLINE_SYS\%%c\Services\kbdclass" /v Start /t REG_DWORD /d 3 /f >nul
    reg add "HKLM\OFFLINE_SYS\%%c\Services\mouclass" /v Start /t REG_DWORD /d 3 /f >nul

    for %%g in (
      {36fc9e60-c465-11cf-8056-444553540000}
      {745a17a0-74d3-11d0-b6fe-00a0c90f57da}
      {4d36e96b-e325-11ce-bfc1-444553540000}
      {4d36e96f-e325-11ce-bfc1-444553540000}
      {88BAE032-5A81-49f0-BC3D-A4FF138216D6}
    ) do (
      reg delete "HKLM\OFFLINE_SYS\%%c\Control\Class\%%g" /v UpperFilters /f >nul 2>&1
      reg delete "HKLM\OFFLINE_SYS\%%c\Control\Class\%%g" /v LowerFilters /f >nul 2>&1
    )
    reg add "HKLM\OFFLINE_SYS\%%c\Control\Class\{4d36e96b-e325-11ce-bfc1-444553540000}" /v UpperFilters /t REG_MULTI_SZ /d "kbdclass" /f >nul
    reg add "HKLM\OFFLINE_SYS\%%c\Control\Class\{4d36e96f-e325-11ce-bfc1-444553540000}" /v UpperFilters /t REG_MULTI_SZ /d "mouclass" /f >nul
    reg delete "HKLM\OFFLINE_SYS\%%c\Control\Keyboard Layout" /v "Scancode Map" /f >nul 2>&1
    echo %%c 完成
  )
)

reg delete "HKLM\OFFLINE_SOFT\Policies\Microsoft\Windows\DeviceInstall\Restrictions" /f >nul 2>&1

echo.
echo 卸载配置单元...
reg unload "HKLM\OFFLINE_SOFT" >nul 2>&1
reg unload "HKLM\OFFLINE_SYS"
if errorlevel 1 (
  echo unload 失败！请手动: reg unload HKLM\OFFLINE_SYS
  pause & exit /b 1
)

echo.
echo 完成。拔 U 盘重启进 Windows 试键鼠。
pause
