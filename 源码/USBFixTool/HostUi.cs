using Microsoft.Web.WebView2.Core;

namespace USBFixTool;

/// <summary>在 UI 线程弹出系统对话框（占用页：一次可选文件或文件夹）。</summary>
internal static class HostUi
{
    private static Form? _main;
    private const string FolderMarker = "选择此文件夹";

    public static void Bind(Form form) => _main = form;

    /// <summary>
    /// 合并选文件 / 选文件夹。
    /// · 点某个文件 → 返回文件路径
    /// · 进入目标文件夹后直接点「打开」（文件名保持「选择此文件夹」）→ 返回该文件夹
    /// </summary>
    public static string? BrowsePath()
    {
        var form = _main;
        if (form == null || form.IsDisposed) return null;

        string? path = null;
        void Show()
        {
            using var dlg = new OpenFileDialog
            {
                Title = "选择文件或文件夹",
                Filter = "所有文件 (*.*)|*.*",
                CheckFileExists = false,
                CheckPathExists = true,
                ValidateNames = false,
                Multiselect = false,
                FileName = FolderMarker,
            };
            if (dlg.ShowDialog(form) != DialogResult.OK || string.IsNullOrWhiteSpace(dlg.FileName))
                return;

            path = NormalizePickedPath(dlg.FileName);
        }

        if (form.InvokeRequired) form.Invoke(Show);
        else Show();
        return path;
    }

    private static string? NormalizePickedPath(string raw)
    {
        var p = raw.Trim().Trim('"');
        if (File.Exists(p)) return p;
        if (Directory.Exists(p)) return p;

        var name = Path.GetFileName(p);
        var dir = Path.GetDirectoryName(p);
        if (string.IsNullOrEmpty(dir)) return null;

        // 用户进入文件夹后点「打开」，系统会带上占位文件名
        if (name.Equals(FolderMarker, StringComparison.OrdinalIgnoreCase)
            || name.Equals("Folder Selection.", StringComparison.OrdinalIgnoreCase)
            || name == ".")
            return dir;

        var asDir = Path.Combine(dir, name);
        if (Directory.Exists(asDir)) return asDir;

        return Directory.Exists(dir) ? dir : null;
    }

    public static void NotifyDroppedPath(CoreWebView2? core, string path)
    {
        if (core == null || string.IsNullOrWhiteSpace(path)) return;
        try
        {
            core.PostWebMessageAsString("dropped-path:" + path);
        }
        catch { /* ignore */ }
    }
}
