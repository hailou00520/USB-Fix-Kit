using Microsoft.Win32;

namespace USBFixTool;

/// <summary>
/// 修复精简/优化工具写坏的驱动 ImagePath（典型：\SystemRoot\\SystemRoot\... → 问题码 39 / 0xC0000033）。
/// 依据：2026-09-24 键鼠失灵实机案例（AMD xHCI / usbxhci）。
/// </summary>
internal static class UsbDriverImagePath
{
    /// <summary>仅处理微软收件箱 USB/HID 相关服务，跳过 \??\ 第三方路径。</summary>
    public static readonly (string Service, string SysFile)[] Targets =
    {
        ("usbxhci", "usbxhci.sys"),
        ("USBXHCI", "usbxhci.sys"),
        ("usbehci", "usbehci.sys"),
        ("usbohci", "usbohci.sys"),
        ("usbuhci", "usbuhci.sys"),
        ("usbhub3", "usbhub3.sys"),
        ("usbhub", "usbhub.sys"),
        ("usbccgp", "usbccgp.sys"),
        ("usbd", "usbd.sys"),
        ("usbport", "usbport.sys"),
        ("HidUsb", "hidusb.sys"),
        ("mouhid", "mouhid.sys"),
        ("kbdhid", "kbdhid.sys"),
        ("mouclass", "mouclass.sys"),
        ("kbdclass", "kbdclass.sys"),
        ("Wdf01000", "Wdf01000.sys"),
    };

    public static bool IsCorrupt(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return false;
        if (IsThirdPartyPath(imagePath)) return false;

        var n = imagePath.Replace("\\\\", "\\");
        var count = 0;
        for (var i = 0; i < n.Length;)
        {
            var idx = n.IndexOf("SystemRoot", i, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;
            count++;
            i = idx + 10;
        }
        return count >= 2;
    }

    /// <summary>是否需要修复：双 SystemRoot，或 \SystemRoot\… 指向的驱动文件不存在。</summary>
    public static bool NeedsRepair(string? imagePath, string sysFile, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(imagePath)) return false;
        if (IsThirdPartyPath(imagePath)) return false;

        if (IsCorrupt(imagePath))
        {
            reason = "双重 SystemRoot（0xC0000033 对象名无效）";
            return true;
        }

        var n = imagePath.Replace("\\\\", "\\").Trim();
        if (n.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase) ||
            n.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase) ||
            n.StartsWith(@"\System32\", StringComparison.OrdinalIgnoreCase))
        {
            var drivers = Path.Combine(Environment.SystemDirectory, "drivers", sysFile);
            if (!File.Exists(drivers))
            {
                reason = $"驱动文件缺失 ({sysFile})";
                return true;
            }
        }

        return false;
    }

    public static string Expected(string sysFile) =>
        $@"\SystemRoot\System32\drivers\{sysFile}";

    /// <summary>在线：仅当 ImagePath 损坏/文件缺失时写回 REG_EXPAND_SZ。返回修复条数。</summary>
    public static int FixLive(IEnumerable<string> controlSets, Action<string> log)
    {
        var fixedCount = 0;
        foreach (var cs in controlSets)
        {
            foreach (var (svc, sys) in Targets)
            {
                var path = $@"SYSTEM\{cs}\Services\{svc}";
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(path, writable: true);
                    if (key == null) continue;
                    var cur = key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    if (!NeedsRepair(cur, sys, out var why)) continue;

                    var expect = Expected(sys);
                    key.SetValue("ImagePath", expect, RegistryValueKind.ExpandString);
                    fixedCount++;
                    log($"  ✓ [{cs}] {svc} ImagePath 已修复（{why}）");
                    log($"      坏: {cur}");
                    log($"      好: {expect}");
                }
                catch (Exception ex)
                {
                    log($"  ✗ [{cs}] {svc} ImagePath: {ex.Message}");
                }
            }
        }
        return fixedCount;
    }

    private static bool IsThirdPartyPath(string imagePath) =>
        imagePath.StartsWith(@"\??\", StringComparison.Ordinal) ||
        imagePath.StartsWith(@"\\?\", StringComparison.Ordinal);
}
