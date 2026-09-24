using System.Diagnostics;
using System.Runtime.InteropServices;

namespace USBFixTool;

/// <summary>
/// 快速判断「键鼠是否已经废了」——废了就自动修，正常打开不打扰。
/// </summary>
public static class InputHealth
{
    private const int SmMousePresent = 19;

    public static bool LooksBroken(out string reason)
    {
        reason = "";
        try
        {
            if (GetSystemMetrics(SmMousePresent) == 0)
            {
                reason = "系统报告无鼠标设备";
                return true;
            }
        }
        catch { /* ignore */ }

        try
        {
            var psi = new ProcessStartInfo(
                "powershell",
                "-NoProfile -Command \"" +
                "$ErrorActionPreference='SilentlyContinue'; " +
                "$all=@(Get-PnpDevice | Where-Object { $_.Class -in @('USB','HIDClass','Keyboard','Mouse','USBDevice') -and $_.Present -eq $true }); " +
                "$hc=@($all | Where-Object { $_.FriendlyName -match 'Host Controller|xHCI|EHCI|OHCI|UHCI|主机控制器' }); " +
                "$hcBad=@($hc | Where-Object { [int]$_.Problem -eq 39 -or $_.Status -eq 'Error' }); " +
                "$km=@($all | Where-Object { $_.Class -in @('Keyboard','Mouse') -and $_.InstanceId -notmatch 'GVINPUT' -and $_.Status -eq 'OK' }); " +
                "Write-Output ('HC_BAD=' + $hcBad.Count); " +
                "Write-Output ('KM_OK=' + $km.Count)" +
                "\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            var output = p?.StandardOutput.ReadToEnd() ?? "";
            p?.WaitForExit(12000);

            int hcBad = 0, kmOk = 0;
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("HC_BAD=", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line.AsSpan(7), out var a)) hcBad = a;
                else if (line.StartsWith("KM_OK=", StringComparison.OrdinalIgnoreCase) &&
                         int.TryParse(line.AsSpan(6), out var b)) kmOk = b;
            }

            if (hcBad > 0)
            {
                reason = $"USB 主机控制器异常 ×{hcBad}（典型问题码39，键鼠会全废）";
                return true;
            }

            if (kmOk == 0)
            {
                reason = "未检测到可用的真实键盘/鼠标";
                return true;
            }
        }
        catch (Exception ex)
        {
            reason = "键鼠探测失败: " + ex.Message;
            // 探测失败不强制当失灵，避免误伤平常打开
            return false;
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
