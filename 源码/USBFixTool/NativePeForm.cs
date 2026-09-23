using System.Drawing.Drawing2D;

namespace USBFixTool;

/// <summary>PE 原生美化界面（无 WebView2）</summary>
public sealed class NativePeForm : Form
{
    private readonly RepairEngine _engine;
    private readonly RichTextBox _log;
    private readonly Label _status;
    private readonly Label _driveHint;
    private readonly Panel _actionsHost;
    private bool _busy;

    private static readonly Color Bg = Color.FromArgb(238, 241, 244);
    private static readonly Color Card = Color.FromArgb(255, 255, 255);
    private static readonly Color Fg = Color.FromArgb(44, 51, 60);
    private static readonly Color Muted = Color.FromArgb(106, 115, 128);
    private static readonly Color Emerald = Color.FromArgb(61, 143, 120);
    private static readonly Color EmeraldDim = Color.FromArgb(229, 242, 237);

    public NativePeForm()
    {
        _engine = new RepairEngine(AppendLog);
        Text = "USB 急救工具 · PE";
        Size = new Size(780, 700);
        MinimumSize = new Size(640, 560);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.FromArgb(232, 236, 241);
        ForeColor = Fg;
        Font = new Font("Microsoft YaHei UI", 9.5F);
        DoubleBuffered = true;
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (!File.Exists(ico)) ico = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(ico)) Icon = new Icon(ico);
            else Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? Icon;
        }
        catch { }

        // Header band
        var header = new DoubleBufPanel { Dock = DockStyle.Top, Height = 108, BackColor = Card };
        header.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(40, 255, 255, 255));
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
            using var glow = new SolidBrush(Color.FromArgb(28, 52, 211, 153));
            e.Graphics.FillEllipse(glow, header.Width - 160, -40, 200, 120);
        };

        var iconBox = new Panel
        {
            Location = new Point(24, 28),
            Size = new Size(44, 44),
            BackColor = EmeraldDim
        };
        iconBox.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundRect(new Rectangle(0, 0, 43, 43), 10);
            using var br = new SolidBrush(EmeraldDim);
            using var pen = new Pen(Color.FromArgb(80, Emerald));
            e.Graphics.FillPath(br, path);
            e.Graphics.DrawPath(pen, path);
            TextRenderer.DrawText(e.Graphics, "USB", new Font("Segoe UI Semibold", 9F),
                new Rectangle(0, 0, 44, 44), Emerald,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };

        var brand = new Label
        {
            Text = "USB FIX KIT",
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            ForeColor = Muted,
            Location = new Point(82, 22),
            AutoSize = true
        };
        var title = new Label
        {
            Text = "USB 急救工具",
            Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Bold),
            ForeColor = Fg,
            Location = new Point(80, 38),
            AutoSize = true
        };
        var tip = new Label
        {
            Text = "PE 原生界面 · 点一次后进系统请干看着",
            Font = new Font("Microsoft YaHei UI", 9F),
            ForeColor = Muted,
            Location = new Point(82, 72),
            AutoSize = true
        };
        _status = new Label
        {
            Text = "● PE",
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(74, 106, 128),
            AutoSize = true,
            Location = new Point(620, 30)
        };
        _driveHint = new Label
        {
            ForeColor = Muted,
            Font = new Font("Consolas", 9F),
            AutoSize = true,
            Location = new Point(620, 54)
        };
        header.Controls.AddRange(new Control[] { iconBox, brand, title, tip, _status, _driveHint });
        header.Resize += (_, _) =>
        {
            _status.Left = Math.Max(480, header.Width - 200);
            _driveHint.Left = _status.Left;
        };

        // 按钮区按内容撑开，不裁切；仅日志区在内容过多时滚动
        _actionsHost = new Panel
        {
            Dock = DockStyle.Top,
            Height = 320,
            BackColor = Bg,
            Padding = new Padding(20, 16, 20, 8),
            AutoScroll = false
        };

        // Log card：占满剩余空间，RichTextBox 仅在日志超高时出滚动条
        var logCard = new DoubleBufPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg,
            Padding = new Padding(20, 0, 20, 20),
            MinimumSize = new Size(0, 180)
        };
        var logInner = new DoubleBufPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Card
        };
        logInner.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundRect(new Rectangle(0, 0, logInner.Width - 1, logInner.Height - 1), 10);
            using var pen = new Pen(Color.FromArgb(35, 255, 255, 255));
            e.Graphics.DrawPath(pen, path);
        };
        var logTitle = new Label
        {
            Text = "  输出日志",
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            ForeColor = Fg,
            Dock = DockStyle.Top,
            Height = 40,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent
        };
        _log = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(246, 248, 250),
            ForeColor = Color.FromArgb(90, 101, 112),
            Font = new Font("Consolas", 9.5F),
            DetectUrls = false
        };
        var logPad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 12), BackColor = Color.Transparent };
        logPad.Controls.Add(_log);
        logInner.Controls.Add(logPad);
        logInner.Controls.Add(logTitle);
        logCard.Controls.Add(logInner);

        Controls.Add(logCard);
        Controls.Add(_actionsHost);
        Controls.Add(header);
        Controls.Add(new TitleChrome(this));
        FormRoundCorners.Attach(this, 10);

        Load += (_, _) =>
        {
            RefreshStatus();
            BuildButtons();
            AppendLog("PE 原生美化界面已就绪");
            AppendLog("推荐：点下方绿色主按钮「穷尽修复 + 自动部署远程自启」");
        };
    }

    protected override void WndProc(ref Message m)
    {
        if (BorderlessHit.Handle(this, ref m)) return;
        base.WndProc(ref m);
    }

    private void RefreshStatus()
    {
        var drive = RepairEngine.FindWindowsDrive();
        var remote = FindRemoteFolder() != null;
        _status.Text = "● PE 维护模式";
        _driveHint.Text = drive != null
            ? $"{drive}  ·  Remote:{(remote ? "有" : "无")}"
            : "未检测到系统盘";
    }

    private void BuildButtons()
    {
        _actionsHost.Controls.Clear();
        var items = new (string title, string desc, bool featured, bool danger, Func<Task> act)[]
        {
            ("穷尽修复（不含远程）", "补 USB · 清 UsbDk · 自启修复（远程请用下方独立按钮）", true, false,
                () => Run(() => _engine.RunFullPeRepairAsync(RequireDrive(), CancellationToken.None))),
            ("仅检查问题（完整）", "完整只读体检：服务/过滤/策略/设备/电源，区分严重与提示", false, false,
                () => Run(() => _engine.RunCheckOnlyAsync(CancellationToken.None))),
            ("部署远程软件自启", "独立：清 Zone 标记 + bat 自启，避免安全警告弹窗", false, false,
                () => Run(() => { _engine.RunRemoteDeploy(RequireDrive()); return Task.CompletedTask; })),
            ("UsbDk 专项离线补服务", "双 ControlSet · 清过滤 · 补 Start", false, false,
                () => Run(() => _engine.RunPeUsbFixAsync(RequireDrive(), CancellationToken.None))),
            ("仅部署 USB 开机自检", "只写穷尽修复自启", false, false,
                () => Run(() => _engine.RunDeployBootCheckAsync(RequireDrive(), CancellationToken.None))),
            ("解除账号自动登录", "Administrator 空密码", false, false,
                () => Run(() => _engine.RunAccountUnlockAsync(RequireDrive(), CancellationToken.None))),
            ("刷新状态", "重新检测系统盘 / Remote", false, false,
                () => { RefreshStatus(); AppendLog("状态已刷新"); return Task.CompletedTask; }),
        };

        int y = 0;
        int w = _actionsHost.ClientSize.Width - 48;
        if (w < 400) w = 700;

        // featured full width
        var feat = items[0];
        var featBtn = MakeButton(feat.title, feat.desc, true, false, feat.act, w, 72);
        featBtn.Location = new Point(4, y);
        _actionsHost.Controls.Add(featBtn);
        y += 84;

        int colW = (w - 12) / 2;
        int col = 0;
        for (int i = 1; i < items.Length; i++)
        {
            var it = items[i];
            var btn = MakeButton(it.title, it.desc, false, it.danger, it.act, colW, 62);
            btn.Location = new Point(4 + col * (colW + 12), y);
            _actionsHost.Controls.Add(btn);
            col++;
            if (col == 2) { col = 0; y += 74; }
        }
        if (col != 0) y += 74;

        // 按按钮实际占位撑开，默认全部可见
        _actionsHost.Height = y + _actionsHost.Padding.Top + _actionsHost.Padding.Bottom + 16;
        _actionsHost.Resize -= RelayoutButtons;
        _actionsHost.Resize += RelayoutButtons;
    }

    private void RelayoutButtons(object? sender, EventArgs e)
    {
        if (_actionsHost.Controls.Count == 0) return;
        int w = Math.Max(400, _actionsHost.ClientSize.Width - 48);
        var feat = _actionsHost.Controls[0];
        feat.Width = w;
        feat.Location = new Point(4, 0);
        int y = feat.Height + 12;
        int colW = (w - 12) / 2;
        int col = 0;
        for (int i = 1; i < _actionsHost.Controls.Count; i++)
        {
            var btn = _actionsHost.Controls[i];
            btn.Width = colW;
            btn.Location = new Point(4 + col * (colW + 12), y);
            col++;
            if (col == 2)
            {
                col = 0;
                y += btn.Height + 12;
            }
        }
        if (col != 0) y += _actionsHost.Controls[^1].Height + 12;
        _actionsHost.Height = y + _actionsHost.Padding.Top + _actionsHost.Padding.Bottom + 8;
    }

    private Control MakeButton(string title, string desc, bool featured, bool danger, Func<Task> act, int width, int height)
    {
        var p = new DoubleBufPanel
        {
            Size = new Size(width, height),
            Cursor = Cursors.Hand,
            BackColor = featured ? EmeraldDim : Card
        };
        var tColor = featured ? Emerald : (danger ? Color.FromArgb(252, 165, 165) : Fg);
        var titleLbl = new Label
        {
            Text = title,
            Font = new Font("Microsoft YaHei UI", featured ? 11F : 10F, FontStyle.Bold),
            ForeColor = tColor,
            Location = new Point(16, featured ? 14 : 12),
            AutoSize = true,
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand
        };
        var descLbl = new Label
        {
            Text = desc,
            Font = new Font("Microsoft YaHei UI", 8.25F),
            ForeColor = Muted,
            Location = new Point(16, featured ? 40 : 34),
            Size = new Size(width - 36, 22),
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand
        };
        p.Controls.Add(titleLbl);
        p.Controls.Add(descLbl);
        p.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, p.Width - 1, p.Height - 1);
            using var path = RoundRect(r, 10);
            using var br = new SolidBrush(featured ? EmeraldDim : Card);
            using var pen = new Pen(featured ? Color.FromArgb(100, Emerald) : Color.FromArgb(40, 255, 255, 255));
            e.Graphics.FillPath(br, path);
            e.Graphics.DrawPath(pen, path);
        };
        async void Click(object? s, EventArgs e) { if (!_busy) await act(); }
        p.Click += Click;
        titleLbl.Click += Click;
        descLbl.Click += Click;
        return p;
    }

    private static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private string RequireDrive()
    {
        var d = RepairEngine.FindWindowsDrive();
        if (d == null) throw new InvalidOperationException("未找到 Windows 系统盘");
        return d;
    }

    private static string? FindRemoteFolder()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var p = Path.Combine(exeDir, "Remote");
        if (Directory.Exists(p) && Directory.EnumerateFiles(p, "*.exe", SearchOption.AllDirectories).Any())
            return p;
        return null;
    }

    private async Task Run(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        _actionsHost.Enabled = false;
        _log.Clear();
        try
        {
            AppendLog($"开始 {DateTime.Now:HH:mm:ss}");
            await action();
            AppendLog("");
            AppendColored("✓ 完成。请拔 U 盘重启，然后坐下干看着。", Emerald);
            MessageBox.Show("完成！\n请拔掉 PE U 盘，重启进 Windows，然后干看着。", "完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            AppendColored("✗ " + ex.Message, Color.FromArgb(248, 113, 113));
            MessageBox.Show(ex.Message, "出错", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            _actionsHost.Enabled = true;
            RefreshStatus();
        }
    }

    private void AppendLog(string msg)
    {
        if (InvokeRequired) { BeginInvoke(() => AppendLog(msg)); return; }
        _log.SelectionStart = _log.TextLength;
        _log.SelectionColor = Color.FromArgb(90, 101, 112);
        _log.AppendText(msg + "\n");
        _log.ScrollToCaret();
    }

    private void AppendColored(string msg, Color c)
    {
        if (InvokeRequired) { BeginInvoke(() => AppendColored(msg, c)); return; }
        _log.SelectionStart = _log.TextLength;
        _log.SelectionColor = c;
        _log.AppendText(msg + "\n");
        _log.ScrollToCaret();
    }

    private sealed class DoubleBufPanel : Panel
    {
        public DoubleBufPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }
    }
}
