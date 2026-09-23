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
        log("分级: ⚠严重 = 会导致键鼠失效的项；◇提示 = 与修复建议值不同或需关注，键鼠正常可忽略");
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
        log("分级: ⚠严重 = 会导致键鼠失效的项；◇提示 = 与修复建议值不同或需关注，键鼠正常可忽略");
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

        log("── 电源 / 快速启动 ──");
        r.CheckedItems++;
        using (var pwr = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power"))
        {
            var hiber = pwr?.GetValue("HiberbootEnabled") as int?;
            if (hiber == 1)
                AddAttention(r, log, "快速启动已开启（偶发导致 USB 设备异常，可在修复时关闭）");
            else
                log($"  · 快速启动 HiberbootEnabled = {hiber ?? 0}");
        }

        r.CheckedItems++;
        using (var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\USB"))
        {
            // selective suspend often under usbhub Device Parameters — just note
            log("  · USB 选择性暂停: 见各集线器设备属性（本项仅登记）");
        }

        log("── PnP 设备（USB / HID，完整状态）──");
        ScanPnpDevices(r, log);

        log("── 启动文件夹远程 exe（安全警告相关）──");
        ScanStartupRemoteExes(r, log);

        return r;
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
            var msg = $"{label} Start={start}({StartLabel(start)})，与修复建议 {suggest}({StartLabel(suggest)}) 不同（键鼠正常可忽略）";
            r.Attention.Add(msg);
            log($"  ◇ {label} Start={start} ({StartLabel(start)}) — 可用；建议值={suggest}({StartLabel(suggest)})");
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

    private static void ScanPnpDevices(DiagnosisReport r, Action<string> log)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell",
                "-NoProfile -Command \"" +
                "$d=Get-PnpDevice -PresentOnly -EA SilentlyContinue | Where-Object { $_.InstanceId -match 'USB|HID|KEYBOARD|MOUSE' }; " +
                "$bad=@($d | Where-Object { $_.Status -ne 'OK' }); " +
                "Write-Output ('TOTAL=' + @($d).Count); Write-Output ('BAD=' + $bad.Count); " +
                "$d | Select-Object Status,Class,FriendlyName | Format-Table -AutoSize | Out-String -Width 220\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            var output = p?.StandardOutput.ReadToEnd() ?? "";
            p?.WaitForExit(20000);
            r.CheckedItems++;

            int total = 0, bad = 0;
            foreach (var line in output.Split('\n'))
            {
                if (line.StartsWith("TOTAL=", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line.Trim().AsSpan(6), out var t)) total = t;
                if (line.StartsWith("BAD=", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line.Trim().AsSpan(4), out var b)) bad = b;
            }

            log($"  · 在场 USB/HID 类设备: {total}，其中非 OK: {bad}");
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith("TOTAL=") || line.StartsWith("BAD=") || line.Contains("---") ||
                    line.StartsWith("Status", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.Length < 4) continue;
                log("    " + line);
                if (line.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Unknown", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Degraded", StringComparison.OrdinalIgnoreCase))
                    AddCritical(r, log, "设备异常: " + line);
            }

            if (total == 0)
                AddAttention(r, log, "未能枚举到 USB/HID 设备（可能权限不足或驱动栈异常）");
            else if (bad == 0)
                log("  · 全部枚举设备状态为 OK");
        }
        catch (Exception ex)
        {
            AddAttention(r, log, "PnP 枚举失败: " + ex.Message);
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

    private static string QueryMultiSz(string path, string valueName)
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
        if (p?.ExitCode != 0) return "";
        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains(valueName, StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
                return string.Join(" ", parts.Skip(2)).Trim();
        }
        return "";
    }
}
