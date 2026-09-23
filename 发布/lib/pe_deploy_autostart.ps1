# PE 离线部署：将 USB 自检脚本写入目标系统 + 创建开机计划任务
param(
    [Parameter(Mandatory=$true)][string]$WinDrive,
    [Parameter(Mandatory=$true)][string]$ToolDir
)

$ErrorActionPreference = 'Stop'
$LogFile = Join-Path $WinDrive 'usb_fix_log.txt'

function Write-Log($msg) {
    $line = "[部署自检] $msg"
    Write-Host $line
    Add-Content -Path $LogFile -Value $line -Encoding UTF8
}

Write-Host ""
Write-Host "── [部署开机自检] 系统盘: $WinDrive ──"
Write-Host ""

# PE 里系统盘可能是 D:，但 Windows 启动后认 C:，服务路径必须用 C:\Windows
$destDir  = Join-Path $WinDrive 'Windows\USBFix'
$fixBat   = Join-Path $destDir 'win_usb_fix.bat'
$bootFix  = 'C:\Windows\USBFix\win_usb_fix.bat'
$bootWrap = 'C:\Windows\USBFix\boot_wrapper.bat'
$srcBat   = Join-Path $ToolDir 'lib\win_usb_fix.bat'

# 1. 复制修复脚本到系统目录
if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
Copy-Item -Path $srcBat -Destination $fixBat -Force
Write-Log "已复制自检脚本到 $fixBat"

# 2. 创建开机启动包装脚本（先等待 USB 栈就绪，再执行修复，最后自禁用服务）
$wrapperBat = Join-Path $destDir 'boot_wrapper.bat'
$wrapperContent = @"
@echo off
timeout /t 30 /nobreak >nul
call "$bootFix" /silent
sc config USBFixBoot start= disabled >nul 2>&1
exit
"@
$wrapperContent | Out-File -FilePath $wrapperBat -Encoding ASCII -Force
Write-Log "已创建启动包装脚本"

# 3. 离线注册 Windows 服务（开机自动运行，SYSTEM 权限，无需登录）
reg load "HKLM\PEOFFLINE_SOFT" "$WinDrive\Windows\System32\config\SOFTWARE" | Out-Null
reg load "HKLM\PEOFFLINE_SYS"  "$WinDrive\Windows\System32\config\SYSTEM"  | Out-Null

try {
    $svcKey = 'HKLM:\PEOFFLINE_SYS\ControlSet001\Services\USBFixBoot'
    if (-not (Test-Path $svcKey)) { New-Item -Path $svcKey -Force | Out-Null }

    $imagePath = "\??\C:\Windows\System32\cmd.exe /c `"$bootWrap`""
    Set-ItemProperty -Path $svcKey -Name 'Type'         -Value 16  -Type DWord
    Set-ItemProperty -Path $svcKey -Name 'Start'        -Value 2   -Type DWord
    Set-ItemProperty -Path $svcKey -Name 'ErrorControl' -Value 1   -Type DWord
    Set-ItemProperty -Path $svcKey -Name 'ImagePath'    -Value $imagePath -Type ExpandString
    Set-ItemProperty -Path $svcKey -Name 'DisplayName'  -Value 'USB Fix Boot Check' -Type String
    Set-ItemProperty -Path $svcKey -Name 'ObjectName'   -Value 'LocalSystem' -Type String
    Set-ItemProperty -Path $svcKey -Name 'Description'  -Value 'USB boot self-check and repair' -Type String

    Write-Log "已注册开机服务 USBFixBoot（SYSTEM 权限，无需登录）"

    # RunOnce 备份（自动登录后再跑一次）
    $runOnce = 'HKLM:\PEOFFLINE_SOFT\Microsoft\Windows\CurrentVersion\RunOnce'
    Set-ItemProperty -Path $runOnce -Name 'USBFixCheck' -Value "cmd /c `"$bootFix`" /silent" -Type String
    Write-Log "RunOnce 备份已添加（登录时再跑一次）"
}
finally {
    reg unload "HKLM\PEOFFLINE_SOFT" 2>$null | Out-Null
    reg unload "HKLM\PEOFFLINE_SYS"  2>$null | Out-Null
}

Write-Host ""
Write-Host "[部署完成]"
Write-Host "  开机后自动运行 USB 自检（Windows 服务，无需登录）"
Write-Host "  配合自动登录，登录后还会再跑一次"
Write-Host "  修复完成后服务会自动禁用，也可手动删除:"
Write-Host "  $destDir"
