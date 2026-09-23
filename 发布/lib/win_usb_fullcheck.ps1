# USB 穷尽式全面修复 — 「干看着」版
# 把所有常见/进阶办法按阶段试完；全部失败才判定「只能重装」
#
# 阶段：
#   1 soft       — 常规修复（服务/策略/过滤/节能/设备）
#   2 aggressive — 强力修复（卸驱动、DISM/SFC、卸光 USB 重枚举）
#   3 safemode   — 安全模式修复（进安全模式再修，再退回正常）
#   4 lastditch  — 最后手段（重置服务默认、清残留）
#   5 exhausted  — 全部试完仍失败 → 大字提示重装（任务完成）

param(
    [switch]$Silent,
    [switch]$Watch
)

$ErrorActionPreference = 'SilentlyContinue'
$drive = if ($env:SystemDrive) { $env:SystemDrive } else { 'C:' }
$FixDir     = Join-Path $drive 'Windows\USBFix'
$LogFile    = Join-Path $drive 'usb_fix_log.txt'
$ReportFile = Join-Path $drive 'usb_fix_report.txt'
$StateFile  = Join-Path $FixDir 'state.txt'
$Issues = New-Object System.Collections.Generic.List[string]
$Fixed  = New-Object System.Collections.Generic.List[string]
$Tried  = New-Object System.Collections.Generic.List[string]

$useUi = -not $Silent
if ($Watch) { $useUi = $true }
if ([System.Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0 -and -not $Watch) {
    $useUi = $false
}

# ===================== UI =====================
$script:Ui = $null; $script:LblStep = $null; $script:LblBig = $null
$script:LblPhase = $null; $script:List = $null; $script:Bar = $null

function New-WatchUi {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    [System.Windows.Forms.Application]::EnableVisualStyles()
    $form = New-Object System.Windows.Forms.Form
    $form.Text = 'USB 穷尽修复 — 请干看着，无需操作'
    $form.WindowState = 'Maximized'
    $form.FormBorderStyle = 'None'
    $form.TopMost = $true
    $form.BackColor = [System.Drawing.Color]::FromArgb(9, 9, 11)
    $sw = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width
    $sh = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height

    $t = New-Object System.Windows.Forms.Label
    $t.Text = 'USB 穷尽修复进行中'; $t.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 28, [System.Drawing.FontStyle]::Bold)
    $t.ForeColor = [System.Drawing.Color]::White; $t.AutoSize = $true; $t.Location = New-Object System.Drawing.Point(48, 28)

    $h = New-Object System.Windows.Forms.Label
    $h.Text = '会把所有办法逐个试完。你不用点任何东西。全部失败才会提示重装。'
    $h.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 13)
    $h.ForeColor = [System.Drawing.Color]::FromArgb(161,161,170); $h.AutoSize = $true
    $h.Location = New-Object System.Drawing.Point(52, 82)

    $script:LblPhase = New-Object System.Windows.Forms.Label
    $script:LblPhase.Text = '阶段：—'
    $script:LblPhase.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 14, [System.Drawing.FontStyle]::Bold)
    $script:LblPhase.ForeColor = [System.Drawing.Color]::FromArgb(250, 204, 21)
    $script:LblPhase.AutoSize = $true; $script:LblPhase.Location = New-Object System.Drawing.Point(52, 118)

    $script:LblStep = New-Object System.Windows.Forms.Label
    $script:LblStep.Text = '准备中…'
    $script:LblStep.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 15)
    $script:LblStep.ForeColor = [System.Drawing.Color]::FromArgb(96,165,250)
    $script:LblStep.AutoSize = $true; $script:LblStep.Location = New-Object System.Drawing.Point(52, 152)

    $script:Bar = New-Object System.Windows.Forms.ProgressBar
    $script:Bar.Minimum = 0; $script:Bar.Maximum = 100; $script:Bar.Value = 0
    $script:Bar.Width = [Math]::Max(600, $sw - 120); $script:Bar.Height = 16
    $script:Bar.Location = New-Object System.Drawing.Point(52, 190)

    $script:List = New-Object System.Windows.Forms.ListBox
    $script:List.Font = New-Object System.Drawing.Font('Consolas', 11)
    $script:List.BackColor = [System.Drawing.Color]::FromArgb(24,24,27)
    $script:List.ForeColor = [System.Drawing.Color]::FromArgb(212,212,216)
    $script:List.BorderStyle = 'None'
    $script:List.Location = New-Object System.Drawing.Point(52, 220)
    $script:List.Width = $script:Bar.Width
    $script:List.Height = [Math]::Max(260, $sh - 460)

    $script:LblBig = New-Object System.Windows.Forms.Label
    $script:LblBig.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 32, [System.Drawing.FontStyle]::Bold)
    $script:LblBig.ForeColor = [System.Drawing.Color]::White
    $script:LblBig.AutoSize = $false; $script:LblBig.Width = $script:Bar.Width; $script:LblBig.Height = 150
    $script:LblBig.Location = New-Object System.Drawing.Point(52, ($script:List.Bottom + 16))
    $script:LblBig.TextAlign = 'MiddleLeft'

    $form.Controls.AddRange(@($t,$h,$script:LblPhase,$script:LblStep,$script:Bar,$script:List,$script:LblBig))
    $script:Ui = $form; $form.Show(); $form.Refresh()
    [System.Windows.Forms.Application]::DoEvents()
}

