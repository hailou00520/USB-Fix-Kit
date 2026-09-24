using System.Drawing.Drawing2D;

namespace USBFixTool;

/// <summary>PE 原生界面（无 WebView2）——视觉对齐 Windows 网页版青绿风格。</summary>
public sealed class NativePeForm : Form
{
    private readonly RepairEngine _engine;
    private readonly RichTextBox _log;
    private readonly Label _status;
    private readonly Label _driveHint;
    private readonly Label _modeTitle;
    private readonly Panel _actionsHost;
    private bool _busy;

    // 对齐 Web 主题：teal-700 / 雾面背景
    private static readonly Color AppBg = Color.FromArgb(221, 232, 228);
    private static readonly Color Card = Color.FromArgb(250, 252, 251);
    private static readonly Color Fg = Color.FromArgb(24, 24, 27);
    private static readonly Color Muted = Color.FromArgb(113, 113, 122);
    private static readonly Color Teal = Color.FromArgb(15, 118, 110);
    private static readonly Color TealSoft = Color.FromArgb(240, 253, 250);
    private static readonly Color TealBorder = Color.FromArgb(153, 246, 228);
    private static readonly Color Danger = Color.FromArgb(185, 28, 28);

    public NativePeForm()
    {
        _engine = new RepairEngine(AppendLog);
        Text = Program.ForcePeUi ? "一体化急救工具 · PE 预览" : "一体化急救工具 · PE";
        Size = new Size(820, 740);
        MinimumSize = new Size(680, 600);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = AppBg;
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
        catch { /* ignore */ }

        var header = new DoubleBufPanel { Dock = DockStyle.Top, Height = 118, BackColor = Color.Transparent };
        header.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(16, 12, header.Width - 32, header.Height - 16);
            using var path = RoundRect(r, 12);
            using var br = new SolidBrush(Color.FromArgb(230, 255, 255, 255));
            using var pen = new Pen(Color.FromArgb(40, 15, 118, 110));
            e.Graphics.FillPath(br, path);
            e.Graphics.DrawPath(pen, path);
            using var glow = new SolidBrush(Color.FromArgb(36, 45, 212, 191));
            e.Graphics.FillEllipse(glow, header.Width - 180, -30, 160, 100);
        };

        var iconBox = new PictureBox
        {
            Location = new Point(32, 32),
            Size = new Size(40, 40),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent
        };
        try
        {
            var png = Path.Combine(AppContext.BaseDirectory, "wwwroot", "icon.png");
            if (!File.Exists(png)) png = Path.Combine(AppContext.BaseDirectory, "icon.png");
            if (File.Exists(png)) iconBox.Image = Image.FromFile(png);
        }
        catch { /* ignore */ }

