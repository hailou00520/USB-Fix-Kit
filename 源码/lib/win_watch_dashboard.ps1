# 干看着总控台 — 进 Windows 后自动全屏，无需键鼠
# 同时：拉起远程 + 显示本机 IP（大字）+ 后台跑穷尽 USB 修复
param(
    [string]$RemoteExe = '',
    [switch]$SkipRemote,
    [switch]$SkipFix
)

$ErrorActionPreference = 'SilentlyContinue'
$drive = if ($env:SystemDrive) { $env:SystemDrive } else { 'C:' }
$FixDir = Join-Path $drive 'Windows\USBFix'
$LogFile = Join-Path $drive 'usb_watch_dashboard.log'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

function Write-Log($m) {
    Add-Content $LogFile "[$(Get-Date -Format 'HH:mm:ss')] $m" -Encoding UTF8
}

function Get-LanIps {
    $list = @()
    try {
        Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object { $_.IPAddress -notlike '127.*' -and $_.PrefixOrigin -ne 'WellKnown' } |
            ForEach-Object { $list += $_.IPAddress }
    } catch {}
    if ($list.Count -eq 0) {
        try {
            Get-CimInstance Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=True' |
                ForEach-Object { foreach ($ip in $_.IPAddress) { if ($ip -match '^\d+\.\d+') { $list += $ip } } }
        } catch {}
    }
    return @($list | Select-Object -Unique)
}

function Find-RemoteExe {
    if ($RemoteExe -and (Test-Path $RemoteExe)) { return $RemoteExe }
    foreach ($c in @(
        'C:\Tools\RemoteFix\ToDesk_Lite.exe',
        'C:\Tools\RemoteFix\ToDesk.exe',
        'C:\Tools\RemoteFix\SunloginClient.exe',
        'C:\Tools\RemoteFix\Sunlogin.exe'
    )) { if (Test-Path $c) { return $c } }
    $hit = Get-ChildItem 'C:\Tools\RemoteFix' -Filter '*.exe' -Recurse -EA SilentlyContinue |
        Where-Object { $_.Name -match 'ToDesk|Sunlogin|Oray' } |
        Select-Object -First 1
    if (-not $hit) {
        $hit = Get-ChildItem 'C:\Tools\RemoteFix' -Filter '*.exe' -Recurse -EA SilentlyContinue | Select-Object -First 1
    }
    if ($hit) { return $hit.FullName }
    return $null
}

# ---------- UI ----------
$form = New-Object System.Windows.Forms.Form
$form.Text = '请干看着 — 无需操作'
$form.WindowState = 'Maximized'
$form.FormBorderStyle = 'None'
$form.TopMost = $true
$form.BackColor = [System.Drawing.Color]::FromArgb(9, 9, 11)
$sw = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width

$title = New-Object System.Windows.Forms.Label
$title.Text = '请干看着，不要点任何东西'
$title.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 32, [System.Drawing.FontStyle]::Bold)
$title.ForeColor = [System.Drawing.Color]::White
$title.AutoSize = $true
$title.Location = New-Object System.Drawing.Point(48, 36)

$sub = New-Object System.Windows.Forms.Label
$sub.Text = "系统在自动修 USB；若修不好会尝试让远程待命。别拔电源。"
$sub.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 14)
$sub.ForeColor = [System.Drawing.Color]::FromArgb(161,161,170)
$sub.AutoSize = $true
$sub.Location = New-Object System.Drawing.Point(52, 100)

$lblIp = New-Object System.Windows.Forms.Label
$lblIp.Text = '正在获取有线网 IP…'
$lblIp.Font = New-Object System.Drawing.Font('Consolas', 36, [System.Drawing.FontStyle]::Bold)
$lblIp.ForeColor = [System.Drawing.Color]::FromArgb(34, 197, 94)
$lblIp.AutoSize = $false
$lblIp.Width = $sw - 100
$lblIp.Height = 120
$lblIp.Location = New-Object System.Drawing.Point(48, 160)