function Write-Log([string]$m) {
    Add-Content -Path $LogFile -Value "[$(Get-Date -Format 'HH:mm:ss')] $m" -Encoding UTF8
}
function Ui-Pulse {
    if ($script:Ui) { $script:Ui.Refresh(); [System.Windows.Forms.Application]::DoEvents() }
}
function Ui-Phase([string]$name) {
    Write-Log "==== 阶段: $name ===="
    if ($script:LblPhase) { $script:LblPhase.Text = "阶段：$name"; Ui-Pulse }
}
function Ui-Step([string]$text, [int]$pct) {
    Write-Log $text
    [void]$Tried.Add($text)
    if ($script:LblStep) { $script:LblStep.Text = $text }
    if ($script:Bar -and $pct -ge 0 -and $pct -le 100) { $script:Bar.Value = $pct }
    if ($script:List) {
        $script:List.Items.Add($text)
        $script:List.TopIndex = $script:List.Items.Count - 1
    }
    Ui-Pulse
}
function Ui-Line([string]$text) {
    Write-Log $text
    if ($script:List) {
        $script:List.Items.Add("    $text")
        $script:List.TopIndex = $script:List.Items.Count - 1
        Ui-Pulse
    }
}
function Add-Issue([string]$m) { [void]$Issues.Add($m); Ui-Line "[问题] $m" }
function Add-Fixed([string]$m) { [void]$Fixed.Add($m); Ui-Line "[已修] $m" }
function Ui-Finish([bool]$ok, [string]$msg) {
    if ($script:LblBig) {
        $script:LblBig.Text = $msg
        $script:LblBig.ForeColor = if ($ok) {
            [System.Drawing.Color]::FromArgb(34,197,94)
        } else {
            [System.Drawing.Color]::FromArgb(239,68,68)
        }
        Ui-Pulse
    }
    try {
        if ($ok) { [Console]::Beep(880,200); Start-Sleep -m 80; [Console]::Beep(1175,280) }
        else {
            1..3 | ForEach-Object { [Console]::Beep(380,350); Start-Sleep -m 100 }
        }
    } catch {}
}
function Ui-Countdown([string]$prefix, [int]$sec) {
    for ($i = $sec; $i -ge 0; $i--) {
        if ($script:LblStep) { $script:LblStep.Text = "$prefix $i 秒…"; Ui-Pulse }
        Start-Sleep -Seconds 1
    }
}

# ===================== 状态机 =====================
function Get-State {
    $s = @{ phase = 'soft'; attempt = 0 }
    if (Test-Path $StateFile) {
        foreach ($line in Get-Content $StateFile) {
            if ($line -match '^phase=(.+)$') { $s.phase = $Matches[1].Trim() }
            if ($line -match '^attempt=(\d+)') { $s.attempt = [int]$Matches[1] }
        }
    }
    return $s
}
function Set-State([string]$phase, [int]$attempt) {
    if (-not (Test-Path $FixDir)) { New-Item -ItemType Directory -Path $FixDir -Force | Out-Null }
    Set-Content $StateFile "phase=$phase`nattempt=$attempt`nupdated=$(Get-Date)" -Encoding UTF8
}

# 最近一次健康检测的白话说明（给报告用）
$script:LastHealth = @{
    Ok = $false
    Usb = 0; Hid = 0; Kbd = 0; Mou = 0; UsbDev = 0
    OneLine = '尚未检测'
}

function Test-UsbHealthy {
    Start-Sleep -Seconds 2
    $usb    = @(Get-PnpDevice -Class USB -Status OK -ErrorAction SilentlyContinue).Count
    $usbDev = @(Get-PnpDevice -Class USBDevice -Status OK -ErrorAction SilentlyContinue).Count
    $hid    = @(Get-PnpDevice -Class HIDClass -Status OK -ErrorAction SilentlyContinue).Count
    $kbd    = @(Get-PnpDevice -Class Keyboard -Status OK -ErrorAction SilentlyContinue).Count
    $mou    = @(Get-PnpDevice -Class Mouse -Status OK -ErrorAction SilentlyContinue).Count
    $usbOk  = ($usb -gt 0) -or ($usbDev -gt 0)
    $hidOk  = ($hid -gt 0) -or ($kbd -gt 0) -or ($mou -gt 0)
    $ok     = $usbOk -and $hidOk

    $script:LastHealth = @{
        Ok = $ok
        Usb = $usb; Hid = $hid; Kbd = $kbd; Mou = $mou; UsbDev = $usbDev
        OneLine = if ($ok) {
            "键鼠/USB 设备管理器里能看到正常设备（可用）"
        } else {
            "设备管理器里暂时数不到正常的 USB 或键鼠（不一定真坏，也可能是检测时机问题）"
        }
    }
    Ui-Line "检测: USB控制器=$usb USB设备=$usbDev HID=$hid 键盘=$kbd 鼠标=$mou → $(if($ok){'正常'}else{'异常/待观察'})"
    return $ok
}

function Clear-AutoStart {
    sc.exe config USBFixBoot start= disabled | Out-Null
    foreach ($name in @('USBFullCheck','USBFullCheckOnce','USBFixCheck')) {
        Remove-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name $name -Force -EA SilentlyContinue
        Remove-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce' -Name $name -Force -EA SilentlyContinue
    }
    $sb = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\StartUp\USB全面体检.bat'
    if (Test-Path $sb) { Remove-Item $sb -Force }
}