        var brand = new Label
        {
            Text = "一体化急救工具",
            Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Bold),
            ForeColor = Teal,
            Location = new Point(84, 28),
            AutoSize = true,
            BackColor = Color.Transparent
        };
        _modeTitle = new Label
        {
            Text = "PE 离线急救",
            Font = new Font("Microsoft YaHei UI", 18F, FontStyle.Bold),
            ForeColor = Fg,
            Location = new Point(82, 46),
            AutoSize = true,
            BackColor = Color.Transparent
        };
        var tip = new Label
        {
            Text = "U 盘进 PE → 点一次 → 拔盘重启进 Windows → 干看着（不需网络）",
            Font = new Font("Microsoft YaHei UI", 9F),
            ForeColor = Muted,
            Location = new Point(84, 80),
            AutoSize = true,
            BackColor = Color.Transparent
        };

        _status = new Label
        {
            Text = "● PE",
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            ForeColor = Teal,
            AutoSize = true,
            BackColor = TealSoft,
            Padding = new Padding(8, 4, 8, 4),
            Location = new Point(560, 34)
        };
        _driveHint = new Label
        {
            ForeColor = Teal,
            Font = new Font("Consolas", 9.5F, FontStyle.Bold),
            AutoSize = true,
            BackColor = Color.Transparent,
            Location = new Point(560, 64)
        };
        header.Controls.AddRange(new Control[] { iconBox, brand, _modeTitle, tip, _status, _driveHint });
        header.Resize += (_, _) =>
        {
            var right = Math.Max(420, header.Width - 220);
            _status.Left = right;
            _driveHint.Left = right;
        };

        _actionsHost = new Panel
        {
            Dock = DockStyle.Top,
            Height = 360,
            BackColor = Color.Transparent,
            Padding = new Padding(16, 4, 16, 8),
            AutoScroll = false
        };

        var logCard = new DoubleBufPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding = new Padding(16, 0, 16, 16),
            MinimumSize = new Size(0, 160)
        };
        var logInner = new DoubleBufPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        logInner.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, logInner.Width - 1, logInner.Height - 1);
            using var path = RoundRect(r, 12);
            using var br = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
            using var pen = new Pen(Color.FromArgb(50, 15, 118, 110));
            e.Graphics.FillPath(br, path);
            e.Graphics.DrawPath(pen, path);
        };
        var logHead = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = Color.Transparent };
        var logTitle = new Label
        {
            Text = "输出日志",
            Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold),
            ForeColor = Fg,
            Location = new Point(14, 12),
            AutoSize = true,
            BackColor = Color.Transparent
        };
        var logSub = new Label
        {
            Text = "实时进度 · 修好后拔 U 盘重启",
            Font = new Font("Microsoft YaHei UI", 8F),
            ForeColor = Muted,
            Location = new Point(88, 15),
            AutoSize = true,
            BackColor = Color.Transparent
        };
        logHead.Controls.Add(logTitle);
        logHead.Controls.Add(logSub);

        _log = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(248, 250, 249),
            ForeColor = Color.FromArgb(82, 82, 91),
            Font = new Font("Consolas", 9.5F),
            DetectUrls = false
        };
        var logPad = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 0, 12, 12),
            BackColor = Color.Transparent
        };
        logPad.Controls.Add(_log);
        logInner.Controls.Add(logPad);
        logInner.Controls.Add(logHead);
        logCard.Controls.Add(logInner);

        Controls.Add(logCard);
        Controls.Add(_actionsHost);
        Controls.Add(header);
        Controls.Add(new TitleChrome(this));
        FormRoundCorners.Attach(this, 12);

        Load += (_, _) =>
        {
            RefreshStatus();
            BuildButtons();
            var preview = Program.ForcePeUi && !RepairEngine.IsPeEnvironment();
            AppendLog(preview
                ? "当前为 PE 界面预览（正常 Windows）。点修复无效——真急救请用 U 盘进 PE。"
                : "PE 媒介模式：离线修目标盘 + 部署开机自修脚本");
            AppendLog("主流程（不需网）：①穷尽修复并部署 → 拔盘重启 → 进系统干看着");
            AppendLog("远程自启是可选支线：进系统后没网连不上就别点");
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
        var preview = Program.ForcePeUi && !RepairEngine.IsPeEnvironment();
        _status.Text = preview ? "● PE 预览（非真实 PE）" : "● PE 维护模式";
        _status.ForeColor = preview ? Color.FromArgb(3, 105, 161) : Teal;
        _status.BackColor = preview ? Color.FromArgb(224, 242, 254) : TealSoft;
        _driveHint.Text = drive != null
            ? $"目标盘 {drive}  ·  Remote:{(remote ? "有" : "无")}"
            : "未检测到系统盘";
        _modeTitle.Text = preview ? "PE 界面预览" : "PE 媒介急救";
    }

    private void BuildButtons()
    {
        _actionsHost.Controls.Clear();
        var items = new (string title, string desc, bool featured, bool danger, Func<Task> act)[]
        {
            ("① 穷尽修复并部署合并自修",
                "媒介主流程·不需网：离线修 → 写 USB+网开机自修 → 进系统全屏自跑",
                true, false,
                () => RunHive(() => _engine.RunFullPeRepairAsync(RequireDrive(), CancellationToken.None))),
            ("部署合并自修（USB+网）", "只写自启：键鼠/USB + 有线网一起修", false, false,
                () => RunHive(() => _engine.RunDeployBootCheckAsync(RequireDrive(), BootFixScope.Both, CancellationToken.None))),
            ("仅部署键鼠/USB 自修", "开机只穷尽修 USB/键鼠", false, false,
                () => RunHive(() => _engine.RunDeployBootCheckAsync(RequireDrive(), BootFixScope.Usb, CancellationToken.None))),
            ("仅部署救网自修", "开机专修有线网，方便远程接手", false, false,
                () => RunHive(() => _engine.RunDeployBootCheckAsync(RequireDrive(), BootFixScope.Net, CancellationToken.None))),
            ("部署远程软件自启", "进系统后要上网才能连；没网请先部署救网", false, false,
                () => RunHive(() => { _engine.RunRemoteDeploy(RequireDrive()); return Task.CompletedTask; })),
            ("仅检查问题", "只读体检，不修改", false, false,
                () => Run(() => _engine.RunCheckOnlyAsync(CancellationToken.None))),
            ("UsbDk / ImagePath 离线补丁", "双 ControlSet · 清过滤 · 修 usbxhci ImagePath", false, false,
                () => RunHive(() => _engine.RunPeUsbFixAsync(RequireDrive(), CancellationToken.None))),
            ("解除账号 · 自动登录", "启用 Administrator · 空密码，保证进系统后自修能跑", false, false,
                () => RunHive(() => _engine.RunAccountUnlockAsync(RequireDrive(), CancellationToken.None))),
            ("刷新状态", "重新检测系统盘 / Remote 目录", false, false,
                () => { RefreshStatus(); AppendLog("状态已刷新"); return Task.CompletedTask; }),
        };

        int y = 0;
        int w = Math.Max(400, _actionsHost.ClientSize.Width - 40);

        var feat = items[0];
        var featBtn = MakeButton(feat.title, feat.desc, true, false, feat.act, w, 78);
        featBtn.Location = new Point(4, y);
        _actionsHost.Controls.Add(featBtn);
        y += 90;

        int colW = (w - 12) / 2;
        int col = 0;
        for (int i = 1; i < items.Length; i++)
        {
            var it = items[i];
            var btn = MakeButton(it.title, it.desc, false, it.danger, it.act, colW, 68);
            btn.Location = new Point(4 + col * (colW + 12), y);
            _actionsHost.Controls.Add(btn);
            col++;
            if (col == 2) { col = 0; y += 80; }
        }
        if (col != 0) y += 80;

        _actionsHost.Height = y + _actionsHost.Padding.Top + _actionsHost.Padding.Bottom + 8;
        _actionsHost.Resize -= RelayoutButtons;
        _actionsHost.Resize += RelayoutButtons;
    }

    private void RelayoutButtons(object? sender, EventArgs e)
    {
        if (_actionsHost.Controls.Count == 0) return;
        int w = Math.Max(400, _actionsHost.ClientSize.Width - 40);
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
            BackColor = Color.Transparent
        };

        var titleLbl = new Label
        {
            Text = title,
            Font = new Font("Microsoft YaHei UI", featured ? 12F : 10F, FontStyle.Bold),
            ForeColor = featured ? Color.White : (danger ? Danger : Fg),
            Location = new Point(featured ? 18 : 14, featured ? 14 : 12),
            AutoSize = true,
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand
        };
        var descLbl = new Label
        {
            Text = desc,
            Font = new Font("Microsoft YaHei UI", 8.25F),
            ForeColor = featured ? Color.FromArgb(220, 255, 255, 255) : Muted,
            Location = new Point(featured ? 18 : 14, featured ? 42 : 36),
            Size = new Size(width - (featured ? 48 : 28), featured ? 28 : 24),
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand
        };
        p.Controls.Add(titleLbl);
        p.Controls.Add(descLbl);

        if (featured)
        {
            var badge = new Label
            {
                Text = "推荐",
                Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(60, 255, 255, 255),
                AutoSize = true,
                Padding = new Padding(6, 2, 6, 2),
                Location = new Point(width - 56, 14),
                Cursor = Cursors.Hand
            };
            p.Controls.Add(badge);
            p.Resize += (_, _) => badge.Left = Math.Max(120, p.Width - 56);
        }

        p.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, p.Width - 1, p.Height - 1);
            using var path = RoundRect(r, 12);
            if (featured)
            {
                using var br = new SolidBrush(Teal);
                e.Graphics.FillPath(br, path);
                using var bar = new SolidBrush(Color.FromArgb(180, 94, 234, 212));
                e.Graphics.FillRectangle(bar, 0, 8, 4, p.Height - 16);
            }
            else
            {
                using var br = new SolidBrush(Color.FromArgb(240, 255, 255, 255));
                using var pen = new Pen(Color.FromArgb(55, 15, 118, 110));
                e.Graphics.FillPath(br, path);
                e.Graphics.DrawPath(pen, path);
            }
        };

        async void Click(object? s, EventArgs e) { if (!_busy) await act(); }
        p.Click += Click;
        foreach (Control c in p.Controls) c.Click += Click;
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

    private static void RejectPePreviewHive()
    {
        if (Program.ForcePeUi && !RepairEngine.IsPeEnvironment())
            throw new InvalidOperationException(
                "当前是 PE 界面预览，不能挂载本机正在使用的注册表。真急救请用 U 盘进 PE 后再点①。");
    }

    private static string? FindRemoteFolder()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var p = Path.Combine(exeDir, "Remote");
        if (Directory.Exists(p) && Directory.EnumerateFiles(p, "*.exe", SearchOption.AllDirectories).Any())
            return p;
        return null;
    }

    private Task RunHive(Func<Task> action) => Run(() =>
    {
        RejectPePreviewHive();
        return action();
    });

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
            AppendColored("✓ 完成。请拔 U 盘重启，然后坐下干看着。", Teal);
            MessageBox.Show("完成！\n请拔掉 PE U 盘，重启进 Windows，然后干看着。", "完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            AppendColored("✗ " + ex.Message, Color.FromArgb(220, 38, 38));
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
        _log.SelectionColor = Color.FromArgb(82, 82, 91);
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