$lblRemote = New-Object System.Windows.Forms.Label
$lblRemote.Text = '远程：准备中…'
$lblRemote.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 18)
$lblRemote.ForeColor = [System.Drawing.Color]::FromArgb(96, 165, 250)
$lblRemote.AutoSize = $false
$lblRemote.Width = $sw - 100
$lblRemote.Height = 80
$lblRemote.Location = New-Object System.Drawing.Point(48, 290)

$lblFix = New-Object System.Windows.Forms.Label
$lblFix.Text = 'USB 修复：尚未开始'
$lblFix.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 16)
$lblFix.ForeColor = [System.Drawing.Color]::FromArgb(250, 204, 21)
$lblFix.AutoSize = $false
$lblFix.Width = $sw - 100
$lblFix.Height = 60
$lblFix.Location = New-Object System.Drawing.Point(48, 380)

$list = New-Object System.Windows.Forms.ListBox
$list.Font = New-Object System.Drawing.Font('Consolas', 11)
$list.BackColor = [System.Drawing.Color]::FromArgb(24,24,27)
$list.ForeColor = [System.Drawing.Color]::FromArgb(212,212,216)
$list.BorderStyle = 'None'
$list.Location = New-Object System.Drawing.Point(48, 450)
$list.Width = $sw - 100
$list.Height = [Math]::Max(200, [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height - 520)

$form.Controls.AddRange(@($title,$sub,$lblIp,$lblRemote,$lblFix,$list))

function Add-Line([string]$t) {
    Write-Log $t
    $list.Items.Add("$(Get-Date -Format 'HH:mm:ss')  $t")
    $list.TopIndex = $list.Items.Count - 1
    $form.Refresh()
    [System.Windows.Forms.Application]::DoEvents()
}

$form.Show()
$form.Refresh()
Add-Line '总控台已启动（干看着模式）'

# 1) 拉远程
$rex = $null
if (-not $SkipRemote) {
    $rex = Find-RemoteExe
    if ($rex) {
        Add-Line "启动远程: $rex"
        try { Unblock-File -Path $rex -EA SilentlyContinue } catch {}
        try { Get-ChildItem (Split-Path $rex) -Recurse -Force -EA SilentlyContinue | Unblock-File -EA SilentlyContinue } catch {}
        Start-Process -FilePath $rex -ErrorAction SilentlyContinue
        $lblRemote.Text = "远程已启动：$([IO.Path]::GetFileName($rex))`n请用手机/另一台电脑打开对应 App 连接本机。`n设备码一般在远程软件窗口上，可扫一眼屏幕角落。"
        try { [Console]::Beep(700,150); [Console]::Beep(900,150) } catch {}
    } else {
        $lblRemote.Text = '未部署远程软件（可忽略）。USB 自动修复仍会继续。'
        Add-Line '未找到远程 exe'
    }
}

# 2) 后台穷尽修复
$fixJob = $null
if (-not $SkipFix) {
    $ps1 = Join-Path $FixDir 'win_usb_fullcheck.ps1'
    if (Test-Path $ps1) {
        $lblFix.Text = 'USB 修复：后台运行中（全屏修复窗可能一并弹出）…'
        Add-Line '启动穷尽 USB 修复 -Watch'
        # 另开窗口跑修复（它自己有全屏进度）；总控台继续显示 IP
        Start-Process powershell.exe -ArgumentList @(
            '-NoProfile','-ExecutionPolicy','Bypass','-WindowStyle','Normal',
            '-File', $ps1, '-Watch'
        ) -ErrorAction SilentlyContinue
    } else {
        $lblFix.Text = 'USB 修复脚本不存在（仅远程待命）'
        Add-Line '缺少 win_usb_fullcheck.ps1'
    }
}

# 3) 循环刷新 IP + 保活蜂鸣，永不要求点击
$tick = 0
while ($form.Visible) {
    $tick++
    $ips = Get-LanIps
    if ($ips.Count -gt 0) {
        $lblIp.Text = "本机 IP（有线优先）：`n" + ($ips -join '   ')
    } else {
        $lblIp.Text = "还没拿到 IP… 请确认插了网线。`n（无线/USB 网卡多半已经挂了）"
    }
    if ($tick % 30 -eq 0) {
        Add-Line '仍在运行中 — 你继续干看着即可'
        try { [Console]::Beep(520, 120) } catch {}
    }
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Seconds 2
}