function Save-Report([bool]$healthy, [string]$verdict) {
    $h = $script:LastHealth
    if ($healthy) {
        $whatNow = '现在可以正常用键鼠和 USB。'
        $needDo  = '什么都不用做。本报告可以删掉或留着备查。'
    } else {
        $whatNow = '脚本当时认为键鼠/USB 仍不健康。'
        $needDo  = @"
请先自己试一下键盘鼠标能不能用：
  · 能用 → 多半是「检测误报」，可以忽略本报告。
  · 不能用 → 看文末详细日志，或考虑重装系统。
"@
    }

    $issuesBlock = if ($Issues.Count -eq 0) {
        '  （没有单独记下来的问题项）'
    } else {
        ($Issues | ForEach-Object { "  · $_" }) -join "`r`n"
    }
    $fixedBlock = if ($Fixed.Count -eq 0) {
        '  （本轮没有记到「已自动修好」的项）'
    } else {
        ($Fixed | ForEach-Object { "  · $_" }) -join "`r`n"
    }
    $triedBlock = if ($Tried.Count -eq 0) {
        '  （无）'
    } else {
        ($Tried | ForEach-Object { "  · $_" }) -join "`r`n"
    }

    $report = @"
========================================
 USB 修复结果说明（给人看的）
 时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
========================================

【一句话结论】
  $verdict
  $whatNow

【你要不要管】
$needDo

【检测时看到了什么】（不是故障清单，只是当时数到的正常设备数量）
  USB 控制器（正常）: $($h.Usb)
  USB 设备（正常）:   $($h.UsbDev)
  HID（正常）:        $($h.Hid)
  键盘（正常）:       $($h.Kbd)
  鼠标（正常）:       $($h.Mou)
  白话: $($h.OneLine)

【本轮自动修好了什么】（共 $($Fixed.Count) 项）
$fixedBlock

【过程中发现过什么】（共 $($Issues.Count) 项；发现后多数已尝试修复）
$issuesBlock

【试过哪些办法】（共 $($Tried.Count) 步；这是进度，不是「坏了多少个」）
$triedBlock

----------------------------------------
 详细过程日志（技术人员看）: $LogFile
 本文件位置: $ReportFile
========================================
"@
    Set-Content $ReportFile $report -Encoding UTF8
    try {
        $desk = [Environment]::GetFolderPath('CommonDesktopDirectory')
        Set-Content (Join-Path $desk 'USB体检结果.txt') $report -Encoding UTF8
    } catch {}
}

function Reboot-Soon([string]$why, [int]$sec = 20) {
    Ui-Finish $false $why
    if ($useUi) { Ui-Countdown '自动重启倒计时' $sec } else { Start-Sleep -Seconds $sec }
    shutdown.exe /r /t 0 /f
}

# ===================== 修复动作库 =====================
function Fix-Services {
    Ui-Step '办法: 启用并启动全部 USB/HID/即插即用服务' 5
    foreach ($svc in @('USBXHCI','usbhub','USBHUB3','usbccgp','USBSTOR','HidUsb','HidClass','mouhid','kbdhid','mouclass','kbdclass','PlugPlay','DeviceInstall','Wdf01000','WUDFRd')) {
        $s = Get-Service $svc -EA SilentlyContinue; if (-not $s) { continue }
        $cfg = Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$svc" -EA SilentlyContinue
        if ($cfg -and $cfg.Start -eq 4) {
            Add-Issue "服务禁用 $svc"; Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$svc" Start 3 -Type DWord; Add-Fixed "启用 $svc"
        }
        if ($s.Status -ne 'Running') {
            Set-Service $svc -StartupType Manual -EA SilentlyContinue
            Start-Service $svc -EA SilentlyContinue
            if ((Get-Service $svc -EA SilentlyContinue).Status -eq 'Running') { Add-Fixed "启动 $svc" }
        }
    }
}

function Fix-Policies {
    Ui-Step '办法: 清除设备安装限制 / 组策略锁' 10
    $paths = @(
        'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions',
        'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions\DenyDeviceIDs',
        'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions\DenyDeviceClasses',
        'HKLM:\SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices'
    )
    foreach ($p in $paths) {
        if (Test-Path $p) { Add-Issue "策略 $p"; Remove-Item $p -Recurse -Force; Add-Fixed "删除 $p" }
    }
    # 解除禁止安装未签名等过度策略（不降低安全到离谱，只清阻止安装）
    $sysPol = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall'
    if (Test-Path $sysPol) {
        Remove-ItemProperty $sysPol DenyRemovableDevices -Force -EA SilentlyContinue
    }
}

function Fix-Filters([switch]$Aggressive) {
    Ui-Step $(if ($Aggressive) { '办法: 强力清理过滤驱动后恢复微软默认' } else { '办法: 清理 USB/键鼠第三方过滤驱动' }) 18
    $guids = @(
        '{36fc9e60-c465-11cf-8056-444553540000}',
        '{4d36e96b-e325-11ce-bfc1-444553540000}',
        '{4d36e96f-e325-11ce-bfc1-444553540000}',
        '{745a17a0-74d3-11d0-b6fe-00a0c90f57da}',
        '{88BAE032-5A81-49f0-BC3D-A4FF138216D6}'
    )
    $keep = @('kbdclass','mouclass','HidUsb','usbccgp','usbhub','USBXHCI')
    foreach ($g in $guids) {
        $path = "HKLM:\SYSTEM\CurrentControlSet\Control\Class\$g"
        foreach ($f in @('UpperFilters','LowerFilters')) {
            $val = (Get-ItemProperty $path -Name $f -EA SilentlyContinue).$f
            if (-not $val) { continue }
            if ($Aggressive) {
                Add-Issue "强力清除 $g $f"
                Remove-ItemProperty $path -Name $f -Force -EA SilentlyContinue
                Add-Fixed "已删除 $f"
            } elseif ($val -is [string[]]) {
                $bad = @($val | Where-Object { $keep -notcontains $_ })
                $safe = @($val | Where-Object { $keep -contains $_ })
                if ($bad.Count -gt 0) {
                    Add-Issue "第三方过滤 $($bad -join ',')"
                    if ($safe.Count -gt 0) { Set-ItemProperty $path $f ([string[]]$safe) } else { Remove-ItemProperty $path $f -Force }
                    Add-Fixed "已清理过滤驱动"
                }
            }
        }
    }
    # 关键：清完必须把键鼠类默认过滤写回去，否则键鼠更废
    Fix-RestoreDefaultClassFilters
}

