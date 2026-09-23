# PE 离线解除账号限制：启用内置管理员 + 自动登录
param(
    [Parameter(Mandatory=$true)][string]$WinDrive
)

$ErrorActionPreference = 'Stop'
$LogFile = Join-Path $WinDrive 'usb_fix_log.txt'

function Write-Log($msg) {
    $line = "[账号解锁] $msg"
    Write-Host $line
    Add-Content -Path $LogFile -Value $line -Encoding UTF8
}

Write-Host ""
Write-Host "── [账号解锁] 系统盘: $WinDrive ──"
Write-Host ""

# 加载离线注册表
$softPath = "$WinDrive\Windows\System32\config\SOFTWARE"
$samPath  = "$WinDrive\Windows\System32\config\SAM"
$sysPath  = "$WinDrive\Windows\System32\config\SYSTEM"

reg load "HKLM\PEOFFLINE_SOFT" $softPath | Out-Null
reg load "HKLM\PEOFFLINE_SAM"  $samPath  | Out-Null
reg load "HKLM\PEOFFLINE_SYS"  $sysPath  | Out-Null

try {
    # ---- 1. 启用内置 Administrator 账户 ----
    Write-Log "启用内置 Administrator 账户..."

    $adminKey = 'HKLM:\PEOFFLINE_SAM\SAM\SAM\Domains\Account\Users\000001F4'
    if (Test-Path $adminKey) {
        $v = (Get-ItemProperty -Path $adminKey -Name 'V').V
        if ($v -is [byte[]]) {
            $offset = 0x38
            $flags  = [BitConverter]::ToInt32($v, $offset)
            $flags  = $flags -band (-bnot 0x10)   # 清除"账户已禁用"
            $flags  = $flags -bor  0x20           # 允许空密码
            [BitConverter]::GetBytes([int32]$flags) | ForEach-Object -Begin { $i = 0 } -Process {
                $v[$offset + $i] = $_; $i++
            }
            Set-ItemProperty -Path $adminKey -Name 'V' -Value $v
            Write-Log "Administrator 已启用（允许空密码）"
        }
    } else {
        Write-Log "警告: 未找到 Administrator 账户项"
    }

    # ---- 2. 设置自动登录（无需键鼠操作登录界面）----
    Write-Log "配置自动登录..."

    $winlogon = 'HKLM:\PEOFFLINE_SOFT\Microsoft\Windows NT\CurrentVersion\Winlogon'
    Set-ItemProperty -Path $winlogon -Name 'AutoAdminLogon'    -Value '1'           -Type String
    Set-ItemProperty -Path $winlogon -Name 'DefaultUserName' -Value 'Administrator' -Type String
    Set-ItemProperty -Path $winlogon -Name 'DefaultPassword' -Value ''              -Type String
    Set-ItemProperty -Path $winlogon -Name 'ForceAutoLogon'  -Value '1'           -Type String

    # 清除可能阻止登录的策略
    $policies = 'HKLM:\PEOFFLINE_SOFT\Microsoft\Windows\CurrentVersion\Policies\System'
    if (Test-Path $policies) {
        Remove-ItemProperty -Path $policies -Name 'DisableCAD'       -ErrorAction SilentlyContinue
        Remove-ItemProperty -Path $policies -Name 'DontDisplayLastUserName' -ErrorAction SilentlyContinue
    }

    # ---- 3. 解除账户锁定（如有）----
    Write-Log "清除账户锁定标记..."
    $usersPath = 'HKLM:\PEOFFLINE_SAM\SAM\SAM\Domains\Account\Users'
    if (Test-Path $usersPath) {
        Get-ChildItem $usersPath | Where-Object { $_.PSChildName -match '^\d+$' } | ForEach-Object {
            try {
                $uv = (Get-ItemProperty -Path $_.PSPath -Name 'V' -ErrorAction SilentlyContinue).V
                if ($uv -is [byte[]] -and $uv.Length -gt 0x3C) {
                    # 清除锁定标志 (bit 0x10 at offset 0x38 是禁用，0x800 是锁定相关)
                    $off = 0x38
                    $fl  = [BitConverter]::ToInt32($uv, $off)
                    $fl  = $fl -band (-bnot 0x10)
                    [BitConverter]::GetBytes([int32]$fl) | ForEach-Object -Begin { $j = 0 } -Process {
                        $uv[$off + $j] = $_; $j++
                    }
                    Set-ItemProperty -Path $_.PSPath -Name 'V' -Value $uv
                }
            } catch {}
        }
        Write-Log "已处理所有本地账户锁定/禁用标记"
    }

    Write-Host ""
    Write-Host "[账号解锁] 完成"
    Write-Host "  - 内置 Administrator 已启用"
    Write-Host "  - 已设置自动登录（无需输入密码）"
    Write-Host "  - 重启后将自动进入桌面"
}
finally {
    reg unload "HKLM\PEOFFLINE_SOFT" 2>$null | Out-Null
    reg unload "HKLM\PEOFFLINE_SAM"  2>$null | Out-Null
    reg unload "HKLM\PEOFFLINE_SYS"  2>$null | Out-Null
}
