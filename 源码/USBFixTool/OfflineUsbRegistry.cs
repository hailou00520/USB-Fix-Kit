using System.Diagnostics;
using System.Text;

namespace USBFixTool;

/// <summary>
/// PE 离线：同时修补 ControlSet001 / 002（避免只改了备份集）
/// </summary>
public static class OfflineUsbRegistry
{
    private static readonly string[] BadServices =
    {
        "UsbDk", "usbdk", "USBDk", "usbdkmon", "UsbDkHelper", "hrdevmon", "HRDevMon", "UsbDkRuntime"
    };

    private static readonly Dictionary<string, int> ServiceStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["usbxhci"] = 0, ["USBXHCI"] = 0,
        ["usbhub"] = 1, ["USBHUB"] = 1,
        ["usbhub3"] = 1, ["USBHUB3"] = 1,
        ["usbccgp"] = 1,
        ["USBSTOR"] = 3,
        ["PlugPlay"] = 2,
        ["HidUsb"] = 3, ["mouhid"] = 3, ["kbdhid"] = 3,
        ["kbdclass"] = 3, ["mouclass"] = 3, ["HidClass"] = 3,
        ["Wdf01000"] = 0, ["WUDFRd"] = 3
    };

    private static readonly string[] ClassGuids =
    {
        "{36fc9e60-c465-11cf-8056-444553540000}",
        "{745a17a0-74d3-11d0-b6fe-00a0c90f57da}",
        "{4d36e96f-e325-11ce-bfc1-444553540000}",
        "{4d36e96b-e325-11ce-bfc1-444553540000}",
        "{88BAE032-5A81-49f0-BC3D-A4FF138216D6}"
    };

    public static IEnumerable<string> EnumerateControlSets(string offlineSysRoot)
    {
        // offlineSysRoot like HKLM\PEOFFLINE_SYS
        var found = new List<string>();
        foreach (var cs in new[] { "ControlSet001", "ControlSet002", "ControlSet003" })
        {
            var psi = new ProcessStartInfo("reg", $"query \"{offlineSysRoot}\\{cs}\\Services\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit();
            if (p?.ExitCode == 0) found.Add(cs);
        }
        if (found.Count == 0) found.Add("ControlSet001");
        return found;
    }

    public static void ApplyUsbDkFix(string offlineSysRoot, Action<string> log, Action<string> runReg)
    {
        var sets = EnumerateControlSets(offlineSysRoot).ToList();
        log($"将修补 ControlSet: {string.Join(", ", sets)}");

        foreach (var cs in sets)
        {
            log($"── {cs}: 删 UsbDk/hrdevmon ──");
            foreach (var svc in BadServices)
                runReg($@"delete ""{offlineSysRoot}\{cs}\Services\{svc}"" /f");

            log($"── {cs}: 补 USB 服务 Start ──");
            foreach (var kv in ServiceStarts)
                runReg($@"add ""{offlineSysRoot}\{cs}\Services\{kv.Key}"" /v Start /t REG_DWORD /d {kv.Value} /f");

            log($"── {cs}: 修复损坏 ImagePath（双 SystemRoot）──");
            foreach (var (svc, sys) in UsbDriverImagePath.Targets)
            {
                if (!ServiceStarts.ContainsKey(svc)) continue;
                var svcPath = $@"{offlineSysRoot}\{cs}\Services\{svc}";
                runReg($@"add ""{svcPath}"" /v ImagePath /t REG_EXPAND_SZ /d ""{UsbDriverImagePath.Expected(sys)}"" /f");
            }

            log($"── {cs}: 清过滤驱动 ──");
            foreach (var g in ClassGuids)
            {
                runReg($@"delete ""{offlineSysRoot}\{cs}\Control\Class\{g}"" /v UpperFilters /f");
                runReg($@"delete ""{offlineSysRoot}\{cs}\Control\Class\{g}"" /v LowerFilters /f");
            }
            runReg($@"add ""{offlineSysRoot}\{cs}\Control\Class\{{4d36e96b-e325-11ce-bfc1-444553540000}}"" /v UpperFilters /t REG_MULTI_SZ /d kbdclass /f");
            runReg($@"add ""{offlineSysRoot}\{cs}\Control\Class\{{4d36e96f-e325-11ce-bfc1-444553540000}}"" /v UpperFilters /t REG_MULTI_SZ /d mouclass /f");
            runReg($@"delete ""{offlineSysRoot}\{cs}\Control\Keyboard Layout"" /v ""Scancode Map"" /f");
        }
    }

    public static void ClearInstallRestrictions(string offlineSoftRoot, Action<string> runReg)
    {
        runReg($@"delete ""{offlineSoftRoot}\Policies\Microsoft\Windows\DeviceInstall\Restrictions"" /f");
        runReg($@"delete ""{offlineSoftRoot}\Policies\Microsoft\Windows\RemovableStorageDevices"" /f");
    }
}