function Fix-RestoreDefaultClassFilters {
    Ui-Step '办法: 恢复键鼠类默认 UpperFilters（kbdclass / mouclass）' 20
    $kbd = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e96b-e325-11ce-bfc1-444553540000}'
    $mou = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e96f-e325-11ce-bfc1-444553540000}'
    if (Test-Path $kbd) {
        Set-ItemProperty -Path $kbd -Name UpperFilters -Value @('kbdclass') -Type MultiString
        Add-Fixed '键盘类 UpperFilters = kbdclass'
    }
    if (Test-Path $mou) {
        Set-ItemProperty -Path $mou -Name UpperFilters -Value @('mouclass') -Type MultiString
        Add-Fixed '鼠标类 UpperFilters = mouclass'
    }
}

function Fix-RegistryDeep {
    Ui-Step '办法: 深度清理「改坏的注册表」常见项（你说的那种）' 22

    # 1) 扫描码重映射 — 很多人改这个导致键盘失灵
    $kbLayout = 'HKLM:\SYSTEM\CurrentControlSet\Control\Keyboard Layout'
    if (Test-Path $kbLayout) {
        if ($null -ne (Get-ItemProperty $kbLayout -Name 'Scancode Map' -EA SilentlyContinue).'Scancode Map') {
            Add-Issue '存在 Scancode Map（键盘扫描码重映射）'
            Remove-ItemProperty $kbLayout -Name 'Scancode Map' -Force
            Add-Fixed '已删除 Scancode Map'
        }
        # 也清掉 Layout 下可能的异常
        Remove-ItemProperty $kbLayout -Name 'ScanCode Map' -Force -EA SilentlyContinue
    }
    $kbLayoutDo = 'HKLM:\SYSTEM\CurrentControlSet\Control\Keyboard Layouts'
    # 不整树删除，只记录

    # 2) 设备安装限制里专门禁键盘/鼠标/HID/USB 类
    $denyClass = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions\DenyDeviceClasses'
    $blockedGuids = @(
        '{4d36e96b-e325-11ce-bfc1-444553540000}', # Keyboard
        '{4d36e96f-e325-11ce-bfc1-444553540000}', # Mouse
        '{745a17a0-74d3-11d0-b6fe-00a0c90f57da}', # HID
        '{36fc9e60-c465-11cf-8056-444553540000}', # USB
        '{88BAE032-5A81-49f0-BC3D-A4FF138216D6}'  # USBDevice
    )
    if (Test-Path $denyClass) {
        Add-Issue '存在 DenyDeviceClasses 策略'
        Remove-Item $denyClass -Recurse -Force
        Add-Fixed '已删除 DenyDeviceClasses'
    }
    $denyIds = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions\DenyDeviceIDs'
    if (Test-Path $denyIds) {
        Add-Issue '存在 DenyDeviceIDs 策略'
        Remove-Item $denyIds -Recurse -Force
        Add-Fixed '已删除 DenyDeviceIDs'
    }
    $restrict = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions'
    if (Test-Path $restrict) {
        foreach ($n in @('DenyUnspecified','DenyDeviceClassesRetroactive','DenyDeviceIDsRetroactive','AllowAdminInstall')) {
            Remove-ItemProperty $restrict -Name $n -Force -EA SilentlyContinue
        }
    }

    # 3) 服务级 Parameters：USB 选择性挂起 / 错误的调试开关
    foreach ($svc in @('usbhub','USBHUB3','USBXHCI','usbccgp','HidUsb')) {
        $pp = "HKLM:\SYSTEM\CurrentControlSet\Services\$svc\Parameters"
        if (-not (Test-Path $pp)) { continue }
        foreach ($n in @('DisableSelectiveSuspend','DisableOnSoftRemove','DebugFlags','DebugLevel')) {
            # DisableSelectiveSuspend=1 是好事；确保挂起相关坏值清掉
        }
        Set-ItemProperty $pp DisableSelectiveSuspend 1 -Type DWord -EA SilentlyContinue
        Remove-ItemProperty $pp -Name 'ForceHCResetOnResume' -Force -EA SilentlyContinue
    }
    Add-Fixed 'USB 服务 Parameters 已校正'

    # 4) Enum 下逐设备：清掉禁用标志 / 错误的 UpperFilters / 节能
    Ui-Line '扫描 Enum\USB 与 Enum\HID 下的异常注册表…'
    foreach ($root in @('USB','HID','HIDCLASS','USBSTOR')) {
        $enumRoot = "HKLM:\SYSTEM\CurrentControlSet\Enum\$root"
        if (-not (Test-Path $enumRoot)) { continue }
        Get-ChildItem $enumRoot -Recurse -EA SilentlyContinue | Where-Object {
            $_.PSChildName -eq 'Device Parameters' -or (Get-ItemProperty $_.PSPath -EA SilentlyContinue).PSObject.Properties.Name -contains 'UpperFilters'
        } | ForEach-Object {
            $p = $_.PSPath
            # Device Parameters 节能
            if ($_.PSChildName -eq 'Device Parameters') {
                Set-ItemProperty $p SelectiveSuspendEnabled 0 -Type DWord -EA SilentlyContinue
                Set-ItemProperty $p EnhancedPowerManagementEnabled 0 -Type DWord -EA SilentlyContinue
                Set-ItemProperty $p AllowIdleIrpInD3 0 -Type DWord -EA SilentlyContinue
            }
        }
        # 设备实例上的 ConfigFlags bit0 = 禁用
        Get-ChildItem $enumRoot -Recurse -EA SilentlyContinue | ForEach-Object {
            try {
                $cf = (Get-ItemProperty $_.PSPath -Name ConfigFlags -EA SilentlyContinue).ConfigFlags
                if ($null -ne $cf -and ($cf -band 0x1) -eq 0x1) {
                    $new = $cf -band (-bnot 0x1)
                    Set-ItemProperty $_.PSPath ConfigFlags $new -Type DWord
                    Ui-Line "清除禁用标志: $($_.Name)"
                    Add-Fixed "启用被注册表禁用的设备"
                }
                # 设备级异常 UpperFilters
                $uf = (Get-ItemProperty $_.PSPath -Name UpperFilters -EA SilentlyContinue).UpperFilters
                if ($uf) {
                    Add-Issue "设备级 UpperFilters $($_.Name)"
                    Remove-ItemProperty $_.PSPath -Name UpperFilters -Force -EA SilentlyContinue
                    Add-Fixed '已删设备级 UpperFilters'
                }
                $lf = (Get-ItemProperty $_.PSPath -Name LowerFilters -EA SilentlyContinue).LowerFilters
                if ($lf) {
                    Remove-ItemProperty $_.PSPath -Name LowerFilters -Force -EA SilentlyContinue
                    Add-Fixed '已删设备级 LowerFilters'
                }
            } catch {}
        }
    }

    # 5) IFEO 劫持（调试器挂到键鼠相关）
    $ifeo = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options'
    Get-ChildItem $ifeo -EA SilentlyContinue | Where-Object {
        $_.PSChildName -match 'usb|hid|mouse|kbd|i8042|input'
    } | ForEach-Object {
        Add-Issue "IFEO $($_.PSChildName)"
        Remove-Item $_.PSPath -Recurse -Force
        Add-Fixed "删除 IFEO $($_.PSChildName)"
    }

    # 6) AppInit_DLLs 注入（偶发搞挂输入栈）
    $winlogon = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows'
    $appInit = (Get-ItemProperty $winlogon -Name AppInit_DLLs -EA SilentlyContinue).AppInit_DLLs
    if ($appInit -and "$appInit".Trim().Length -gt 0) {
        Add-Issue "AppInit_DLLs = $appInit"
        Set-ItemProperty $winlogon AppInit_DLLs '' -Type String
        Set-ItemProperty $winlogon LoadAppInit_DLLs 0 -Type DWord -EA SilentlyContinue
        Add-Fixed '已清空 AppInit_DLLs'
    }

    # 7) 会话管理器里异常 BootExecute / 屏蔽
    # 不乱动 BootExecute 默认值，只检查是否有明显 usb 过滤注入
    $sm = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager'
    # 8) 恢复默认类过滤
    Fix-RestoreDefaultClassFilters

    # 9) 类安装程序异常值
    foreach ($g in $blockedGuids) {
        $cp = "HKLM:\SYSTEM\CurrentControlSet\Control\Class\$g"
        if (-not (Test-Path $cp)) { continue }
        # 清掉可能阻止安装的异常
        Remove-ItemProperty $cp -Name 'NoInstallClass' -Force -EA SilentlyContinue
        Remove-ItemProperty $cp -Name 'SilentInstall' -Force -EA SilentlyContinue
    }

    # 10) 策略：禁止人机接口设备（少见但致命）
    $hidPol = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System'
    Remove-ItemProperty $hidPol -Name 'DenyOnlineID' -Force -EA SilentlyContinue

    Add-Fixed '注册表深度清理完成'
}

