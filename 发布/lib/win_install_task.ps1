# 在正常 Windows 里安装开机自检计划任务
$ErrorActionPreference = 'Stop'

$fixBat = Join-Path $env:SystemRoot 'USBFix\win_usb_fix.bat'
$srcBat = Join-Path (Split-Path $PSScriptRoot -Parent) 'lib\win_usb_fix.bat'
$destDir = Join-Path $env:SystemRoot 'USBFix'

if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
Copy-Item -Path $srcBat -Destination $fixBat -Force

$action  = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c `"$fixBat`" /silent"
$trigger = New-ScheduledTaskTrigger -AtStartup -RandomDelay (New-TimeSpan -Seconds 30)
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 10)

Register-ScheduledTask -TaskName 'USBFixBootCheck' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

Write-Host ""
Write-Host "[完成] 开机自检已安装"
Write-Host "  任务名: USBFixBootCheck"
Write-Host "  开机 30 秒后自动运行"
