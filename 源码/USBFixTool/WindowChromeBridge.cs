using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace USBFixTool;

/// <summary>WebView ↔ 无边框窗口：拖动 / 最小化 / 最大化 / 关闭</summary>
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
        void Hook(CoreWebView2 core)
        {
            core.WebMessageReceived += (_, e) =>
            {
                string? msg = null;
                try { msg = e.TryGetWebMessageAsString(); }
                catch { }

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