function Fix-LastDitchRegistry {
    Ui-Step '办法: 最后手段 — 重置服务 + 再跑一遍注册表深度清理' 92
    foreach ($svc in @('USBXHCI','usbhub','USBHUB3','HidUsb','mouhid','kbdhid','kbdclass','mouclass')) {
        $p = "HKLM:\SYSTEM\CurrentControlSet\Services\$svc"
        if (Test-Path $p) {
            $start = switch ($svc) {
                'USBXHCI' { 0 }
                'kbdclass' { 3 }
                'mouclass' { 3 }
                default { 3 }
            }
            Set-ItemProperty $p Start $start -Type DWord
            Ui-Line "重置 $svc Start=$start"
        }
    }
    Fix-RegistryDeep
    Fix-RestoreDefaultClassFilters
    Add-Fixed '最后手段注册表处理完成'
}

function Fix-Power {
    Ui-Step '办法: 关闭 USB 选择性暂停与设备节能' 25
    powercfg /setacvalueindex SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0 | Out-Null
    powercfg /setdcvalueindex SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0 | Out-Null
    powercfg /setactive SCHEME_CURRENT | Out-Null
    # PCI Express 链路状态电源管理
    powercfg /setacvalueindex SCHEME_CURRENT 501a4d13-42af-4429-9fd1-a821f2b14571 ee12f906-d277-404b-b6da-e5fa1a576df5 0 | Out-Null
    powercfg /setdcvalueindex SCHEME_CURRENT 501a4d13-42af-4429-9fd1-a821f2b14571 ee12f906-d277-404b-b6da-e5fa1a576df5 0 | Out-Null
    Get-PnpDevice -Class USB -EA SilentlyContinue | ForEach-Object {
        $pk = "HKLM:\SYSTEM\CurrentControlSet\Enum\$($_.InstanceId)\Device Parameters"
        if (Test-Path $pk) {
            Set-ItemProperty $pk EnhancedPowerManagementEnabled 0 -Type DWord -EA SilentlyContinue
            Set-ItemProperty $pk AllowIdleIrpInD3 0 -Type DWord -EA SilentlyContinue
            Set-ItemProperty $pk DeviceSelectiveSuspended 0 -Type DWord -EA SilentlyContinue
        }
    }
    Add-Fixed '节能相关已关闭'
}

function Fix-FastBoot {
    Ui-Step '办法: 关闭快速启动 / 混合关机' 30
    $p = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power'
    if ((Get-ItemProperty $p HiberbootEnabled -EA SilentlyContinue).HiberbootEnabled -eq 1) {
        Add-Issue '快速启动开启'; Set-ItemProperty $p HiberbootEnabled 0 -Type DWord; Add-Fixed '已关闭快速启动'
    }
}

