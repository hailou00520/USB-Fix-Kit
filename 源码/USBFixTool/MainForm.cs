using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace USBFixTool;

public sealed class MainForm : Form
{
    private readonly LocalApiServer _api;
    private readonly WebView2 _webView;
    private readonly Label _fallback;
    private readonly bool _fallbackToNative;
    private bool _switched;

    public MainForm(LocalApiServer api, bool fallbackToNative = true)
    {
        _api = api;
        _fallbackToNative = fallbackToNative;
        Text = Program.IsPeMode ? "一体化急救工具 · PE" : "急救工具";
        Size = new Size(780, 720);
        MinimumSize = new Size(640, 560);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        // 与 Web 背景同色，去掉那条「假标题栏」后边缘不露白
        BackColor = Color.FromArgb(221, 232, 228);
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (!File.Exists(ico)) ico = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(ico)) Icon = new Icon(ico);
            else Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? Icon;
        }
        catch { }

        _fallback = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(44, 51, 60),
            Font = new Font("Microsoft YaHei UI", 11F),
            Text = "正在加载界面…",
            Visible = true,
            BackColor = Color.FromArgb(221, 232, 228)
        };

        _webView = new WebView2 { Dock = DockStyle.Fill, Visible = false };
        Controls.Add(_webView);
        Controls.Add(_fallback);

        FormRoundCorners.Attach(this);
        HostUi.Bind(this);
        WindowChromeBridge.Attach(this, _webView);

        Shown += async (_, _) => await InitWebView();
        FormClosed += (_, _) => _api.Dispose();
    }

    protected override void WndProc(ref Message m)
    {
        if (BorderlessHit.Handle(this, ref m)) return;
        base.WndProc(ref m);
    }

    private async Task InitWebView()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(
                null, Path.Combine(Path.GetTempPath(), "USBFixToolWV2"));
            await _webView.EnsureCoreWebView2Async(env);
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess)
                {
                    _fallback.Visible = false;
                    _webView.Visible = true;
                }
            };
            var url = _api.BaseUrl;
            if (Program.AutoFix)
                url = url.TrimEnd('/') + "/?autofix=1";
            _webView.Source = new Uri(url);

            _ = Task.Run(async () =>
            {
                await Task.Delay(8000);
                BeginInvoke(() =>
                {
                    if (!_webView.Visible && !_switched && _fallbackToNative)
                        SwitchToNative("WebView 加载超时，已切换 PE 原生界面");
                });
            });
        }
        catch (Exception ex)
        {
            if (_fallbackToNative)
                SwitchToNative("当前环境无 WebView2，已切换 PE 原生界面\n" + ex.Message);
            else
            {
                _fallback.Text = "无法加载 WebView2:\n" + ex.Message + "\n\n也可打开: " + _api.BaseUrl;
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_api.BaseUrl)
                    { UseShellExecute = true });
                }
                catch { }
            }
        }
    }

    private void SwitchToNative(string reason)
    {
        if (_switched) return;
        _switched = true;
        Hide();
        _api.Dispose();
        var native = new NativePeForm();
        native.FormClosed += (_, _) => Close();
        native.Show();
        _ = reason;
    }
}
