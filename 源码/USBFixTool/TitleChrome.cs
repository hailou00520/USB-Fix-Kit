using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace USBFixTool;

/// <summary>
/// PE 原生窗用的轻量标题条：与背景同色、无重复品牌字，只留拖动区 + 窗口按钮。
/// WebView 主界面不走这里，按钮做在 React 顶栏里。
/// </summary>
public sealed class TitleChrome : Panel
{
    private readonly Form _form;
    private Point _dragStart;
    private bool _dragging;
    private readonly Button _btnMin;
    private readonly Button _btnMax;
    private readonly Button _btnClose;

    private static readonly Color Bar = Color.FromArgb(241, 244, 243);
    private static readonly Color Fg = Color.FromArgb(39, 39, 42);
    private static readonly Color Muted = Color.FromArgb(113, 113, 122);
    private static readonly Color Hover = Color.FromArgb(220, 226, 232);
    private static readonly Color CloseHover = Color.FromArgb(220, 38, 38);

    public const int BarHeight = 32;

    public TitleChrome(Form form)
    {
        _form = form;
        Dock = DockStyle.Top;
        Height = BarHeight;
        BackColor = Bar;
        DoubleBuffered = true;

        _btnMin = MakeWinBtn("\uE921", (_, _) => _form.WindowState = FormWindowState.Minimized);
        _btnMax = MakeWinBtn("\uE922", (_, _) => ToggleMax());
        _btnClose = MakeWinBtn("\uE8BB", (_, _) => _form.Close(), true);

        Controls.Add(_btnMin);
        Controls.Add(_btnMax);
        Controls.Add(_btnClose);

        WireDrag(this);
        DoubleClick += (_, _) => ToggleMax();
        Resize += (_, _) => LayoutButtons();
        LayoutButtons();
    }

    private void WireDrag(Control c)
    {
        c.MouseDown += TitleMouseDown;
        c.MouseMove += TitleMouseMove;
        c.MouseUp += (_, _) => _dragging = false;
        c.DoubleClick += (_, _) => ToggleMax();
    }

    private Button MakeWinBtn(string glyph, EventHandler click, bool isClose = false)
    {
        var b = new Button
        {
            Text = glyph,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(46, BarHeight),
            ForeColor = Muted,
            BackColor = Bar,
            Font = new Font("Segoe Fluent Icons", 9F),
            Cursor = Cursors.Hand,
            TabStop = false
        };
        // Win10 可能没有 Fluent Icons，回退 MDL2 / 字符
        try { _ = b.Font.Name; }
        catch { }
        if (b.Font.Name is not ("Segoe Fluent Icons" or "Segoe MDL2 Assets"))
        {
            try { b.Font = new Font("Segoe MDL2 Assets", 9F); }
            catch
            {
                b.Font = new Font("Segoe UI", 10F);
                b.Text = isClose ? "✕" : glyph switch
                {
                    "\uE921" => "─",
                    "\uE922" => "□",
                    _ => glyph
                };
            }
        }

        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = isClose ? CloseHover : Hover;
        b.FlatAppearance.MouseDownBackColor = isClose
            ? Color.FromArgb(185, 28, 28)
            : Color.FromArgb(200, 208, 216);
        b.MouseEnter += (_, _) => b.ForeColor = isClose ? Color.White : Fg;
        b.MouseLeave += (_, _) => b.ForeColor = Muted;
        b.Click += click;
        return b;
    }

    private void LayoutButtons()
    {
        _btnClose.Location = new Point(Math.Max(0, Width - 46), 0);
        _btnMax.Location = new Point(Math.Max(0, Width - 92), 0);
        _btnMin.Location = new Point(Math.Max(0, Width - 138), 0);
        var maxGlyph = _form.WindowState == FormWindowState.Maximized ? "\uE923" : "\uE922";
        if (_btnMax.Font.Name is "Segoe Fluent Icons" or "Segoe MDL2 Assets")
            _btnMax.Text = maxGlyph;
        else
            _btnMax.Text = _form.WindowState == FormWindowState.Maximized ? "❐" : "□";
    }

    private void ToggleMax()
    {
        _form.WindowState = _form.WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal
            : FormWindowState.Maximized;
        LayoutButtons();
    }

    private void TitleMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_form.WindowState == FormWindowState.Maximized) return;
        _dragging = true;
        _dragStart = e.Location;
    }

    private void TitleMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging || e.Button != MouseButtons.Left) return;
        var screen = ((Control)sender!).PointToScreen(e.Location);
        _form.Location = new Point(screen.X - _dragStart.X, screen.Y - _dragStart.Y);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // 极淡底线，几乎看不见，避免和内容「割裂」
        using var pen = new Pen(Color.FromArgb(18, 24, 24, 27));
        e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }
}

internal static class BorderlessHit
{
    private const int Grip = 6;
    private const int WM_NCHITTEST = 0x84;

    public static bool Handle(Form form, ref Message m)
    {
        if (m.Msg != WM_NCHITTEST || form.WindowState != FormWindowState.Normal)
            return false;

        var pt = form.PointToClient(Cursor.Position);
        var w = form.ClientSize.Width;
        var h = form.ClientSize.Height;
        bool left = pt.X <= Grip, right = pt.X >= w - Grip;
        bool top = pt.Y <= Grip, bottom = pt.Y >= h - Grip;

        m.Result = (IntPtr)((top, left, right, bottom) switch
        {
            (true, true, _, _) => 13,
            (true, _, true, _) => 14,
            (_, true, _, true) => 16,
            (_, _, true, true) => 17,
            (true, _, _, _) => 12,
            (_, _, _, true) => 15,
            (_, true, _, _) => 10,
            (_, _, true, _) => 11,
            _ => 1
        });
        return true;
    }
}

/// <summary>
/// 圆角：Win11 DWM「标准圆角」(≈12px)，与界面主面 10px / 内嵌 8px 成外大内小。
/// </summary>
internal static class FormRoundCorners
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2; // 标准圆角；3=小圆角

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Attach(Form form, int _ignored = 0)
    {
        void Apply()
        {
            if (form.IsDisposed || !form.IsHandleCreated) return;

            var old = form.Region;
            form.Region = null;
            old?.Dispose();

            try
            {
                var pref = DwmwcpRound;
                _ = DwmSetWindowAttribute(form.Handle, DwmwaWindowCornerPreference, ref pref, sizeof(int));
            }
            catch { }
        }

        form.HandleCreated += (_, _) => Apply();
        form.Shown += (_, _) => Apply();
        if (form.IsHandleCreated) Apply();
    }
}