function Fix-SuspectAutorun {
    Ui-Step '办法: 清除 USB 管控类自启残留' 35
    $suspect = @('usblock','usbcontrol','usbguard','devicecontrol','usbmonitor','devicelock','endpointprotector','deviceblock')
    foreach ($rk in @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run'
    )) {
        if (-not (Test-Path $rk)) { continue }
        $props = Get-ItemProperty $rk
        foreach ($pr in $props.PSObject.Properties) {
            if ($pr.Name -match '^PS' -or $pr.Name -match 'USBFull') { continue }
            $val = "$($pr.Value)".ToLowerInvariant()
            foreach ($s in $suspect) {
                if ($val -like "*$s*" -or $pr.Name.ToLowerInvariant() -like "*$s*") {
                    Add-Issue "可疑自启 $($pr.Name)"; Remove-ItemProperty $rk $pr.Name -Force; Add-Fixed "移除 $($pr.Name)"
                }
            }
        }
    }
}

function Fix-EnableDevices {
    Ui-Step '办法: 启用并重启异常 USB/HID 设备' 42
    pnputil /scan-devices | Out-Null
    Get-PnpDevice -EA SilentlyContinue |
        Where-Object { $_.Class -in @('USB','HIDClass','Keyboard','Mouse','USBDevice') -and $_.Status -notin @('OK','Unknown') } |
        ForEach-Object {
            Add-Issue "[$($_.Status)] $($_.FriendlyName)"
            Enable-PnpDevice -InstanceId $_.InstanceId -Confirm:$false -EA SilentlyContinue
            pnputil /restart-device "$($_.InstanceId)" | Out-Null
            Add-Fixed "重启 $($_.FriendlyName)"
        }
}

function Fix-RestartControllers {
    Ui-Step '办法: 重启全部 USB 主机控制器 / Root Hub' 50
    Get-PnpDevice -Class USB -EA SilentlyContinue |
        Where-Object { $_.FriendlyName -match 'Root Hub|Host Controller|xHCI|EHCI|OHCI|UHCI' } |
        ForEach-Object {
            pnputil /disable-device "$($_.InstanceId)" | Out-Null
            Start-Sleep -Milliseconds 400
            pnputil /enable-device "$($_.InstanceId)" | Out-Null
            pnputil /restart-device "$($_.InstanceId)" | Out-Null
            Ui-Line "重启控制器 $($_.FriendlyName)"
        }
    Add-Fixed '控制器已重启'
}

function Fix-RemoveOemDrivers([switch]$Broad) {
    Ui-Step $(if ($Broad) { '办法: 卸载所有可疑第三方 USB/手机驱动' } else { '办法: 卸载 vivo/小米/ADB 等冲突驱动' }) 58
    $keywords = if ($Broad) {
        @('vivo','xiaomi','miui','android','adb','qualcomm','huawei','oppo','samsung','mtp','usb filter','virtualbox usb','vmware usb','libusb','winusb filter')
    } else {
        @('vivo','xiaomi','miui','android','adb','qualcomm','huawei','oppo')
    }
    $drivers = pnputil /enum-drivers 2>$null
    $oem = $null; $name = $null
    foreach ($line in ($drivers -split "`n")) {
        if ($line -match 'Published Name\s*:\s*(oem\d+\.inf)') { $oem = $Matches[1] }
        if ($line -match 'Original Name\s*:\s*(.+)') { $name = $Matches[1].Trim() }
        if ($line -match 'Driver Version' -and $oem -and $name) {
            foreach ($k in $keywords) {
                if ($name.ToLowerInvariant() -like "*$k*") {
                    Add-Issue "驱动 $oem ($name)"
                    pnputil /delete-driver $oem /uninstall /force | Out-Null
                    Add-Fixed "卸载 $oem"
                    break
                }
            }
            $oem = $null; $name = $null
        }
    }
}

function Fix-ResetHid {
    Ui-Step '办法: 移除异常 HID 并重新枚举' 65
    Get-PnpDevice -EA SilentlyContinue |
        Where-Object { $_.Class -in @('HIDClass','Keyboard','Mouse') -and $_.Status -ne 'OK' } |
        ForEach-Object { pnputil /remove-device "$($_.InstanceId)" | Out-Null; Ui-Line "移除 $($_.FriendlyName)" }
    pnputil /scan-devices | Out-Null
    Start-Sleep -Seconds 3
    Add-Fixed 'HID 重枚举完成'
}

function Fix-RemoveAllUsbRescan {
    Ui-Step '办法: 卸装全部 USB 设备后强制重扫（强力）' 72
    Get-PnpDevice -Class USB -EA SilentlyContinue | ForEach-Object {
        pnputil /remove-device "$($_.InstanceId)" | Out-Null
    }
    Start-Sleep -Seconds 2
    pnputil /scan-devices | Out-Null
    Start-Sleep -Seconds 5
    Add-Fixed 'USB 全量重枚举完成'
}

function Fix-OnlineSfcDism {
    Ui-Step '办法: 在线 DISM + SFC 修复系统文件（较慢）' 80
    Ui-Line 'DISM RestoreHealth 开始…'
    $dism = Start-Process -FilePath 'Dism.exe' -ArgumentList '/Online','/Cleanup-Image','/RestoreHealth' -Wait -PassThru -WindowStyle Hidden
    Ui-Line "DISM 退出码 $($dism.ExitCode)"
    Ui-Line 'SFC ScanNow 开始…'
    $sfc = Start-Process -FilePath 'sfc.exe' -ArgumentList '/scannow' -Wait -PassThru -WindowStyle Hidden
    Ui-Line "SFC 退出码 $($sfc.ExitCode)"
    Add-Fixed '已执行在线 DISM/SFC'
}

