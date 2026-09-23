using System.Diagnostics;
using System.Runtime.InteropServices;

namespace USBFixTool;

static class Program
{
    private const string MutexName = @"Local\USBFixTool_SingleInstance_v1";

    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            ActivateExistingWindow();
            return;
        }

        ApplicationConfiguration.Initialize();

        // PE 环境：直接用原生界面（不依赖 WebView2，保证能用）
        if (RepairEngine.IsPeEnvironment())
        {
            Application.Run(new NativePeForm());
            return;
        }

        // 正常 Windows：优先 shadcn WebView；失败则回退原生界面
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
