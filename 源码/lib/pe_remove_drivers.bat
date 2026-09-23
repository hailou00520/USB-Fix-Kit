@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul

set "WIN=%~1"
if not defined WIN (
    for %%d in (C D E F G H I) do (
        if exist "%%d:\Windows\System32\config\SYSTEM" set "WIN=%%d:"
    )
)
if not defined WIN ( echo [错误] 未找到系统盘 & exit /b 1 )

echo.
echo ── [删除 USB 第三方驱动] 系统盘: %WIN% ──
echo.

set COUNT=0
Dism /Image:%WIN%\ /Get-Drivers /Format:Table > "%TEMP%\drv_table.txt" 2>&1

for /f "skip=1 tokens=*" %%l in ('type "%TEMP%\drv_table.txt"') do (
    echo %%l | findstr /i /c:"usb" /c:"xhci" /c:"hub" /c:"vivo" /c:"mi " /c:"android" /c:"qualcomm" /c:"adb" >nul
    if not errorlevel 1 (
        for /f "tokens=1" %%f in ("%%l") do (
            echo [删除] %%f
            Dism /Image:%WIN%\ /Remove-Driver /Driver:%%f /ForceUnsigned >nul 2>&1
            if not errorlevel 1 set /a COUNT+=1
        )
    )
)

echo.
echo 共删除 %COUNT% 个驱动，请重启测试。
exit /b 0