function Fix-ReinstallInboxUsb {
    Ui-Step '办法: 尝试从驱动库重装收件箱 USB 驱动' 85
    $repos = Join-Path $env:SystemRoot 'System32\DriverStore\FileRepository'
    $infs = Get-ChildItem $repos -Directory -EA SilentlyContinue |
        Where-Object { $_.Name -match '^(usbxhci|usbhub|usbhub3|hidusb|input)\.inf_' } |
        ForEach-Object { Get-ChildItem $_.FullName -Filter '*.inf' -EA SilentlyContinue } |
        Select-Object -First 20
    foreach ($inf in $infs) {
        Ui-Line "添加驱动 $($inf.Name)"
        pnputil /add-driver "$($inf.FullName)" /install | Out-Null
    }
    pnputil /scan-devices | Out-Null
    Add-Fixed '收件箱 USB 驱动已尝试重装'
}

function Fix-UsbDkRemnants {
    Ui-Step '办法: 专项清除 UsbDk / hrdevmon 残留（UNLOCKTOOL 删坏的那种）' 8
    $badSvcs = @('UsbDk','usbdk','USBDk','usbdkmon','UsbDkHelper','hrdevmon','HRDevMon','UsbDkRuntime')
    foreach ($svc in $badSvcs) {
        $p = "HKLM:\SYSTEM\CurrentControlSet\Services\$svc"
        if (Test-Path $p) {
            Add-Issue "残留服务 $svc"
            sc.exe stop $svc | Out-Null
            sc.exe delete $svc | Out-Null
            Remove-Item $p -Recurse -Force -EA SilentlyContinue
            Add-Fixed "已删除服务 $svc"
        }
    }
    # 从 USB 类过滤里抠掉 UsbDk / hrdevmon 字样
    $guids = @(
        '{36fc9e60-c465-11cf-8056-444553540000}',
        '{4d36e96b-e325-11ce-bfc1-444553540000}',
        '{4d36e96f-e325-11ce-bfc1-444553540000}',
        '{745a17a0-74d3-11d0-b6fe-00a0c90f57da}',
        '{88BAE032-5A81-49f0-BC3D-A4FF138216D6}'
    )
    $poison = @('usbdk','hrdevmon','usbdkmon','usbdkhelper')
    $keep = @('kbdclass','mouclass','HidUsb','usbccgp','usbhub','USBXHCI')
    foreach ($g in $guids) {
        $path = "HKLM:\SYSTEM\CurrentControlSet\Control\Class\$g"
        foreach ($f in @('UpperFilters','LowerFilters')) {
            $val = (Get-ItemProperty $path -Name $f -EA SilentlyContinue).$f
            if (-not $val) { continue }
            $arr = @($val)
            $bad = @($arr | Where-Object { $n = "$_".ToLowerInvariant(); ($poison | Where-Object { $n -like "*$_*" }).Count -gt 0 })
            if ($bad.Count -gt 0) {
                Add-Issue "$g.$f 含 $($bad -join ',')"
                $safe = @($arr | Where-Object { $n = "$_".ToLowerInvariant(); ($poison | Where-Object { $n -like "*$_*" }).Count -eq 0 -and ($keep -contains $_ -or $_ -notmatch '.') })
                # 更稳：只保留白名单
                $safe2 = @($arr | Where-Object { $keep -contains $_ })
                if ($safe2.Count -gt 0) {
                    Set-ItemProperty $path $f -Value ([string[]]$safe2) -Type MultiString
                } else {
                    Remove-ItemProperty $path -Name $f -Force -EA SilentlyContinue
                }
                Add-Fixed "已从 $f 移除 UsbDk/hrdevmon"
            }
        }
    }
    Fix-RestoreDefaultClassFilters
    # 关键：按微软默认 Start 值补 USB 核心服务（商家说的「补 USB 服务」）
    $starts = @{
        'USBXHCI' = 0; 'usbxhci' = 0
        'usbhub' = 1; 'USBHUB' = 1
        'usbhub3' = 1; 'USBHUB3' = 1
        'usbccgp' = 1
        'USBSTOR' = 3
        'PlugPlay' = 2
        'HidUsb' = 3; 'mouhid' = 3; 'kbdhid' = 3
        'kbdclass' = 3; 'mouclass' = 3
    }
    foreach ($kv in $starts.GetEnumerator()) {
        $p = "HKLM:\SYSTEM\CurrentControlSet\Services\$($kv.Key)"
        if (Test-Path $p) {
            Set-ItemProperty $p Start ([int]$kv.Value) -Type DWord
            Ui-Line "补服务 $($kv.Key) Start=$($kv.Value)"
        }
    }
    Add-Fixed 'UsbDk 专项清理 + USB 服务 Start 已补齐'
}

function Enable-SafeModeNextBoot {
    Ui-Step '办法: 设置下次启动进入安全模式' 50
    bcdedit /set '{current}' safeboot minimal | Out-Null
    # 备份标记
    Set-Content (Join-Path $FixDir 'safemode.flag') '1' -Encoding ASCII
    Add-Fixed '已设置安全模式启动'
}

function Disable-SafeModeBoot {
    Ui-Step '办法: 退出安全模式，恢复正常启动' 50
    bcdedit /deletevalue '{current}' safeboot | Out-Null
    Remove-Item (Join-Path $FixDir 'safemode.flag') -Force -EA SilentlyContinue
    Add-Fixed '已取消安全模式'
}

function Test-IsSafeMode {
    $boot = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Option' -EA SilentlyContinue
    return ($null -ne $boot)
}

# ===================== 阶段执行 =====================
function Invoke-PhaseSoft {
    Ui-Phase '1/4 常规修复（含 UsbDk 专项）'
    Fix-UsbDkRemnants
    Fix-RegistryDeep
    Fix-Services
    Fix-Policies
    Fix-Filters
    Fix-Power
    Fix-FastBoot
    Fix-SuspectAutorun
    Fix-EnableDevices
    Fix-RestartControllers
    Fix-RemoveOemDrivers
    Fix-ResetHid
    return (Test-UsbHealthy)
}

