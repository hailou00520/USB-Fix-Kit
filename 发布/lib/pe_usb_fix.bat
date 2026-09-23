@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul

set "WIN=%~1"
if not defined WIN (
    for %%d in (C D E F G H I) do (
        if exist "%%d:\Windows\System32\config\SYSTEM" set "WIN=%%d:"
    )
)
if not defined WIN (
    echo [错误] 未找到 Windows 系统盘
    exit /b 1
)

set "LOG=%WIN%\usb_fix_log.txt"

echo.
echo ── [USB 离线修复] 系统盘: %WIN% ──
echo.

echo ===== USB 离线修复 %date% %time% ===== >> "%LOG%"

:: 加载离线注册表
echo [1/5] 加载离线注册表...
reg load "HKLM\PEOFFLINE_SOFT" "%WIN%\Windows\System32\config\SOFTWARE" >nul 2>&1
if errorlevel 1 ( echo [错误] 无法加载 SOFTWARE & exit /b 1 )
reg load "HKLM\PEOFFLINE_SYS" "%WIN%\Windows\System32\config\SYSTEM" >nul 2>&1
if errorlevel 1 (
    reg unload "HKLM\PEOFFLINE_SOFT" >nul 2>&1
    echo [错误] 无法加载 SYSTEM
    exit /b 1
)
echo       完成

:: 修复 USB/HID 服务
echo [2/5] 检查 USB / HID 服务...
for %%s in (USBXHCI usbhub USBHUB3 USBSTOR HidUsb mouhid kbdhid HidClass) do (
    reg query "HKLM\PEOFFLINE_SYS\ControlSet001\Services\%%s" /v Start >nul 2>&1
    if not errorlevel 1 (
        for /f "tokens=3" %%v in ('reg query "HKLM\PEOFFLINE_SYS\ControlSet001\Services\%%s" /v Start 2^>nul ^| findstr Start') do (
            if "%%v"=="0x4" (
                echo       修复 %%s : 禁用 -^> 手动
                reg add "HKLM\PEOFFLINE_SYS\ControlSet001\Services\%%s" /v Start /t REG_DWORD /d 3 /f >nul
                echo 服务修复 %%s >> "%LOG%"
            )
        )
    )
)

:: 清除安装限制
echo [3/5] 清除设备安装限制...
reg query "HKLM\PEOFFLINE_SOFT\Policies\Microsoft\Windows\DeviceInstall\Restrictions" >nul 2>&1
if not errorlevel 1 (
    reg delete "HKLM\PEOFFLINE_SOFT\Policies\Microsoft\Windows\DeviceInstall\Restrictions" /f >nul 2>&1
    echo 已删除 DeviceInstall\Restrictions >> "%LOG%"
    echo       已清除 Restrictions
) else (
    echo       无限制策略
)

:: 清除过滤驱动
echo [4/5] 检查 USB / 键鼠过滤驱动...
for %%c in (
    "{36fc9e60-c465-11cf-8056-444553540000}"
    "{4d36e96f-e325-11ce-bfc1-444553540000}"
    "{4d36e96b-e325-11ce-bfc1-444553540000}"
    "{745a17a0-74d3-11d0-b6fe-00a0c90f57da}"
) do (
    reg query "HKLM\PEOFFLINE_SYS\ControlSet001\Control\Class\%%c" /v UpperFilters >nul 2>&1
    if not errorlevel 1 (
        reg delete "HKLM\PEOFFLINE_SYS\ControlSet001\Control\Class\%%c" /v UpperFilters /f >nul 2>&1
        echo 删除 UpperFilters %%c >> "%LOG%"
        echo       已清除 UpperFilters
    )
    reg query "HKLM\PEOFFLINE_SYS\ControlSet001\Control\Class\%%c" /v LowerFilters >nul 2>&1
    if not errorlevel 1 (
        reg delete "HKLM\PEOFFLINE_SYS\ControlSet001\Control\Class\%%c" /v LowerFilters /f >nul 2>&1
        echo 删除 LowerFilters %%c >> "%LOG%"
        echo       已清除 LowerFilters
    )
)

reg unload "HKLM\PEOFFLINE_SOFT" >nul 2>&1
reg unload "HKLM\PEOFFLINE_SYS" >nul 2>&1

:: DISM + SFC
echo [5/5] 离线修复系统文件（需几分钟）...
Dism /Image:%WIN%\ /Cleanup-Image /RestoreHealth >> "%LOG%" 2>&1
Sfc /ScanNow /OffBootDir=%WIN%\ /OffWinDir=%WIN%\Windows >> "%LOG%" 2>&1
Dism /Image:%WIN%\ /Get-Drivers /Format:Table > "%WIN%\usb_oem_drivers.txt" 2>&1

echo.
echo [USB 离线修复] 完成，日志: %LOG%
exit /b 0
