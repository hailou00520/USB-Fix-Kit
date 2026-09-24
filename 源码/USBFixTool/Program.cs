using System.Diagnostics;
using System.Runtime.InteropServices;

namespace USBFixTool;

static class Program
{
    private const string MutexName = @"Local\USBFixTool_SingleInstance_v1";

    /// <summary>命令行 --autofix：键鼠失灵时自动全面修复。</summary>
    public static bool AutoFix { get; private set; }

    /// <summary>命令行 --pe / --preview-pe：强制按 PE 模式显示网页界面（预览/真 PE 同款）。</summary>
    public static bool ForcePeUi { get; private set; }

    /// <summary>当前应按 PE 功能集运行（真 PE 或 --pe 预览）。</summary>
    public static bool IsPeMode => ForcePeUi || RepairEngine.IsPeEnvironment();

    [STAThread]
    static void Main(string[] args)
    {
        AutoFix = args.Any(a =>
            a.Equals("--autofix", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-autofix", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/autofix", StringComparison.OrdinalIgnoreCase));

        ForcePeUi = args.Any(a =>
            a.Equals("--pe", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-pe", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/pe", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--preview-pe", StringComparison.OrdinalIgnoreCase));

        var mutexName = ForcePeUi ? MutexName + "_PePreview" : MutexName;
        using var mutex = new Mutex(true, mutexName, out var createdNew);
        if (!createdNew)
        {
            ActivateExistingWindow();
            return;
        }

        ApplicationConfiguration.Initialize();

        // 真 PE / 预览 / 正常 Windows：一律优先网页界面（你记得的那套）；无 WebView2 才退回原生窗
        LocalApiServer? api = null;
        try
        {
            api = new LocalApiServer();
            api.Start();
            Application.Run(new MainForm(api, fallbackToNative: true));
        }
        catch
        {
            api?.Dispose();
            Application.Run(new NativePeForm());
        }
    }

    private static void ActivateExistingWindow()
    {
        try
        {
            var current = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(current.ProcessName))
            {
                if (p.Id == current.Id) continue;
                var hWnd = p.MainWindowHandle;
                if (hWnd == IntPtr.Zero) continue;

                if (IsIconic(hWnd))
                    ShowWindow(hWnd, SwRestore);
                SetForegroundWindow(hWnd);
                return;
            }
        }
        catch
        {
            // ignore — second instance just exits
        }
    }

    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
}
