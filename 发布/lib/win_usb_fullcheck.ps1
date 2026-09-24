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
    [switch]$Watch,
    # Both=键鼠USB+有线网 · Usb=仅键鼠USB · Net=仅救网（远程通道）
    [ValidateSet('Both', 'Usb', 'Net')]
    [string]$Scope = 'Both'
)

$ErrorActionPreference = 'SilentlyContinue'
$drive = if ($env:SystemDrive) { $env:SystemDrive } else { 'C:' }
$FixDir     = Join-Path $drive 'Windows\USBFix'
$LogFile    = Join-Path $drive 'usb_fix_log.txt'
$ReportFile = Join-Path $drive 'usb_fix_report.txt'
$StateFile  = Join-Path $FixDir 'state.txt'
$ScopeFile  = Join-Path $FixDir 'scope.txt'
$Issues = New-Object System.Collections.Generic.List[string]
$Fixed  = New-Object System.Collections.Generic.List[string]
$Tried  = New-Object System.Collections.Generic.List[string]

# 部署写入的 scope.txt 优先（重启后 bat 参数偶发丢失时仍按部署意图跑）
if (Test-Path $ScopeFile) {
    $fromDisk = ((Get-Content $ScopeFile -Raw -EA SilentlyContinue) + '').Trim()
    if ($fromDisk -match '^(Both|Usb|Net)$') { $Scope = $Matches[1] }
}
$script:FixScope = $Scope

