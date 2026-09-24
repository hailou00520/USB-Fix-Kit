using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace USBFixTool;

/// <summary>WebView ↔ 无边框窗口：拖动 / 最小化 / 最大化 / 关闭 / 文件拖放路径</summary>
internal static class WindowChromeBridge
{
    private const int WmNcLButtonDown = 0xA1;
    private const int HtCaption = 2;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public static void Attach(Form form, WebView2 webView)
    {
        // 窗体边缘拖放兜底（标题栏/空白处）
        form.AllowDrop = true;
        form.DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
        };
        form.DragDrop += (_, e) =>
        {
            try
            {
                if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                    HostUi.NotifyDroppedPath(webView.CoreWebView2, files[0]);
            }
            catch { /* ignore */ }
        };

        void Hook(CoreWebView2 core)
        {
            try { webView.AllowExternalDrop = true; }
            catch { /* older runtime */ }

            core.WebMessageReceived += (_, e) =>
            {
                string? msg = null;
                try { msg = e.TryGetWebMessageAsString(); }
                catch { }

                // WebView2：前端 postMessageWithAdditionalObjects('FilesDropped', files)
                if (string.Equals(msg, "FilesDropped", StringComparison.Ordinal))
                {
                    try
                    {
                        foreach (var obj in e.AdditionalObjects)
                        {
                            // CoreWebView2File.Path（运行时类型名因版本而异，用反射更稳）
                            var pathProp = obj?.GetType().GetProperty("Path");
                            var path = pathProp?.GetValue(obj) as string;
                            if (!string.IsNullOrWhiteSpace(path))
                            {
                                form.BeginInvoke(() => HostUi.NotifyDroppedPath(core, path!));
                                break;
                            }
                        }
                    }
                    catch { /* ignore */ }
                    return;
                }

                if (string.IsNullOrEmpty(msg)) return;

                form.BeginInvoke(() =>
                {
                    switch (msg)
                    {
                        case "drag":
                            if (form.WindowState == FormWindowState.Maximized) return;
                            ReleaseCapture();
                            _ = SendMessage(form.Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
                            break;
                        case "minimize":
                            form.WindowState = FormWindowState.Minimized;
                            break;
                        case "maximize":
                            form.WindowState = form.WindowState == FormWindowState.Maximized
                                ? FormWindowState.Normal
                                : FormWindowState.Maximized;
                            PushState(core, form);
                            break;
                        case "close":
                            form.Close();
                            break;
                        case "query-state":
                            PushState(core, form);
                            break;
                    }
                });
            };

            form.Resize += (_, _) =>
            {
                try { PushState(core, form); }
                catch { }
            };

            PushState(core, form);
        }

        if (webView.CoreWebView2 != null)
            Hook(webView.CoreWebView2);
        else
            webView.CoreWebView2InitializationCompleted += (_, e) =>
            {
                if (e.IsSuccess && webView.CoreWebView2 != null)
                    Hook(webView.CoreWebView2);
            };
    }

    private static void PushState(CoreWebView2 core, Form form)
    {
        var max = form.WindowState == FormWindowState.Maximized ? "1" : "0";
        try
        {
            core.PostWebMessageAsString("winstate:" + max);
        }
        catch { }
    }
}
