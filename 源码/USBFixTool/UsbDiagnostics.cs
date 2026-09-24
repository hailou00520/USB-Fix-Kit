using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace USBFixTool;

/// <summary>完整只读体检结果</summary>
public sealed class DiagnosisReport
{
    public List<string> Critical { get; } = new();
    public List<string> Attention { get; } = new();
    public int CheckedItems { get; set; }

    public int ProblemCount => Critical.Count;
}

/// <summary>完整只读检查：覆盖服务/过滤/策略/设备/电源等，不修改系统</summary>
public static class UsbDiagnostics
{
    private static readonly string[] BadServices =
    {
        "UsbDk", "usbdk", "USBDk", "usbdkmon", "UsbDkHelper", "hrdevmon", "HRDevMon", "UsbDkRuntime"
    };

    private static readonly string[] UsbRelatedServices =
    {
        "usbxhci", "usbhub", "usbhub3", "usbccgp", "USBSTOR", "usbuhci", "usbehci", "usbport",
        "HidUsb", "mouhid", "kbdhid", "kbdclass", "mouclass", "HidClass",
        "PlugPlay", "Wdf01000", "WUDFRd", "BasicDisplay", "BasicRender"
    };

    /// <summary>修复脚本常用目标值（仅作提示对比，不等于故障）</summary>
    private static readonly Dictionary<string, int> SuggestedStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["usbxhci"] = 0,
        ["usbhub"] = 1,
        ["usbhub3"] = 1,
        ["usbccgp"] = 1,
        ["PlugPlay"] = 2,
        ["Wdf01000"] = 0,
        ["kbdclass"] = 3,
        ["mouclass"] = 3,
        ["HidUsb"] = 3,
        ["kbdhid"] = 3,
        ["mouhid"] = 3,
    };

    private static readonly (string Guid, string Name)[] ClassGuids =
    {
        ("{36fc9e60-c465-11cf-8056-444553540000}", "USB"),
        ("{745a17a0-74d3-11d0-b6fe-00a0c90f57da}", "HID"),
        ("{4d36e96f-e325-11ce-bfc1-444553540000}", "鼠标"),
        ("{4d36e96b-e325-11ce-bfc1-444553540000}", "键盘"),
        ("{88BAE032-5A81-49f0-BC3D-A4FF138216D6}", "USB设备"),
    };

    private static string StartLabel(int start) => start switch
    {
        0 => "引导",
        1 => "系统",
        2 => "自动",
        3 => "手动",
        4 => "禁用",
        _ => "未知"
    };

    public static DiagnosisReport ScanOffline(string offlineSysRoot, string? offlineSoftRoot, Action<string> log)
    {
        var r = new DiagnosisReport();
        var sets = OfflineUsbRegistry.EnumerateControlSets(offlineSysRoot).ToList();
        log("══ 完整检查（PE 离线 / 只读）══");
        log($"ControlSet: {string.Join(", ", sets)}");
        log("分级: ⚠严重 = 键鼠会挂；◇开机风险 = 启动类型不对，急救箱会自动写死");
        log("");

        foreach (var cs in sets)
        {
            log($"── {cs} · 危险残留服务 ──");
            var foundBad = false;
            foreach (var svc in BadServices)
            {
                r.CheckedItems++;
                if (RegKeyExists($@"{offlineSysRoot}\{cs}\Services\{svc}"))
                {
                    foundBad = true;
                    AddCritical(r, log, $"[{cs}] 残留危险服务: {svc}");
                }
            }
            if (!foundBad) log("  · 未发现 UsbDk/hrdevmon 残留");

            log($"── {cs} · USB/HID 服务 Start ──");
            foreach (var svc in UsbRelatedServices)
            {
                var path = $@"{offlineSysRoot}\{cs}\Services\{svc}";
                if (!RegKeyExists(path)) continue;
                r.CheckedItems++;
                var start = QueryDword(path, "Start");
                if (start == null)
                {
                    log($"  · {svc} 无 Start 值");
                    continue;
                }
                ReportStart(r, log, $"[{cs}] {svc}", start.Value, svc);
            }

            log($"── {cs} · 驱动 ImagePath（双 SystemRoot）──");
            CheckOfflineImagePaths(r, log, offlineSysRoot, cs);

            log($"── {cs} · 类过滤驱动 Upper/LowerFilters ──");
            foreach (var (guid, name) in ClassGuids)
            {
                var classPath = $@"{offlineSysRoot}\{cs}\Control\Class\{guid}";
                foreach (var filter in new[] { "UpperFilters", "LowerFilters" })
                {
                    r.CheckedItems++;
                    var val = QueryMultiSz(classPath, filter);
                    if (string.IsNullOrWhiteSpace(val))
                    {
                        log($"  · {name} {filter}: (空)");
                        continue;
                    }
                    var pretty = val.Replace('\0', ' ').Trim();
                    if (ContainsBadFilter(pretty))
                        AddCritical(r, log, $"[{cs}] {name} {filter} 含 UsbDk/hrdevmon: {pretty}");
                    else
                        log($"  · {name} {filter}: {pretty}");
                }
            }

            r.CheckedItems++;
            if (RegValueExists($@"{offlineSysRoot}\{cs}\Control\Keyboard Layout", "Scancode Map"))
                AddCritical(r, log, $"[{cs}] 存在 Scancode Map（键盘扫描码重映射）");
            else
                log("  · Scancode Map: 无");
        }

        if (!string.IsNullOrEmpty(offlineSoftRoot))
        {
            log("── 策略 / 其它 ──");
            CheckPolicyKeys(r, log, offlineSoftRoot, offline: true);
        }

        return r;
    }

    public static DiagnosisReport ScanLive(Action<string> log)
    {
        var r = new DiagnosisReport();
        log("══ 完整检查（当前 Windows / 只读）══");
        log("分级: ⚠严重 = 键鼠会挂；◇开机风险 = 启动类型不对，急救箱会自动写死");
        log("");

        log("── 危险残留服务 ──");
        using (var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
        {
            var foundBad = false;
            if (services != null)
            {
                foreach (var svc in BadServices)
                {
                    r.CheckedItems++;
                    using var k = services.OpenSubKey(svc);
                    if (k != null)
                    {
                        foundBad = true;
                        AddCritical(r, log, $"残留危险服务: {svc}");
                    }
                }
            }
            if (!foundBad) log("  · 未发现 UsbDk/hrdevmon 残留");

            log("── USB/HID 服务 Start（完整列表）──");
            if (services != null)
            {
                foreach (var svc in UsbRelatedServices)
                {
                    using var k = services.OpenSubKey(svc);
                    if (k == null) continue;
                    r.CheckedItems++;
                    var start = k.GetValue("Start") as int?;
                    if (start == null)
                    {
                        log($"  · {svc} 无 Start 值");
                        continue;
                    }
                    ReportStart(r, log, svc, start.Value, svc);
                }
            }
        }

        log("── 驱动 ImagePath（双 SystemRoot / 问题码 39）──");
        CheckLiveImagePaths(r, log);

        log("── 类过滤驱动 Upper/LowerFilters ──");
        foreach (var (guid, name) in ClassGuids)
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{guid}");
            foreach (var filter in new[] { "UpperFilters", "LowerFilters" })
            {
                r.CheckedItems++;
                if (k == null)
                {
                    log($"  · {name} {filter}: (类不存在)");
                    continue;
                }
                var raw = k.GetValue(filter);
                string text = raw switch
                {
                    string[] arr => string.Join(" ", arr),
                    string s => s,
                    _ => ""
                };
                if (string.IsNullOrWhiteSpace(text))
                {
                    log($"  · {name} {filter}: (空)");
                    continue;
                }
                if (ContainsBadFilter(text))
                    AddCritical(r, log, $"{name} {filter} 含 UsbDk/hrdevmon: {text}");
                else
                    log($"  · {name} {filter}: {text}");
            }
        }

        log("── 键盘 / 策略 / 注入 ──");
        r.CheckedItems++;
        using (var layout = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Keyboard Layout"))
        {
            if (layout?.GetValue("Scancode Map") != null)
                AddCritical(r, log, "存在 Scancode Map（键盘扫描码重映射）");
            else
                log("  · Scancode Map: 无");
        }

        CheckPolicyKeys(r, log, @"SOFTWARE", offline: false);

        r.CheckedItems++;
        using (var appInit = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows"))
        {
            var dlls = appInit?.GetValue("AppInit_DLLs") as string;
            if (!string.IsNullOrWhiteSpace(dlls))
                AddAttention(r, log, $"AppInit_DLLs = {dlls}（全局 DLL 注入，偶发影响输入）");
            else
                log("  · AppInit_DLLs: 空");
        }

        log("── 电源 / 快速启动 / USB 节能 ──");
        r.CheckedItems++;
        using (var pwr = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power"))
        {
            var hiber = pwr?.GetValue("HiberbootEnabled") as int?;
            if (hiber == 1)
                AddAttention(r, log, "快速启动已开启（偶发导致 USB 设备异常，可在修复时关闭）");
            else
                log($"  · 快速启动 HiberbootEnabled = {hiber ?? 0}");
        }

        CheckUsbPowerSaving(r, log);

        log("── PnP 设备（问题码 / 主机控制器 / 真实键鼠）──");
        ScanPnpDevices(r, log);

        log("── Kernel-PnP 驱动加载失败日志（近7天）──");
        ScanKernelPnpLoadFailures(r, log);

        log("── 启动文件夹远程 exe（安全警告相关）──");
        ScanStartupRemoteExes(r, log);

        log("── 网卡（有线优先：识别了但不通）──");
        ScanLiveNetwork(r, log);

        return r;
    }

    private static void ScanLiveNetwork(DiagnosisReport r, Action<string> log)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell",
                "-NoProfile -Command \"" +
                "$ErrorActionPreference='SilentlyContinue'; " +
                "function V($n,$d){ return [bool](\"$n $d\" -match 'Hyper-V|vEthernet|VMware|VirtualBox|TAP-|OpenVPN|WireGuard|Wintun|VPN|Loopback|Pseudo|Bluetooth|Wi-Fi Direct') }; " +
                "$ads=@(Get-NetAdapter | Where-Object { -not (V $_.Name $_.InterfaceDescription) }); " +
                "$up=@($ads | Where-Object Status -eq 'Up'); " +
                "$dis=@($ads | Where-Object Status -eq 'Disabled'); " +
                "$ip=@($ads | Where-Object Status -eq 'Up' | ForEach-Object { " +
                "  $c=Get-NetIPConfiguration -InterfaceIndex $_.ifIndex; " +
                "  $a=@($c.IPv4Address | ForEach-Object IPAddress | Where-Object { $_ -and $_ -notlike '169.254.*' }); " +
                "  if($a.Count){ [PSCustomObject]@{ n=$_.Name; ip=$a[0]; gw=$(($c.IPv4DefaultGateway|Select-Object -First 1).NextHop) } } " +
                "}); " +
                "$svcBad=@('Dhcp','Dnscache','NlaSvc','Netman','nsi' | ForEach-Object { $s=Get-Service $_ -EA SilentlyContinue; if($s -and $s.Status -ne 'Running'){ $_ } }); " +
                "Write-Output ('AD_TOTAL=' + $ads.Count); " +
                "Write-Output ('AD_UP=' + $up.Count); " +
                "Write-Output ('AD_DIS=' + $dis.Count); " +
                "Write-Output ('IP_OK=' + @($ip).Count); " +
                "Write-Output ('SVC_BAD=' + ($svcBad -join ',')); " +
                "foreach($a in ($ads | Select-Object -First 10)){ Write-Output ('AD|' + $a.Status + '|' + $a.Name + '|' + $a.InterfaceDescription) }; " +
                "foreach($i in $ip){ Write-Output ('IP|' + $i.n + '|' + $i.ip + '|' + $i.gw) }" +
                "\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            var output = p?.StandardOutput.ReadToEnd() ?? "";
            p?.WaitForExit(30000);
            r.CheckedItems += 3;

            int total = 0, up = 0, dis = 0, ipOk = 0;
            string svcBad = "";
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("AD_TOTAL=", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line.AsSpan(9), out var a)) total = a;
                else if (line.StartsWith("AD_UP=", StringComparison.OrdinalIgnoreCase) &&
                         int.TryParse(line.AsSpan(6), out var b)) up = b;
                else if (line.StartsWith("AD_DIS=", StringComparison.OrdinalIgnoreCase) &&
                         int.TryParse(line.AsSpan(7), out var c)) dis = c;
                else if (line.StartsWith("IP_OK=", StringComparison.OrdinalIgnoreCase) &&
                         int.TryParse(line.AsSpan(6), out var d)) ipOk = d;
                else if (line.StartsWith("SVC_BAD=", StringComparison.OrdinalIgnoreCase))
                    svcBad = line.Length > 8 ? line[8..] : "";
                else if (line.StartsWith("AD|", StringComparison.OrdinalIgnoreCase))
                    log("  · 网卡 " + line[3..].Replace("|", " · "));
                else if (line.StartsWith("IP|", StringComparison.OrdinalIgnoreCase))
                    log("  · 地址 " + line[3..].Replace("|", " · "));
            }

            log($"  · 物理网卡: {total}，Up={up}，禁用={dis}，有效IPv4={ipOk}");
            if (!string.IsNullOrWhiteSpace(svcBad))
                AddCritical(r, log, $"网络关键服务未运行: {svcBad}");
            if (total == 0)
                AddAttention(r, log, "未枚举到物理网卡（驱动未装或总线异常）");
            else if (dis > 0 && up == 0)
                AddCritical(r, log, "网卡全部被禁用（识别了但被软件关掉）— 全面修复会自动启用");
            else if (up == 0)
                AddAttention(r, log, "网卡已识别但无一 Up（常见：没插网线 / 口无链路 / 节能休眠）");
            else if (ipOk == 0)
                AddCritical(r, log, "网卡已 Up 但仍无有效 IP（DHCP/协议栈/代理问题）— 全面修复会离线续租并重置栈");
            else
                log("  · 网卡链路与地址看起来正常");
        }
        catch (Exception ex)
        {
            AddAttention(r, log, "网卡枚举失败: " + ex.Message);
        }
    }

    private static void CheckLiveImagePaths(DiagnosisReport r, Action<string> log)
    {
        var bad = 0;
        foreach (var (svc, sys) in UsbDriverImagePath.Targets)
        {
            r.CheckedItems++;
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{svc}");
                if (k == null) continue;
                var img = k.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                if (!UsbDriverImagePath.NeedsRepair(img, sys, out var why)) continue;
                bad++;
                AddCritical(r, log, $"{svc} ImagePath 异常（{why}）→ 问题码39风险: {img}");
            }
            catch { /* ignore */ }
        }
        if (bad == 0) log("  · USB/HID 相关 ImagePath 正常（无双 SystemRoot / 文件缺失）");
    }

    private static void CheckOfflineImagePaths(DiagnosisReport r, Action<string> log, string offlineSysRoot, string cs)
    {
        var bad = 0;
        foreach (var (svc, _) in UsbDriverImagePath.Targets)
        {
            var path = $@"{offlineSysRoot}\{cs}\Services\{svc}";
            if (!RegKeyExists(path)) continue;
            r.CheckedItems++;
            var img = QueryRegValue(path, "ImagePath");
            if (!UsbDriverImagePath.IsCorrupt(img)) continue;
            bad++;
            AddCritical(r, log, $"[{cs}] {svc} ImagePath 双重 SystemRoot: {img}");
        }
        if (bad == 0) log("  · ImagePath 未发现双 SystemRoot");
    }

    private static void ReportStart(DiagnosisReport r, Action<string> log, string label, int start, string svcKey)
    {
        if (start == 4)
        {
            AddCritical(r, log, $"{label} 已被禁用 (Start=4)");
            return;
        }

        if (SuggestedStarts.TryGetValue(svcKey, out var suggest) && start != suggest)
        {
            var msg = $"{label} Start={start}({StartLabel(start)})，与开机必起 {suggest}({StartLabel(suggest)}) 不符 → 将自动写入";
            r.Attention.Add(msg);
            log($"  ◇ {label} Start={start} ({StartLabel(start)}) → 应改为 {suggest}({StartLabel(suggest)})");
        }
        else
        {
            log($"  · {label} Start={start} ({StartLabel(start)}) — 正常");
        }
    }

    private static void CheckPolicyKeys(DiagnosisReport r, Action<string> log, string softRoot, bool offline)
    {
        if (offline)
        {
            var paths = new[]
            {
                @"Policies\Microsoft\Windows\DeviceInstall\Restrictions",
                @"Policies\Microsoft\Windows\RemovableStorageDevices",
                @"Policies\Microsoft\Windows\DeviceInstall\Restrictions\DenyDeviceClasses",
                @"Policies\Microsoft\Windows\DeviceInstall\Restrictions\DenyDeviceIDs",
            };
            foreach (var rel in paths)
            {
                r.CheckedItems++;
                var full = $@"HKLM\PEOFFLINE_SOFT\{rel}";
                if (RegKeyExists(full))
                    AddCritical(r, log, $"存在策略: {rel}");
                else
                    log($"  · 策略 {rel}: 无");
            }
            return;
        }

        var livePaths = new[]
        {
            @"SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions",
            @"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices",
            @"SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions\DenyDeviceClasses",
            @"SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions\DenyDeviceIDs",
        };
        foreach (var p in livePaths)
        {
            r.CheckedItems++;
            using var k = Registry.LocalMachine.OpenSubKey(p);
            if (k != null)
                AddCritical(r, log, $"存在策略: {p}");
            else
                log($"  · 策略 {p.Replace(@"SOFTWARE\Policies\Microsoft\Windows\", "")}: 无");
        }
    }

    private static void CheckUsbPowerSaving(DiagnosisReport r, Action<string> log)
    {
        // 手册 2.2 / 3.1：USB 选择性暂停 + Root Hub AllowIdleIrpInD3
        try
        {
            r.CheckedItems++;
            var psi = new ProcessStartInfo("powercfg", "/query SCHEME_CURRENT SUB_USB")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            var output = p?.StandardOutput.ReadToEnd() ?? "";
            p?.WaitForExit(8000);
            // 当前电源设置索引：0x00000001 = 开启选择性暂停（坏），0x00000000 = 关闭（好）
            var enabled = false;
            foreach (var line in output.Split('\n'))
            {
                if (line.Contains("当前交流电源设置索引", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Current AC Power Setting Index", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("当前直流电源设置索引", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Current DC Power Setting Index", StringComparison.OrdinalIgnoreCase))
                {
                    if (line.Contains("0x00000001", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("0x1", StringComparison.OrdinalIgnoreCase))
                        enabled = true;
                }
            }
            if (enabled)
                AddAttention(r, log, "USB 选择性暂停已开启（使用中可能突然断连；全面修复会关闭）");
            else
                log("  · USB 选择性暂停: 已关闭或未启用");
        }
        catch (Exception ex)
        {
            log("  · USB 选择性暂停查询跳过: " + ex.Message);
        }

        try
        {
            r.CheckedItems++;
            var psi = new ProcessStartInfo("powershell",
                "-NoProfile -Command \"" +
                "$ErrorActionPreference='SilentlyContinue'; " +
                "$n=0; Get-ChildItem 'HKLM:\\SYSTEM\\CurrentControlSet\\Enum\\USB' -Recurse -EA SilentlyContinue | " +
                "  Where-Object { $_.PSChildName -eq 'Device Parameters' } | ForEach-Object { " +
                "    $v=(Get-ItemProperty $_.PSPath -Name AllowIdleIrpInD3 -EA SilentlyContinue).AllowIdleIrpInD3; " +
                "    if($v -eq 1){ $n++ } }; Write-Output ('IDLE=' + $n)" +
                "\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            var output = (p?.StandardOutput.ReadToEnd() ?? "").Trim();
            p?.WaitForExit(15000);
            var idle = 0;
            if (output.StartsWith("IDLE=", StringComparison.OrdinalIgnoreCase))
                int.TryParse(output.AsSpan(5), out idle);
            if (idle > 0)
                AddAttention(r, log, $"Root Hub/USB 设备节能 AllowIdleIrpInD3=1 共 {idle} 处（闲置唤醒失败风险；全面修复会清零）");
            else
                log("  · Root Hub AllowIdleIrpInD3: 未发现开启项");
        }
        catch (Exception ex)
        {
            log("  · Root Hub 节能查询跳过: " + ex.Message);
        }
    }

    private static void ScanPnpDevices(DiagnosisReport r, Action<string> log)
    {
        try
        {
            // 结构化输出，避免把 PHANTOM/Unknown 幽灵设备误报成严重故障
            var psi = new ProcessStartInfo("powershell",
                "-NoProfile -Command \"" +
                "$ErrorActionPreference='SilentlyContinue'; " +
                "$all=Get-PnpDevice | Where-Object { $_.Class -in @('USB','HIDClass','Keyboard','Mouse','USBDevice') }; " +
                "$hc=@($all | Where-Object { $_.FriendlyName -match 'Host Controller|xHCI|EHCI|OHCI|UHCI|可扩展主机控制器|主机控制器' }); " +
                "$hcBad=@($hc | Where-Object { [int]$_.Problem -eq 39 -or $_.Status -eq 'Error' }); " +
                "$realKm=@($all | Where-Object { $_.Class -in @('Keyboard','Mouse') -and $_.InstanceId -notmatch 'GVINPUT' -and $_.Status -eq 'OK' -and $_.Present -eq $true }); " +
                "$p39=@($all | Where-Object { [int]$_.Problem -eq 39 }); " +
                "Write-Output ('HC_TOTAL=' + $hc.Count); " +
                "Write-Output ('HC_BAD=' + $hcBad.Count); " +
                "Write-Output ('P39=' + $p39.Count); " +
                "Write-Output ('REAL_KM_OK=' + $realKm.Count); " +
                "foreach($d in $hcBad){ Write-Output ('HCERR|' + $d.Status + '|P' + $d.Problem + '|' + $d.FriendlyName) }; " +
                "foreach($d in ($p39 | Select-Object -First 12)){ Write-Output ('P39|' + $d.Class + '|' + $d.FriendlyName) }; " +
                "foreach($d in ($realKm | Select-Object -First 8)){ Write-Output ('KMOK|' + $d.Class + '|' + $d.FriendlyName) }" +
                "\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            var output = p?.StandardOutput.ReadToEnd() ?? "";
            p?.WaitForExit(25000);
            r.CheckedItems += 4;

            int hcTotal = 0, hcBad = 0, p39 = 0, realKm = 0;
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("HC_TOTAL=", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line.AsSpan(9), out var a)) hcTotal = a;
                else if (line.StartsWith("HC_BAD=", StringComparison.OrdinalIgnoreCase) &&
                         int.TryParse(line.AsSpan(7), out var b)) hcBad = b;
                else if (line.StartsWith("P39=", StringComparison.OrdinalIgnoreCase) &&
                         int.TryParse(line.AsSpan(4), out var c)) p39 = c;
                else if (line.StartsWith("REAL_KM_OK=", StringComparison.OrdinalIgnoreCase) &&
                         int.TryParse(line.AsSpan(11), out var d)) realKm = d;
                else if (line.StartsWith("HCERR|", StringComparison.OrdinalIgnoreCase))
                    AddCritical(r, log, "USB 主机控制器异常: " + line[6..].Replace('|', ' '));
                else if (line.StartsWith("P39|", StringComparison.OrdinalIgnoreCase))
                    log("  · 问题码39: " + line[4..].Replace('|', ' '));
                else if (line.StartsWith("KMOK|", StringComparison.OrdinalIgnoreCase))
                    log("  · 真实键鼠 OK: " + line[5..].Replace('|', ' '));
            }

            log($"  · USB 主机控制器: {hcTotal}，异常(Error/问题码39): {hcBad}");
            log($"  · 全机问题码39设备: {p39}（含幽灵残留时数字可能偏大）");
            log($"  · 真实键鼠(非GVINPUT)当前 OK: {realKm}");

            if (hcBad > 0)
                AddCritical(r, log, $"有 {hcBad} 个 USB 主机控制器驱动加载失败（典型问题码39）— 键鼠会全部失灵");
            else if (hcTotal == 0)
                AddAttention(r, log, "未枚举到 USB 主机控制器（权限或驱动栈异常）");
            else
                log("  · 主机控制器状态正常");

            if (realKm == 0 && hcBad > 0)
                AddCritical(r, log, "无可用真实键盘/鼠标（仅虚拟 GVINPUT 存活时也属此情况）");
            else if (realKm == 0)
                AddAttention(r, log, "当前无 Present+OK 的真实键鼠（可能未插入，或已被上层故障拖死）");
        }
        catch (Exception ex)
        {
            AddAttention(r, log, "PnP 枚举失败: " + ex.Message);
        }
    }

    private static void ScanKernelPnpLoadFailures(DiagnosisReport r, Action<string> log)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell",
                "-NoProfile -Command \"" +
                "$ErrorActionPreference='SilentlyContinue'; " +
                "$since=(Get-Date).AddDays(-7); " +
                "$ev=Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-Kernel-PnP'; Id=219; StartTime=$since} -MaxEvents 40; " +
                "if(-not $ev){ Write-Output 'COUNT=0'; return }; " +
                "$hit=@($ev | Where-Object { $_.Message -match '0xC0000033|0xC000026C|0xC0000034|USBXHCI|usbxhci|STATUS_OBJECT_NAME|无法加载|加载失败' }); " +
                "Write-Output ('COUNT=' + $hit.Count); " +
                "foreach($e in ($hit | Select-Object -First 6)){ " +
                "  $t=$e.TimeCreated.ToString('yyyy-MM-dd HH:mm'); " +
                "  $m=(($e.Message -replace '\\s+',' ').Substring(0,[Math]::Min(160,$e.Message.Length))); " +
                "  Write-Output ('EV|' + $t + '|' + $m) " +
                "}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            var output = p?.StandardOutput.ReadToEnd() ?? "";
            p?.WaitForExit(20000);
            r.CheckedItems++;

            var count = 0;
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("COUNT=", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line.AsSpan(6), out var n)) count = n;
                else if (line.StartsWith("EV|", StringComparison.OrdinalIgnoreCase))
                    log("  · " + line[3..].Replace('|', ' '));
            }

            if (count > 0)
                AddCritical(r, log, $"近7天 Kernel-PnP 事件219（驱动加载失败）相关 {count} 条 — 重点查 usbxhci ImagePath");
            else
                log("  · 近7天无 USBXHCI/0xC0000033/0xC000026C 类加载失败记录");
        }
        catch (Exception ex)
        {
            log("  · 事件日志读取跳过: " + ex.Message);
        }
    }

    private static void ScanStartupRemoteExes(DiagnosisReport r, Action<string> log)
    {
        var dirs = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)),
            Environment.GetFolderPath(Environment.SpecialFolder.Startup)
        };
        var found = false;
        foreach (var dir in dirs.Where(Directory.Exists))
        {
            foreach (var pat in new[] { "ToDesk*.exe", "*Sunlogin*.exe", "Oray*.exe" })
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, pat))
                    {
                        found = true;
                        r.CheckedItems++;
                        AddAttention(r, log, $"启动文件夹存在远程 exe（易弹安全警告）: {f}");
                    }
                }
                catch { }
            }
        }
        if (!found)
        {
            r.CheckedItems++;
            log("  · Startup 中无 ToDesk/向日葵 exe");
        }
    }

    private static void AddCritical(DiagnosisReport r, Action<string> log, string msg)
    {
        r.Critical.Add(msg);
        log("⚠ " + msg);
    }

    private static void AddAttention(DiagnosisReport r, Action<string> log, string msg)
    {
        r.Attention.Add(msg);
        log("◇ " + msg);
    }

    private static bool ContainsBadFilter(string text)
    {
        var t = text.ToLowerInvariant();
        return t.Contains("usbdk") || t.Contains("hrdevmon") || t.Contains("usbdkmon");
    }

    private static bool RegKeyExists(string path)
    {
        var psi = new ProcessStartInfo("reg", $"query \"{path}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        p?.WaitForExit();
        return p?.ExitCode == 0;
    }

    private static bool RegValueExists(string path, string valueName)
    {
        var psi = new ProcessStartInfo("reg", $"query \"{path}\" /v \"{valueName}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        p?.WaitForExit();
        return p?.ExitCode == 0;
    }

    private static int? QueryDword(string path, string valueName)
    {
        var psi = new ProcessStartInfo("reg", $"query \"{path}\" /v {valueName}")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        var output = p?.StandardOutput.ReadToEnd() ?? "";
        p?.WaitForExit();
        var idx = output.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var hex = output[idx..].Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
        return Convert.ToInt32(hex, 16);
    }

    private static string QueryMultiSz(string path, string valueName) => QueryRegValue(path, valueName);

    private static string QueryRegValue(string path, string valueName)
    {
        var psi = new ProcessStartInfo("reg", $"query \"{path}\" /v \"{valueName}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        var output = p?.StandardOutput.ReadToEnd() ?? "";
        p?.WaitForExit();
        if (p?.ExitCode != 0) return "";
        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains(valueName, StringComparison.OrdinalIgnoreCase)) continue;
            // REG_SZ / REG_EXPAND_SZ / REG_MULTI_SZ: NAME  TYPE  DATA...
            var idx = line.IndexOf("REG_", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var afterType = line[(idx + 4)..];
            var sp = afterType.IndexOfAny(new[] { ' ', '\t' });
            if (sp < 0) continue;
            // skip type token (SZ / EXPAND_SZ / MULTI_SZ / DWORD)
            var rest = afterType[sp..].TrimStart();
            var sp2 = rest.IndexOfAny(new[] { ' ', '\t' });
            if (sp2 < 0) return rest.Trim();
            return rest[sp2..].Trim();
        }
        return "";
    }
}