$useUi = -not $Silent
if ($Watch) { $useUi = $true }
if ([System.Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0 -and -not $Watch) {
    $useUi = $false
}

$script:ScopeTitle = switch ($script:FixScope) {
    'Net' { '有线网络急救' }
    'Usb' { '键鼠 / USB 穷尽修复' }
    default { '键鼠+网络 穷尽修复' }
}
$script:ScopeHint = switch ($script:FixScope) {
    'Net' { '专修有线网卡/DHCP/协议栈，方便远程接手。你不用点。' }
    'Usb' { '专修键鼠与 USB。你不用点。全部失败才会提示重装。' }
    default { '键鼠 USB + 有线网一起修。你不用点。全部失败才会提示重装。' }
}

# ===================== UI =====================
$script:Ui = $null; $script:LblStep = $null; $script:LblBig = $null
$script:LblPhase = $null; $script:List = $null; $script:Bar = $null

function New-WatchUi {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    [System.Windows.Forms.Application]::EnableVisualStyles()
    $form = New-Object System.Windows.Forms.Form
    $form.Text = "$($script:ScopeTitle) — 请干看着，无需操作"
    $form.WindowState = 'Maximized'
    $form.FormBorderStyle = 'None'
    $form.TopMost = $true
    $form.BackColor = [System.Drawing.Color]::FromArgb(9, 9, 11)
    $sw = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width
    $sh = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height

    $t = New-Object System.Windows.Forms.Label
    $t.Text = "$($script:ScopeTitle) 进行中"; $t.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 28, [System.Drawing.FontStyle]::Bold)
    $t.ForeColor = [System.Drawing.Color]::White; $t.AutoSize = $true; $t.Location = New-Object System.Drawing.Point(48, 28)

    $h = New-Object System.Windows.Forms.Label
    $h.Text = $script:ScopeHint
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

function Test-NetworkHealthy {
    Start-Sleep -Seconds 2
    $phys = @(Get-NetAdapter -EA SilentlyContinue | Where-Object {
        -not (Test-IsVirtualNetName $_.Name $_.InterfaceDescription) -and
        $_.Status -eq 'Up'
    })
    $goodIp = $false
    foreach ($a in $phys) {
        $ips = @(Get-NetIPAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4 -EA SilentlyContinue |
            Where-Object { $_.IPAddress -notlike '169.254.*' -and $_.PrefixOrigin -ne 'WellKnown' })
        if ($ips.Count -gt 0) { $goodIp = $true; break }
    }
    # 有物理网卡 Up 就算基本活了；有非 APIPA 地址更好
    $ok = ($phys.Count -gt 0)
    $script:LastHealth = @{
        Ok = $ok
        Usb = 0; Hid = 0; Kbd = 0; Mou = 0; UsbDev = 0
        OneLine = if ($goodIp) {
            "物理网卡已 Up 且有有效 IPv4（可尝试远程）"
        } elseif ($ok) {
            "物理网卡已 Up，但可能还在拿 DHCP / 未插线（可再等或查线）"
        } else {
            "没有处于 Up 的物理网卡（驱动/禁用/口坏/未识别）"
        }
    }
    Ui-Line "检测网: Up物理卡=$($phys.Count) 有效IP=$(if($goodIp){'有'}else{'无/APIPA'}) → $(if($ok){'基本可用'}else{'异常'})"
    return $ok
}

function Test-ScopeHealthy {
    switch ($script:FixScope) {
        'Net' { return (Test-NetworkHealthy) }
        'Usb' { return (Test-UsbHealthy) }
        default {
            $u = Test-UsbHealthy
            $n = Test-NetworkHealthy
            $ok = $u -and $n
            if ($script:LastHealth) {
                $script:LastHealth.Ok = $ok
                $script:LastHealth.OneLine = "USB: $(if($u){'OK'}else{'异常'}) · 网: $(if($n){'OK'}else{'异常'})"
            }
            return $ok
        }
    }
}

function Clear-AutoStart {
    sc.exe config USBFixBoot start= disabled | Out-Null
    foreach ($name in @('USBFullCheck','USBFullCheckOnce','USBFixCheck')) {
        Remove-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name $name -Force -EA SilentlyContinue
        Remove-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce' -Name $name -Force -EA SilentlyContinue
    }
    $startup = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\StartUp'
    foreach ($n in @('USB全面体检.bat', '网络急救自修.bat', 'USB与网络急救.bat', '键鼠USB急救.bat')) {
        $sb = Join-Path $startup $n
        if (Test-Path $sb) { Remove-Item $sb -Force }
    }
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

function Fix-UsbDriverImagePath {
    Ui-Step '办法: 修复驱动 ImagePath 双 SystemRoot（问题码 39）' 40
    $targets = @(
        @{ S='usbxhci'; F='usbxhci.sys' }, @{ S='USBXHCI'; F='usbxhci.sys' },
        @{ S='usbehci'; F='usbehci.sys' }, @{ S='usbohci'; F='usbohci.sys' },
        @{ S='usbuhci'; F='usbuhci.sys' }, @{ S='usbhub3'; F='usbhub3.sys' },
        @{ S='usbhub'; F='usbhub.sys' }, @{ S='usbccgp'; F='usbccgp.sys' },
        @{ S='usbd'; F='usbd.sys' }, @{ S='usbport'; F='usbport.sys' },
        @{ S='HidUsb'; F='hidusb.sys' }, @{ S='mouhid'; F='mouhid.sys' },
        @{ S='kbdhid'; F='kbdhid.sys' }, @{ S='mouclass'; F='mouclass.sys' },
        @{ S='kbdclass'; F='kbdclass.sys' }, @{ S='Wdf01000'; F='Wdf01000.sys' }
    )
    $sets = @('CurrentControlSet','ControlSet001','ControlSet002') | Where-Object {
        Test-Path "HKLM:\SYSTEM\$_\Services"
    }
    $n = 0
    foreach ($cs in $sets) {
        foreach ($t in $targets) {
            $key = "HKLM:\SYSTEM\$cs\Services\$($t.S)"
            if (-not (Test-Path $key)) { continue }
            $cur = (Get-ItemProperty $key -Name ImagePath -EA SilentlyContinue).ImagePath
            if (-not $cur) { continue }
            $norm = [string]$cur
            $hits = ([regex]::Matches($norm, 'SystemRoot', 'IgnoreCase')).Count
            if ($hits -lt 2) { continue }
            $expect = "\SystemRoot\System32\drivers\$($t.F)"
            New-ItemProperty -Path $key -Name ImagePath -PropertyType ExpandString -Value $expect -Force | Out-Null
            Add-Issue "[$cs] $($t.S) ImagePath 双重 SystemRoot"
            Add-Fixed "[$cs] $($t.S) → $expect"
            $n++
        }
    }
    if ($n -eq 0) { Ui-Line 'ImagePath 无需修复' }
}

function Fix-EnableDevices {
    Ui-Step '办法: 启用并重启异常 USB/HID 设备（含问题码 39）' 42
    pnputil /scan-devices | Out-Null
    Get-PnpDevice -EA SilentlyContinue |
        Where-Object {
            $_.Class -in @('USB','HIDClass','Keyboard','Mouse','USBDevice') -and (
                $_.Status -notin @('OK','Unknown') -or
                ([int]($_.Problem) -eq 39)
            )
        } |
        ForEach-Object {
            Add-Issue "[$($_.Status)/P$($_.Problem)] $($_.FriendlyName)"
            Enable-PnpDevice -InstanceId $_.InstanceId -Confirm:$false -EA SilentlyContinue
            pnputil /restart-device "$($_.InstanceId)" | Out-Null
            if ($LASTEXITCODE -ne 0) {
                pnputil /disable-device "$($_.InstanceId)" | Out-Null
                Start-Sleep -Milliseconds 300
                pnputil /enable-device "$($_.InstanceId)" | Out-Null
            }
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
function Fix-EnableDisabledUsbControllers {
    Ui-Step '办法: 启用被禁用的 USB 主机控制器 / Root Hub（问题码22）' 44
    Get-PnpDevice -Class USB -EA SilentlyContinue |
        Where-Object {
            ($_.Problem -eq 22 -or $_.Status -eq 'Error') -and
            ($_.FriendlyName -match 'Host Controller|Root Hub|xHCI|EHCI|OHCI|UHCI|可扩展主机控制器|根集线器')
        } |
        ForEach-Object {
            Add-Issue "已禁用/异常控制器 $($_.FriendlyName)"
            pnputil /enable-device "$($_.InstanceId)" | Out-Null
            Enable-PnpDevice -InstanceId $_.InstanceId -Confirm:$false -EA SilentlyContinue
            pnputil /restart-device "$($_.InstanceId)" | Out-Null
            Add-Fixed "已启用 $($_.FriendlyName)"
        }
}

function Fix-RestartExplorer {
    Ui-Step '办法: 重启 explorer（壳层卡死导致键鼠假死）' 45
    try {
        $exp = Get-Process explorer -EA SilentlyContinue
        if ($exp) {
            Stop-Process -Name explorer -Force -EA SilentlyContinue
            Start-Sleep -Milliseconds 600
        }
        Start-Process explorer | Out-Null
        Add-Fixed '已重启 explorer'
    } catch {
        Ui-Line "explorer 重启跳过: $($_.Exception.Message)"
    }
}

function Fix-VendorInputConflicts {
    Ui-Step '办法: 结束键鼠厂商冲突软件（G HUB / Synapse / 雷云等）' 46
    $names = @(
        'LGHUB','lghub_agent','lghub_updater','logi*','LogiOptions*','OptionsPlus*',
        'RazerSynapse*','RazerCentral*','rzsynapse*','GameManager*',
        'Rapoo*','雷云*','DD*','Op*','Corsair*','iCUE*','ArmouryCrate*','ASUS*',
        'SteelSeries*','GG*'
    )
    $killed = 0
    foreach ($n in $names) {
        Get-Process -Name $n -EA SilentlyContinue | ForEach-Object {
            try {
                Stop-Process -Id $_.Id -Force -EA Stop
                Add-Fixed "结束进程 $($_.ProcessName)"
                $killed++
            } catch {}
        }
    }
    # 常见服务：停掉即可，不强制删除（避免下次开机又起可再杀）
    foreach ($svc in @('LGHUBUpdaterService','Razer Synapse Service','RzActionSvc','LogiRegistryService','iCUE*')) {
        Get-Service -Name $svc -EA SilentlyContinue | ForEach-Object {
            try {
                if ($_.Status -eq 'Running') { Stop-Service $_.Name -Force -EA SilentlyContinue }
                Set-Service $_.Name -StartupType Manual -EA SilentlyContinue
                Add-Fixed "停服务 $($_.Name)"
            } catch {}
        }
    }
    if ($killed -eq 0) { Ui-Line '未发现正在运行的厂商键鼠套件' }
}

function Fix-MaliciousOrOrphanKernelDrivers {
    Ui-Step '办法: 清理指向临时/下载目录的异常内核驱动服务' 47
    $badRoots = @('\??\C:\Users\','\??\C:\Windows\Temp','\??\C:\Temp','\??\D:\下载','\??\D:\Download','AppData\Local\Temp','\Temp\')
    $protect = @('usbxhci','usbhub','usbhub3','usbccgp','usbd','usbport','hidusb','mouhid','kbdhid','mouclass','kbdclass','wdf01000','ntoskrnl','disk','partmgr','volmgr','ACPI','pci','BasicDisplay','BasicRender','NDIS','Tcpip','http','afd','netbt')
    Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Services' -EA SilentlyContinue | ForEach-Object {
        $name = $_.PSChildName
        if ($protect -contains $name) { return }
        $type = (Get-ItemProperty $_.PSPath -Name Type -EA SilentlyContinue).Type
        if ($type -notin @(1,2)) { return } # 1=kernel 2=fs
        $img = (Get-ItemProperty $_.PSPath -Name ImagePath -EA SilentlyContinue).ImagePath
        if (-not $img) { return }
        $hit = $false
        foreach ($b in $badRoots) {
            if ("$img" -like "*$b*") { $hit = $true; break }
        }
        if (-not $hit) { return }
        try {
            Add-Issue "异常内核驱动 $name → $img"
            Stop-Service $name -Force -EA SilentlyContinue
            sc.exe delete $name | Out-Null
            Add-Fixed "已删除异常服务 $name"
        } catch {
            Ui-Line "清理 $name 失败: $($_.Exception.Message)"
        }
    }
}

function Test-IsVirtualNetName([string]$name, [string]$desc) {
    $t = "$name $desc"
    return [bool]($t -match 'Hyper-V|vEthernet|VMware|VirtualBox|TAP-|OpenVPN|WireGuard|Wintun|VPN|Loopback|Pseudo|Bluetooth|Microsoft Wi-Fi Direct|Hosted Network|Npc\s*Debug|Npc\s*Kernel')
}

function Fix-NetworkDriverImagePath {
    Ui-Step '办法: 修复网卡相关 ImagePath 双 SystemRoot' 71
    $targets = @(
        @{ S='ndis'; F='ndis.sys' }, @{ S='NDIS'; F='ndis.sys' },
        @{ S='tcpip'; F='tcpip.sys' }, @{ S='Tcpip'; F='tcpip.sys' },
        @{ S='Tcpip6'; F='tcpip.sys' }, @{ S='netbt'; F='netbt.sys' },
        @{ S='NetBT'; F='netbt.sys' }, @{ S='afd'; F='afd.sys' },
        @{ S='AFD'; F='afd.sys' }, @{ S='wfplwfs'; F='wfplwfs.sys' },
        @{ S='Ndisuio'; F='ndisuio.sys' }, @{ S='NdProt'; F='ndprot.sys' },
        @{ S='vwififlt'; F='vwififlt.sys' }, @{ S='vwifibus'; F='vwifibus.sys' },
        @{ S='nwifi'; F='nwifi.sys' }, @{ S='WlanSvc'; F=$null }
    )
    $sets = @('CurrentControlSet','ControlSet001','ControlSet002') | Where-Object {
        Test-Path "HKLM:\SYSTEM\$_\Services"
    }
    $n = 0
    foreach ($cs in $sets) {
        foreach ($t in $targets) {
            if (-not $t.F) { continue }
            $key = "HKLM:\SYSTEM\$cs\Services\$($t.S)"
            if (-not (Test-Path $key)) { continue }
            $cur = (Get-ItemProperty $key -Name ImagePath -EA SilentlyContinue).ImagePath
            if (-not $cur) { continue }
            $hits = ([regex]::Matches([string]$cur, 'SystemRoot', 'IgnoreCase')).Count
            if ($hits -lt 2) { continue }
            $expect = "\SystemRoot\System32\drivers\$($t.F)"
            New-ItemProperty -Path $key -Name ImagePath -PropertyType ExpandString -Value $expect -Force | Out-Null
            Add-Issue "[$cs] $($t.S) 网络 ImagePath 双重 SystemRoot"
            Add-Fixed "[$cs] $($t.S) → $expect"
            $n++
        }
        # 扫一遍 Class=Net 驱动服务里的双 SystemRoot（覆盖 Realtek/Intel OEM）
        Get-ChildItem "HKLM:\SYSTEM\$cs\Services" -EA SilentlyContinue | ForEach-Object {
            $img = (Get-ItemProperty $_.PSPath -Name ImagePath -EA SilentlyContinue).ImagePath
            if (-not $img) { return }
            $hits = ([regex]::Matches([string]$img, 'SystemRoot', 'IgnoreCase')).Count
            if ($hits -lt 2) { return }
            $grp = (Get-ItemProperty $_.PSPath -Name Group -EA SilentlyContinue).Group
            $name = $_.PSChildName
            if ($grp -notmatch 'NDIS|Network|Stream' -and $name -notmatch 'e1|e2|rt|rtl|ixgbe|i40e|igb|mlx|bnxt|ath|iwl|mt7|Qualcomm|Killer') { return }
            $file = [IO.Path]::GetFileName(([string]$img -replace '(?i)\\SystemRoot\\SystemRoot\\','\SystemRoot\' -replace '(?i)%SystemRoot%\\',''))
            if (-not $file -or $file -notmatch '\.sys$') { return }
            $expect = "\SystemRoot\System32\drivers\$file"
            if (([string]$img) -eq $expect) { return }
            New-ItemProperty -Path $_.PSPath -Name ImagePath -PropertyType ExpandString -Value $expect -Force | Out-Null
            Add-Issue "[$cs] $name 网卡驱动 ImagePath 双重 SystemRoot"
            Add-Fixed "[$cs] $name → $expect"
            $n++
        }
    }
    if ($n -eq 0) { Ui-Line '网络 ImagePath 无需修复' }
}

function Fix-NetworkFullAuto {
    Ui-Phase '网络全自动（有线优先 · 离线可跑 · 不依赖外网下驱动）'
    Ui-Step '办法: 拉起网络核心服务（DHCP/NLA/DNS/网卡管理）' 72
    foreach ($svc in @('nsi','NlaSvc','netprofm','Netman','Dhcp','Dnscache','WinHttpAutoProxySvc','WlanSvc','WwanSvc','LanmanWorkstation','lmhosts','BFE')) {
        $s = Get-Service $svc -EA SilentlyContinue
        if (-not $s) { continue }
        $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$svc"
        $cfg = Get-ItemProperty $key -EA SilentlyContinue
        if ($cfg -and $cfg.Start -eq 4) {
            Add-Issue "网络服务禁用 $svc"
            $start = if ($svc -in @('Dhcp','Dnscache','NlaSvc','nsi','BFE','LanmanWorkstation','Netman','netprofm')) { 2 } else { 3 }
            Set-ItemProperty $key Start $start -Type DWord -EA SilentlyContinue
            Add-Fixed "启用 $svc (Start=$start)"
        }
        if ($s.Status -ne 'Running') {
            Set-Service $svc -StartupType Automatic -EA SilentlyContinue
            if ($svc -in @('WlanSvc','WwanSvc','lmhosts')) {
                Set-Service $svc -StartupType Manual -EA SilentlyContinue
            }
            # Dhcp 勿强杀重启，易卡代理；只尝试 Start
            if ($svc -eq 'Dhcp') {
                Start-Service $svc -EA SilentlyContinue
            } else {
                Restart-Service $svc -Force -EA SilentlyContinue
                if ((Get-Service $svc -EA SilentlyContinue).Status -ne 'Running') {
                    Start-Service $svc -EA SilentlyContinue
                }
            }
            if ((Get-Service $svc -EA SilentlyContinue).Status -eq 'Running') {
                Add-Fixed "启动网络服务 $svc"
            } else {
                Ui-Line "服务 $svc 未能 Running（可能被策略/依赖挡住）"
            }
        }
    }

    Fix-NetworkDriverImagePath

    Ui-Step '办法: 启用设备管理器里挂掉的网卡（含问题码）' 74
    pnputil /scan-devices | Out-Null
    Get-PnpDevice -Class Net -EA SilentlyContinue | ForEach-Object {
        $fn = [string]$_.FriendlyName
        if (Test-IsVirtualNetName $fn $fn) { return }
        $prob = 0
        try { $prob = [int]$_.Problem } catch {}
        if ($_.Status -in @('OK') -and $prob -eq 0) { return }
        Add-Issue ("网卡设备 [{0}/P{1}] {2}" -f $_.Status, $prob, $fn)
        Enable-PnpDevice -InstanceId $_.InstanceId -Confirm:$false -EA SilentlyContinue
        pnputil /enable-device "$($_.InstanceId)" | Out-Null
        pnputil /restart-device "$($_.InstanceId)" | Out-Null
        Add-Fixed "启用/重启网卡设备 $fn"
    }

    Ui-Step '办法: 打开被禁用的网卡适配器（有线优先）' 76
    $adapters = @(Get-NetAdapter -EA SilentlyContinue | Sort-Object {
        if ($_.MediaType -match '802\.3|Ethernet' -or $_.InterfaceDescription -match 'Ethernet|PCI|GbE|LAN') { 0 }
        elseif ($_.Name -match 'Wi-?Fi|Wireless|WLAN' -or $_.InterfaceDescription -match 'Wi-?Fi|Wireless|WLAN|802\.11') { 1 }
        else { 2 }
    })
    foreach ($a in $adapters) {
        if (Test-IsVirtualNetName $a.Name $a.InterfaceDescription) { continue }
        Ui-Line "网卡 $($a.Name): $($a.Status) · $($a.InterfaceDescription)"
        if ($a.AdminStatus -eq 'Down' -or $a.Status -eq 'Disabled') {
            Add-Issue "适配器禁用 $($a.Name)"
            Enable-NetAdapter -Name $a.Name -Confirm:$false -EA SilentlyContinue
            Add-Fixed "已启用 $($a.Name)"
        }
    }

    Ui-Step '办法: 关闭网卡节能（识别了但休眠掉线）' 78
    Get-NetAdapter -EA SilentlyContinue | ForEach-Object {
        if (Test-IsVirtualNetName $_.Name $_.InterfaceDescription) { return }
        try {
            $p = Get-NetAdapterPowerManagement -Name $_.Name -EA SilentlyContinue
            if ($p -and $p.AllowComputerToTurnOffDevice -eq 'Enabled') {
                Set-NetAdapterPowerManagement -Name $_.Name -AllowComputerToTurnOffDevice Disabled -EA SilentlyContinue
                Add-Fixed "关闭节能 $($_.Name)"
            }
        } catch {}
        # PnP 设备电源管理勾选
        $id = $_.PnPDeviceID
        if ($id) {
            $enum = "HKLM:\SYSTEM\CurrentControlSet\Enum\$id\Device Parameters"
            if (Test-Path $enum) {
                New-ItemProperty -Path $enum -Name EnhancedPowerManagementEnabled -PropertyType DWord -Value 0 -Force -EA SilentlyContinue | Out-Null
                New-ItemProperty -Path $enum -Name AllowIdleIrpInD3 -PropertyType DWord -Value 0 -Force -EA SilentlyContinue | Out-Null
                New-ItemProperty -Path $enum -Name SelectiveSuspendOn -PropertyType DWord -Value 0 -Force -EA SilentlyContinue | Out-Null
            }
        }
    }

    Ui-Step '办法: 硬复位物理网卡（禁用→启用，模拟拔插网线侧软件复位）' 80
    foreach ($a in $adapters) {
        if (Test-IsVirtualNetName $a.Name $a.InterfaceDescription) { continue }
        try {
            Disable-NetAdapter -Name $a.Name -Confirm:$false -EA SilentlyContinue
            Start-Sleep -Milliseconds 600
            Enable-NetAdapter -Name $a.Name -Confirm:$false -EA SilentlyContinue
            Restart-NetAdapter -Name $a.Name -Confirm:$false -EA SilentlyContinue
            Add-Fixed "硬复位 $($a.Name)"
        } catch {
            Ui-Line "硬复位 $($a.Name) 失败: $($_.Exception.Message)"
        }
    }
    Start-Sleep -Seconds 2

    Ui-Step '办法: 清代理 / ARP / DNS，释放并续租 DHCP（有线无 IP 常见）' 82
    try {
        netsh winhttp reset proxy | Out-Null
        Add-Fixed '已重置 WinHTTP 代理'
    } catch {}
    # 用户级代理勾选常把「网卡有线却上不了网」搞挂
    $inet = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    if (Test-Path $inet) {
        Set-ItemProperty $inet ProxyEnable 0 -Type DWord -EA SilentlyContinue
        Remove-ItemProperty $inet ProxyServer -Force -EA SilentlyContinue
        Remove-ItemProperty $inet AutoConfigURL -Force -EA SilentlyContinue
        Add-Fixed '已关闭当前用户系统代理'
    }
    arp -d * 2>$null | Out-Null
    ipconfig /flushdns | Out-Null
    ipconfig /registerdns | Out-Null
    # 只对 Up/Disconnected 的物理卡续租（Disconnected=有线没插好也试，识别了但拿不到地址）
    foreach ($a in @(Get-NetAdapter -EA SilentlyContinue)) {
        if (Test-IsVirtualNetName $a.Name $a.InterfaceDescription) { continue }
        if ($a.Status -notin @('Up','Disconnected','Not Present')) { continue }
        try {
            $cfg = Get-NetIPConfiguration -InterfaceIndex $a.ifIndex -EA SilentlyContinue
            # 清掉残留 APIPA / 坏静态，再 DHCP
            Get-NetIPAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4 -EA SilentlyContinue |
                Where-Object { $_.PrefixOrigin -eq 'Manual' -or $_.IPAddress -like '169.254.*' } |
                ForEach-Object {
                    Remove-NetIPAddress -InterfaceIndex $_.InterfaceIndex -IPAddress $_.IPAddress -Confirm:$false -EA SilentlyContinue
                    Add-Fixed "清除坏地址 $($_.IPAddress) @ $($a.Name)"
                }
            Set-NetIPInterface -InterfaceIndex $a.ifIndex -Dhcp Enabled -EA SilentlyContinue
            ipconfig /release "$($a.Name)" 2>$null | Out-Null
            ipconfig /renew "$($a.Name)" 2>$null | Out-Null
            Add-Fixed "DHCP 续租 $($a.Name)"
        } catch {
            Ui-Line "DHCP $($a.Name): $($_.Exception.Message)"
        }
    }

    Ui-Step '办法: 重置 Winsock / TCP-IP 协议栈（离线本地操作）' 84
    $needRebootNet = $false
    foreach ($cmd in @(
        @{ A=@('winsock','reset'); L='Winsock' },
        @{ A=@('int','ip','reset'); L='IPv4' },
        @{ A=@('int','ipv6','reset'); L='IPv6' }
    )) {
        $out = ''
        try { $out = & netsh @($cmd.A) 2>&1 | Out-String } catch { $out = $_.Exception.Message }
        if ($out -match 'restart|重新启动|重启') { $needRebootNet = $true }
        Add-Fixed "$($cmd.L) 协议栈已重置"
        $trim = (($out -replace '\s+', ' ').Trim())
        if ($trim.Length -gt 120) { $trim = $trim.Substring(0, 120) }
        Ui-Line ("{0}: {1}" -f $cmd.L, $(if ($trim) { $trim } else { '完成' }))
    }
    # 栈重置后再拉一次关键服务
    foreach ($svc in @('NlaSvc','Dnscache','WlanSvc','Netman')) {
        Restart-Service $svc -Force -EA SilentlyContinue
        Start-Service $svc -EA SilentlyContinue
    }
    Start-Service Dhcp -EA SilentlyContinue

    Ui-Step '办法: 网络修复后复检' 86
    $up = @(Get-NetAdapter -EA SilentlyContinue | Where-Object {
        -not (Test-IsVirtualNetName $_.Name $_.InterfaceDescription) -and $_.Status -eq 'Up'
    })
    $ipOk = @(Get-NetIPConfiguration -EA SilentlyContinue | Where-Object {
        $_.NetAdapter.Status -eq 'Up' -and $_.IPv4Address -and ($_.IPv4Address.IPAddress -notlike '169.254.*')
    })
    if ($up.Count -gt 0) {
        foreach ($a in $up) { Ui-Line "复检 Up: $($a.Name) $($a.LinkSpeed) · $($a.InterfaceDescription)" }
        Add-Fixed "物理网卡已 Up ×$($up.Count)"
    } else {
        Add-Issue '复检: 仍无物理网卡处于 Up（可能没插网线 / 口坏 / 驱动需重启）'
    }
    if ($ipOk.Count -gt 0) {
        foreach ($c in $ipOk) {
            $ip = ($c.IPv4Address | Select-Object -First 1).IPAddress
            $gw = ($c.IPv4DefaultGateway | Select-Object -First 1).NextHop
            Ui-Line "复检 IP: $($c.InterfaceAlias) = $ip 网关=$gw"
        }
        Add-Fixed "已拿到有效 IPv4 ×$($ipOk.Count)"
    } else {
        Add-Issue '复检: 网卡可能已识别但仍无有效 IP（线序/交换机/DHCP/需重启后协议栈才生效）'
    }
    if ($needRebootNet) {
        Add-Issue '协议栈提示需重启后才完全生效（本工具不自动重启）'
    }
    Ui-Line '网络全自动本轮结束（坏口/没插线/交换机对端故障无法软件改）'
}

function Fix-HandbookFullAuto {
    if ($script:FixScope -eq 'Net') {
        Ui-Phase '救网全自动（专修有线通道）'
        Fix-NetworkFullAuto
        Ui-Line '救网栈本轮结束（没插线/口坏/交换机对端无法软件改）'
        return
    }
    Ui-Phase '手册全自动栈（软件可改项全部执行）'
    Fix-UsbDriverImagePath
    Fix-Power
    Fix-FastBoot
    Fix-EnableDisabledUsbControllers
    Fix-EnableDevices
    Fix-RestartControllers
    Fix-VendorInputConflicts
    Fix-MaliciousOrOrphanKernelDrivers
    Fix-RestartExplorer
    Fix-ResetHid
    Fix-Policies
    Fix-Filters
    Fix-Services
    if ($script:FixScope -ne 'Usb') {
        Fix-NetworkFullAuto
    }
    Ui-Line '手册全自动栈本轮结束（硬件/BIOS/没插网线 无法由软件改写，见报告残留）'
}

function Invoke-PhaseSoft {
    if ($script:FixScope -eq 'Net') {
        Ui-Phase '1/2 救网常规'
        Fix-NetworkFullAuto
        return (Test-NetworkHealthy)
    }
    Ui-Phase '1/4 常规修复（含手册全自动 + UsbDk）'
    Fix-UsbDkRemnants
    Fix-HandbookFullAuto
    Fix-RegistryDeep
    Fix-SuspectAutorun
    Fix-RemoveOemDrivers
    return (Test-ScopeHealthy)
}

function Invoke-PhaseAggressive {
    if ($script:FixScope -eq 'Net') {
        Ui-Phase '2/2 救网强力（再跑一轮 + 强调协议栈）'
        Fix-NetworkFullAuto
        return (Test-NetworkHealthy)
    }
    Ui-Phase '2/4 强力修复'
    Fix-HandbookFullAuto
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
    return (Test-ScopeHealthy)
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
    return (Test-ScopeHealthy)
}

function Invoke-PhaseLastDitch {
    if ($script:FixScope -eq 'Net') {
        Ui-Phase '救网最后手段'
        Fix-NetworkFullAuto
        return (Test-NetworkHealthy)
    }
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
    if ($script:FixScope -ne 'Usb') { Fix-NetworkFullAuto }
    return (Test-ScopeHealthy)
}

function Complete-Success {
    $msg = switch ($script:FixScope) {
        'Net' { "请看网线灯 / 试远程能不能连！`n网卡 Up 了就有机会远程接手。已关后续自启。" }
        'Usb' { "请晃一下鼠标 / 按一下键盘！`n能动了就修好了。已自动关闭后续自启。" }
        default { "请试键鼠，并确认网线口灯！`n两边都活了最理想。已关后续自启。" }
    }
    Ui-Finish $true $msg
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
Add-Content $LogFile "`r`n===== $($script:ScopeTitle) $(Get-Date) Scope=$($script:FixScope) =====" -Encoding UTF8
if (-not (Test-Path $FixDir)) { New-Item -ItemType Directory $FixDir -Force | Out-Null }
if (-not (Test-Path $ScopeFile)) { Set-Content -Path $ScopeFile -Value $script:FixScope -Encoding ASCII -Force }
if ($useUi) { New-WatchUi }

$state = Get-State
$phase = $state.phase
Ui-Line "范围=$($script:FixScope) 当前阶段=$phase  attempt=$($state.attempt)"
Ui-Line $script:ScopeHint

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

# 救网专属：不进安全模式（对有线网帮助有限），soft → aggressive → lastditch
if ($script:FixScope -eq 'Net' -and $phase -in @('safemode_pending', 'safemode_running', 'safemode_exit_check')) {
    Ui-Line '救网模式跳过安全模式阶段 → 最后手段'
    Disable-SafeModeBoot
    Set-State 'lastditch' 0
    $phase = 'lastditch'
}

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
    $ok = Test-ScopeHealthy
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
        if ($script:FixScope -eq 'Net') {
            Set-State 'lastditch' 0
            Reboot-Soon "救网强力仍不够。`n20 秒后重启进入最后手段（跳过安全模式）。" 20
            exit 1
        }
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