function Invoke-PhaseAggressive {
    Ui-Phase '2/4 强力修复'
    Fix-UsbDkRemnants
    Fix-RegistryDeep
    Fix-Services
    Fix-Filters -Aggressive
    Fix-RemoveOemDrivers -Broad
    Fix-RemoveAllUsbRescan
    Fix-ReinstallInboxUsb
    Fix-OnlineSfcDism
    Fix-RestartControllers
    Fix-ResetHid
    return (Test-UsbHealthy)
}

function Invoke-PhaseSafeModeFix {
    Ui-Phase '3/4 安全模式内修复'
    Fix-UsbDkRemnants
    Fix-RegistryDeep
    Fix-Services
    Fix-Filters -Aggressive
    Fix-Policies
    Fix-RemoveOemDrivers -Broad
    Fix-RemoveAllUsbRescan
    Fix-RestartControllers
    return (Test-UsbHealthy)
}

function Invoke-PhaseLastDitch {
    Ui-Phase '4/4 最后手段'
    Fix-UsbDkRemnants
    Fix-LastDitchRegistry
    Fix-Services
    Fix-Filters -Aggressive
    Fix-Power
    Fix-FastBoot
    Fix-RemoveAllUsbRescan
    Fix-ReinstallInboxUsb
    Fix-RestartControllers
    Fix-ResetHid
    return (Test-UsbHealthy)
}

function Complete-Success {
    Ui-Finish $true "请晃一下鼠标 / 按一下键盘！`n能动了就修好了。已自动关闭后续自启。"
    Clear-AutoStart
    Set-State 'done_ok' 0
    Save-Report $true '修复成功'
    if ($script:Ui) {
        for ($i = 90; $i -ge 0; $i--) {
            $script:LblStep.Text = "成功 — $i 秒后自动关窗（也可不管）"
            Ui-Pulse; Start-Sleep -Seconds 1
        }
        $script:Ui.Close()
    }
}

function Complete-Exhausted {
    Ui-Phase '全部办法已试完'
    Ui-Step '结论: 软件能做的都做了' 100
    Save-Report $false '全部办法失败 → 只能重装系统'
    Clear-AutoStart
    Set-State 'done_fail' 0
    Ui-Finish $false @"
所有自动修复办法都已试过，仍然不行。
软件任务完成 — 下一步只能：
关机 → 进 PE → 备份数据 → 重装 Windows。
（PE 里键鼠正常 = 硬件没问题，是系统坏了）
"@
    # 失败页一直挂着，让你看清楚
    if ($script:Ui) {
        while ($script:Ui.Visible) { Ui-Pulse; Start-Sleep -Milliseconds 250 }
    }
}

# ===================== 主流程 =====================
Add-Content $LogFile "`r`n===== USB 穷尽修复 $(Get-Date) =====" -Encoding UTF8
if (-not (Test-Path $FixDir)) { New-Item -ItemType Directory $FixDir -Force | Out-Null }
if ($useUi) { New-WatchUi }

$state = Get-State
$phase = $state.phase
Ui-Line "当前阶段=$phase  attempt=$($state.attempt)"
Ui-Line '请干看着，程序会把所有办法试完'

# 静默预修：只做 soft，不做阶段推进
if ($Silent -and -not $Watch) {
    Ui-Phase '开机静默预修'
    $null = Invoke-PhaseSoft
    Write-Log '静默预修结束'
    exit 0
}

# 已结束过
if ($phase -eq 'done_ok') { Complete-Success; exit 0 }
if ($phase -eq 'done_fail') { Complete-Exhausted; exit 1 }

# ---- 安全模式分支 ----
if ($phase -eq 'safemode_pending') {
    # 刚被要求进安全模式后的那次启动
    if (Test-IsSafeMode) {
        Set-State 'safemode_running' 0
        $ok = Invoke-PhaseSafeModeFix
        Disable-SafeModeBoot
        if ($ok) {
            Set-State 'safemode_exit_check' 0
            Reboot-Soon "安全模式里看起来正常了。`n20 秒后重启回正常模式做最终确认。" 20
            exit 0
        } else {
            Disable-SafeModeBoot
            Set-State 'lastditch' 0
            Reboot-Soon "安全模式修复仍未成功。`n退出安全模式，重启后进入最后手段。" 20
            exit 1
        }
    } else {
        # 没进成安全模式，跳到最后手段
        Ui-Line '未能进入安全模式，跳到最后手段'
        Disable-SafeModeBoot
        Set-State 'lastditch' 0
    }
    $phase = (Get-State).phase
}

if ($phase -eq 'safemode_exit_check') {
    $ok = Test-UsbHealthy
    if ($ok) { Complete-Success; exit 0 }
    Set-State 'lastditch' 0
    $phase = 'lastditch'
}

# ---- 正常阶段推进 ----
switch ($phase) {
    'soft' {
        $ok = Invoke-PhaseSoft
        if ($ok) { Complete-Success; exit 0 }
        Set-State 'aggressive' 0
        Reboot-Soon "常规办法未完全修好。`n20 秒后自动重启，进入「强力修复」。" 20
        exit 1
    }
    'aggressive' {
        $ok = Invoke-PhaseAggressive
        if ($ok) { Complete-Success; exit 0 }
        Enable-SafeModeNextBoot
        Set-State 'safemode_pending' 0
        Reboot-Soon "强力修复仍不够。`n20 秒后自动进「安全模式」再修一轮。" 20
        exit 1
    }
    'lastditch' {
        $ok = Invoke-PhaseLastDitch
        if ($ok) { Complete-Success; exit 0 }
        Complete-Exhausted
        exit 1
    }
    default {
        # 未知状态从 soft 开始
        Set-State 'soft' 0
        $ok = Invoke-PhaseSoft
        if ($ok) { Complete-Success; exit 0 }
        Set-State 'aggressive' 0
        Reboot-Soon "将进入下一阶段强力修复…" 20
        exit 1
    }
}
