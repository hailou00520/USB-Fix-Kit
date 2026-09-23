namespace USBFixTool;

static class Program
{
    [STAThread]
    static void Main()
    {
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
}
