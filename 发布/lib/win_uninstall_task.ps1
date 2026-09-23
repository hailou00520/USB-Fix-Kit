# 卸载开机自检
$ErrorActionPreference = 'SilentlyContinue'

Unregister-ScheduledTask -TaskName 'USBFixBootCheck' -Confirm:$false
sc.exe delete USBFixBoot 2>$null | Out-Null

$destDir = Join-Path $env:SystemRoot 'USBFix'
if (Test-Path $destDir) { Remove-Item -Path $destDir -Recurse -Force }

Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce' -Name 'USBFixCheck' -ErrorAction SilentlyContinue

$taskFile = Join-Path $env:SystemRoot 'System32\Tasks\USBFixBootCheck'
if (Test-Path $taskFile) { Remove-Item -Path $taskFile -Force }

Write-Host ""
Write-Host "[完成] 开机自检已卸载（服务 + 计划任务 + 文件）"
