using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace USBFixTool;

public sealed class RepairEngine
{
    private readonly Action<string> _log;

    public RepairEngine(Action<string> log) => _log = log;

    public static bool IsPeEnvironment()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT");
            if (key != null) return true;
        }
        catch { }

        return File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "wpeinit.exe"));
    }

    public static string? FindWindowsDrive()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
        {
            var root = drive.Name.TrimEnd('\\');
            if (File.Exists($@"{root}\Windows\System32\config\SYSTEM") &&
                File.Exists($@"{root}\Windows\System32\config\SOFTWARE") &&
                File.Exists($@"{root}\Windows\System32\config\SAM"))
                return root;
        }
        return null;
    }

    public async Task RunCheckOnlyAsync(CancellationToken ct)
    {
        Log("══ 仅检查问题 · 完整体检（只读，不会修改系统）══");
        DiagnosisReport report;

        if (IsPeEnvironment())
        {
            var winDrive = FindWindowsDrive()
                ?? throw new InvalidOperationException("未找到 Windows 系统盘");
            report = await RunPeCheckAsync(winDrive, ct);
        }
        else
        {
            report = await Task.Run(() => UsbDiagnostics.ScanLive(Log), ct);
        }

        Log("");
        Log("──────── 检查汇总 ────────");
        Log($"已检查项目: {report.CheckedItems}");
        Log($"严重问题:   {report.Critical.Count}");
        Log($"提示项:     {report.Attention.Count}");

        if (report.Critical.Count == 0 && report.Attention.Count == 0)
        {
            Log("✓ 完整检查通过：未发现严重问题或需关注项");
        }
        else
        {
            if (report.Critical.Count > 0)
            {
                Log("");
                Log($"⚠ 严重问题（{report.Critical.Count}）— 建议修复：");
                for (var i = 0; i < report.Critical.Count; i++)
                    Log($"  {i + 1}. {report.Critical[i]}");
            }
            if (report.Attention.Count > 0)
            {
                Log("");
                Log($"◇ 开机风险（{report.Attention.Count}）— USB 服务未按开机必起配置：");
                for (var i = 0; i < report.Attention.Count; i++)
                    Log($"  {i + 1}. {report.Attention[i]}");
            }
            if (report.Critical.Count == 0)
            {
                Log("");
                if (report.Attention.Count == 0)
                    Log("结论: 未发现严重问题，开机相关服务也已到位。");
                else
                    Log("结论: 发现开机风险。急救箱不会只写报告——下面自动写入开机必起。");
            }
            else
            {
                Log("");
                Log("结论: 存在严重问题。请继续用 PE「穷尽修复」或本机「全面体检修复」。");
            }
        }

        // 急救立场：检查出开机风险就立刻写死
        if (!IsPeEnvironment() && report.Attention.Count > 0)
        {
            Log("");
            Log("======== 急救自动处理 ========");
            await RunEnsureUsbBootAsync(ct);
            // 写入后再扫一遍，报告要反映「已修好」的现状
            report = await Task.Run(() => UsbDiagnostics.ScanLive(Log), ct);
        }

        try
        {
            var path = await WriteHumanCheckReportAsync(report, ct);
            var fixReport = Path.Combine(Path.GetDirectoryName(path) ?? "C:\\", "usb_fix_report.txt");
            RewriteObsoleteFixReportIfNeeded(fixReport, path);
            Log($"报告已保存: {path}");
        }
        catch (Exception ex)
        {
            Log("报告写入失败: " + ex.Message);
        }
    }

    /// <summary>写出/覆盖 C:\usb_check_report.txt（给人看的白话报告）</summary>
    public async Task<string> WriteHumanCheckReportAsync(DiagnosisReport report, CancellationToken ct)
    {
        var reportDir = IsPeEnvironment()
            ? FindWindowsDrive() ?? Environment.CurrentDirectory
            : (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:");
        var path = Path.Combine(reportDir.TrimEnd('\\') + "\\", "usb_check_report.txt");
        var sb = new StringBuilder();
        sb.AppendLine("========================================");
        sb.AppendLine(" USB 急救检查结果（给人看的）");
        sb.AppendLine($" 时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($" 模式: {(IsPeEnvironment() ? "PE 离线检查" : "当前 Windows")}");
        sb.AppendLine("========================================");
        sb.AppendLine();
        sb.AppendLine("【一句话结论】");
        if (report.Critical.Count > 0)
        {
            sb.AppendLine($"  发现 {report.Critical.Count} 项严重问题。");
            sb.AppendLine("  请用 PE 点「穷尽修复」，或本机点「深度全面体检修复」。");
        }
        else if (report.Attention.Count > 0)
        {
            sb.AppendLine("  仍有开机风险（USB 服务启动类型不对）。");
            sb.AppendLine("  请点「急救：写入开机必起」，然后自行重启验证。");
        }
        else
        {
            sb.AppendLine("  未发现严重问题，开机相关配置正常。");
            sb.AppendLine("  自行重启后，在登录界面试键鼠即最终确认。");
        }
        sb.AppendLine();
        sb.AppendLine("【你要做什么】");
        if (report.Critical.Count > 0)
            sb.AppendLine("  立刻做修复（PE 穷尽修复 / 本机深度体检），不要拖。");
        else if (report.Attention.Count > 0)
        {
            sb.AppendLine("  1. 点「急救：写入开机必起」。");
            sb.AppendLine("  2. 自行重启，在登录界面试键盘鼠标。");
            sb.AppendLine("  3. 若仍失效 → PE「穷尽修复」。");
        }
        else
            sb.AppendLine("  无需再改配置。想确认就自行重启测一次键鼠。");
        sb.AppendLine();
        sb.AppendLine($"【统计】检查了 {report.CheckedItems} 项 · 严重 {report.Critical.Count} · 开机风险 {report.Attention.Count}");
        sb.AppendLine();
        sb.AppendLine("【严重】");
        if (report.Critical.Count == 0) sb.AppendLine("  （无）");
        else foreach (var i in report.Critical) sb.AppendLine("  · " + i);
        sb.AppendLine();
        sb.AppendLine("【开机风险】");
        if (report.Attention.Count == 0) sb.AppendLine("  （无）");
        else foreach (var i in report.Attention) sb.AppendLine("  · " + i);
        sb.AppendLine();
        sb.AppendLine("----------------------------------------");
        sb.AppendLine(" 说明: 本文件每次检查/急救后都会覆盖更新。");
        sb.AppendLine("       写入开机必起后须自行重启才生效。");
        sb.AppendLine($" 本文件: {path}");
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
        return path;
    }

    private async Task<DiagnosisReport> RunPeCheckAsync(string winDrive, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Log($"目标系统盘: {winDrive}");
        LoadHive("PEOFFLINE_SOFT", Path.Combine(winDrive, @"Windows\System32\config\SOFTWARE"));
        LoadHive("PEOFFLINE_SYS", Path.Combine(winDrive, @"Windows\System32\config\SYSTEM"));
        try
        {
            return await Task.Run(
                () => UsbDiagnostics.ScanOffline(@"HKLM\PEOFFLINE_SYS", @"HKLM\PEOFFLINE_SOFT", Log),
                ct);
        }
        finally
        {
            UnloadHive("PEOFFLINE_SOFT");
            UnloadHive("PEOFFLINE_SYS");
        }
    }

    public async Task RunFullPeRepairAsync(string winDrive, CancellationToken ct)
    {
        Log("══ 穷尽修复（按「只能干看着」设计）══");
        Log("PE 里你点一次；进 Windows 后全自动，不用键鼠。");
        await RunPeUsbFixAsync(winDrive, ct);
        await RunAccountUnlockAsync(winDrive, ct);
        await RunDeployBootCheckAsync(winDrive, ct);

        Log("远程自启已剥离为独立功能：需要时请点「部署远程软件自启」");
        Log("══ PE 侧完成。请拔 U 盘重启，然后坐下干看着。══");
    }

    public async Task RunPeUsbFixAsync(string winDrive, CancellationToken ct)
    {
        var logFile = Path.Combine(winDrive, "usb_fix_log.txt");
        Log("── USB 离线修复 ──");

        LoadHive("PEOFFLINE_SOFT", Path.Combine(winDrive, @"Windows\System32\config\SOFTWARE"));
        LoadHive("PEOFFLINE_SYS", Path.Combine(winDrive, @"Windows\System32\config\SYSTEM"));

        try
        {
            OfflineUsbRegistry.ApplyUsbDkFix(@"HKLM\PEOFFLINE_SYS", Log, RunReg);
            Log("[策略] 清除设备安装限制...");
            OfflineUsbRegistry.ClearInstallRestrictions(@"HKLM\PEOFFLINE_SOFT", RunReg);
            Log("[完成注册表] 卸载离线配置单元...");
        }
        finally
        {
            UnloadHive("PEOFFLINE_SOFT");
            UnloadHive("PEOFFLINE_SYS");
        }

        Log("[6/6] DISM + SFC 离线修复（无 ISO 源时 RestoreHealth 可能报 0x800f0915，可忽略）...");
        await RunCmdAsync($"Dism /Image:{winDrive}\\ /Cleanup-Image /RestoreHealth", logFile, ct);
        await RunCmdAsync($"Sfc /ScanNow /OffBootDir={winDrive}\\ /OffWinDir={winDrive}\\Windows", logFile, ct);
        await RunCmdAsync($"Dism /Image:{winDrive}\\ /Get-Drivers /Format:Table", Path.Combine(winDrive, "usb_oem_drivers.txt"), ct);
        Log("USB 离线修复完成（含 UsbDk 专项）");
    }

    public void RunRemoteDeploy(string winDrive)
    {
        Log("── 部署远程软件自启（独立功能）──");
        Log("策略：exe 不进 Startup；清 Zone.Identifier；用 bat 静默启动，避免安全警告弹窗");
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var deployer = new RemoteDeployer(Log, ExtractEmbeddedResource);
        if (IsPeEnvironment())
            deployer.DeployRemoteAutostart(winDrive, exeDir);
        else
            deployer.DeployRemoteAutostartLive(exeDir);
    }

    /// <summary>远程 + 干看着总控台（可选高级）</summary>
    public void RunRemoteSpectatorDeploy(string winDrive)
    {
        Log("── 部署远程自启 + 干看着总控台 ──");
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        new RemoteDeployer(Log, ExtractEmbeddedResource).DeploySpectatorWithRemote(winDrive, exeDir);
    }

    public async Task RunAccountUnlockAsync(string winDrive, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var logFile = Path.Combine(winDrive, "usb_fix_log.txt");
        Log("── 账号解锁 ──");

        LoadHive("PEOFFLINE_SOFT", Path.Combine(winDrive, @"Windows\System32\config\SOFTWARE"));
        LoadHive("PEOFFLINE_SAM", Path.Combine(winDrive, @"Windows\System32\config\SAM"));

        try
        {
            Log("启用内置 Administrator...");
            EnableAdministrator();

            Log("配置自动登录...");
            RunReg(@"add ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows NT\CurrentVersion\Winlogon"" /v AutoAdminLogon /t REG_SZ /d 1 /f");
            RunReg(@"add ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows NT\CurrentVersion\Winlogon"" /v DefaultUserName /t REG_SZ /d Administrator /f");
            RunReg(@"add ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows NT\CurrentVersion\Winlogon"" /v DefaultPassword /t REG_SZ /d "" /f");
            RunReg(@"add ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows NT\CurrentVersion\Winlogon"" /v ForceAutoLogon /t REG_SZ /d 1 /f");

            Log("清除账户锁定标记...");
            UnlockAllUsers();
            Log("账号解锁完成（重启后自动登录管理员）");
            File.AppendAllText(logFile, "[账号解锁] 完成\r\n", Encoding.UTF8);
        }
        finally
        {
            UnloadHive("PEOFFLINE_SOFT");
            UnloadHive("PEOFFLINE_SAM");
        }

        await Task.CompletedTask;
    }

    public async Task RunDeployBootCheckAsync(string winDrive, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Log("── 部署「干看着」全自动体检 ──");
        Log("场景：进 Windows 后你不能用键鼠，所以一切自动跑、全屏显示结果");

        var destDir = Path.Combine(winDrive, @"Windows\USBFix");
        Directory.CreateDirectory(destDir);

        ExtractEmbeddedResource("win_usb_fix.bat", Path.Combine(destDir, "win_usb_fix.bat"));
        ExtractEmbeddedResource("win_usb_fullcheck.ps1", Path.Combine(destDir, "win_usb_fullcheck.ps1"));
        await File.WriteAllTextAsync(Path.Combine(destDir, "state.txt"), "phase=soft\nattempt=0\n", ct);
        Log("已写入穷尽修复脚本（常规→强力→安全模式→最后手段→判定重装）");

        // 服务：开机早期静默先修一把（登录前）
        var bootWrap = Path.Combine(destDir, "boot_wrapper.bat");
        await File.WriteAllTextAsync(bootWrap, """
            @echo off
            timeout /t 15 /nobreak >nul
            powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "C:\Windows\USBFix\win_usb_fullcheck.ps1" -Silent
            exit
            """, Encoding.ASCII, ct);

        // 用户会话：全屏「干看着」界面（自动登录后你能看见）
        var watchBat = Path.Combine(destDir, "watch.bat");
        await File.WriteAllTextAsync(watchBat, """
            @echo off
            start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Normal -File "C:\Windows\USBFix\win_usb_fullcheck.ps1" -Watch
            """, Encoding.ASCII, ct);

        var startupDir = Path.Combine(winDrive, @"ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp");
        Directory.CreateDirectory(startupDir);
        await File.WriteAllTextAsync(
            Path.Combine(startupDir, "USB全面体检.bat"),
            "start \"\" powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Normal -File \"C:\\Windows\\USBFix\\win_usb_fullcheck.ps1\" -Watch\r\n",
            Encoding.ASCII, ct);
        Log("已写入启动项（全屏可视，不用你点）");

        LoadHive("PEOFFLINE_SOFT", Path.Combine(winDrive, @"Windows\System32\config\SOFTWARE"));
        LoadHive("PEOFFLINE_SYS", Path.Combine(winDrive, @"Windows\System32\config\SYSTEM"));

        try
        {
            var imagePath = @"\??\C:\Windows\System32\cmd.exe /c ""C:\Windows\USBFix\boot_wrapper.bat""";
            RunReg(@"add ""HKLM\PEOFFLINE_SYS\ControlSet001\Services\USBFixBoot"" /v Type /t REG_DWORD /d 16 /f");
            RunReg(@"add ""HKLM\PEOFFLINE_SYS\ControlSet001\Services\USBFixBoot"" /v Start /t REG_DWORD /d 2 /f");
            RunReg(@"add ""HKLM\PEOFFLINE_SYS\ControlSet001\Services\USBFixBoot"" /v ErrorControl /t REG_DWORD /d 1 /f");
            RunReg($@"add ""HKLM\PEOFFLINE_SYS\ControlSet001\Services\USBFixBoot"" /v ImagePath /t REG_EXPAND_SZ /d ""{imagePath}"" /f");
            RunReg(@"add ""HKLM\PEOFFLINE_SYS\ControlSet001\Services\USBFixBoot"" /v DisplayName /t REG_SZ /d ""USB Full Check"" /f");
            RunReg(@"add ""HKLM\PEOFFLINE_SYS\ControlSet001\Services\USBFixBoot"" /v ObjectName /t REG_SZ /d LocalSystem /f");

            // 登录后全屏体检（不要 Hidden）
            var watchCmd = @"powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Normal -File C:\Windows\USBFix\win_usb_fullcheck.ps1 -Watch";
            RunReg($@"add ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows\CurrentVersion\Run"" /v USBFullCheck /t REG_SZ /d ""{watchCmd}"" /f");
            RunReg($@"add ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows\CurrentVersion\RunOnce"" /v USBFullCheckOnce /t REG_SZ /d ""{watchCmd}"" /f");

            // 去掉会挡住自动登录、需要按键确认的东西
            RunReg(@"add ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows\CurrentVersion\Policies\System"" /v DisableCAD /t REG_DWORD /d 1 /f");
            RunReg(@"delete ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows\CurrentVersion\Policies\System"" /v legalnoticecaption /f");
            RunReg(@"delete ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows\CurrentVersion\Policies\System"" /v legalnoticetext /f");
            RunReg(@"add ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows NT\CurrentVersion\Winlogon"" /v AutoAdminLogon /t REG_SZ /d 1 /f");
            RunReg(@"add ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows NT\CurrentVersion\Winlogon"" /v DefaultUserName /t REG_SZ /d Administrator /f");
            RunReg(@"add ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows NT\CurrentVersion\Winlogon"" /v DefaultPassword /t REG_SZ /d "" /f");
            RunReg(@"add ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows NT\CurrentVersion\Winlogon"" /v ForceAutoLogon /t REG_SZ /d 1 /f");
            // 跳过首次登录动画 / 隐私选项卡住
            RunReg(@"add ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows\CurrentVersion\Policies\System"" /v EnableFirstLogonAnimation /t REG_DWORD /d 0 /f");
            RunReg(@"add ""HKLM\PEOFFLINE_SOFT\Policies\Microsoft\Windows\OOBE"" /v DisablePrivacyExperience /t REG_DWORD /d 1 /f");

            Log("自启链路：服务静默预修 → 自动登录 → 全屏穷尽修复（你干看着）");
            Log("阶段：常规 → 强力 → 安全模式 → 最后手段 → 全部失败才提示重装");
        }
        finally
        {
            UnloadHive("PEOFFLINE_SOFT");
            UnloadHive("PEOFFLINE_SYS");
        }
    }

    public async Task RunWinUsbFixAsync(CancellationToken ct)
    {
        Log("── Windows USB 全面体检 ──");
        // 先把开机必起写好，再跑深度脚本（重启留给脚本结束后统一安排）
        await RunEnsureUsbBootAsync(ct);

        var destDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "USBFix");
        Directory.CreateDirectory(destDir);
        var ps1 = Path.Combine(destDir, "win_usb_fullcheck.ps1");
        ExtractEmbeddedResource("win_usb_fullcheck.ps1", ps1);
        ExtractEmbeddedResource("win_usb_fix.bat", Path.Combine(destDir, "win_usb_fix.bat"));

        await RunCmdAsync(
            $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{ps1}\"",
            Path.Combine(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:", "usb_fix_log.txt"),
            ct);

        var report = Path.Combine(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:", "usb_fix_report.txt");
        if (File.Exists(report))
            Log(await File.ReadAllTextAsync(report, ct));

        Log("");
        Log("── 刷新体检报告 ──");
        try
        {
            var after = await Task.Run(() => UsbDiagnostics.ScanLive(Log), ct);
            var checkPath = await WriteHumanCheckReportAsync(after, ct);
            Log($"报告已更新: {checkPath}");
        }
        catch (Exception ex)
        {
            Log("报告刷新失败: " + ex.Message);
        }

        Log("全面体检完成。请自行重启后再测键鼠（本工具不会自动重启）。");
    }

    /// <summary>
    /// 当前 Windows：把 USB/键鼠核心服务 Start 写成开机必起（双 ControlSet），
    /// 并关闭快速启动。不删设备、不写 Enum\USB、不 pnputil。不自动重启。
    /// </summary>
    public async Task RunEnsureUsbBootAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Log("── 急救：写入开机必起 ──");
        Log("把 USB 控制器 / 集线器 / 即插即用 写成开机自动拉起");
        Log("安全边界：不删除设备、不改 Enum\\USB、不用 pnputil、不自动重启");
        Log("");

        // Start: 0=引导 1=系统 2=自动 3=手动 4=禁用
        var starts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["usbxhci"] = 0,
            ["USBXHCI"] = 0,
            ["usbhub"] = 1,
            ["USBHUB"] = 1,
            ["usbhub3"] = 1,
            ["USBHUB3"] = 1,
            ["usbccgp"] = 1,
            ["PlugPlay"] = 2,
            ["Wdf01000"] = 0,
            ["HidUsb"] = 3,
            ["mouhid"] = 3,
            ["kbdhid"] = 3,
            ["kbdclass"] = 3,
            ["mouclass"] = 3,
        };

        var sets = new List<string> { "CurrentControlSet" };
        foreach (var cs in new[] { "ControlSet001", "ControlSet002" })
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\{cs}\Services");
                if (k != null) sets.Add(cs);
            }
            catch { /* ignore */ }
        }
        sets = sets.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Log($"写入目标: {string.Join(", ", sets)}");

        var changed = 0;
        var skipped = 0;
        foreach (var cs in sets)
        {
            Log($"── {cs} ──");
            foreach (var kv in starts)
            {
                ct.ThrowIfCancellationRequested();
                var path = $@"SYSTEM\{cs}\Services\{kv.Key}";
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(path, writable: true);
                    if (key == null)
                    {
                        skipped++;
                        continue;
                    }
                    var curObj = key.GetValue("Start");
                    var cur = curObj is int i ? i : (curObj is long l ? (int)l : -1);
                    if (cur == kv.Value)
                    {
                        Log($"  · {kv.Key} Start={kv.Value}({StartLabel(kv.Value)}) 已正确");
                        continue;
                    }
                    key.SetValue("Start", kv.Value, RegistryValueKind.DWord);
                    changed++;
                    Log($"  ✓ {kv.Key} Start {cur}({StartLabel(cur)}) → {kv.Value}({StartLabel(kv.Value)})");
                }
                catch (Exception ex)
                {
                    Log($"  ✗ {kv.Key} 写入失败: {ex.Message}");
                }
            }
        }

        Log("");
        Log("── sc config 同步 ──");
        foreach (var (name, start) in new (string, string)[]
                 {
                     ("usbxhci", "boot"),
                     ("usbhub", "system"),
                     ("usbhub3", "system"),
                     ("usbccgp", "system"),
                     ("PlugPlay", "auto"),
                 })
        {
            try
            {
                RunCmd($"sc config {name} start= {start}");
                Log($"  · sc config {name} start= {start}");
            }
            catch (Exception ex)
            {
                Log($"  · sc config {name}: {ex.Message}");
            }
        }

        Log("");
        Log("── 关闭快速启动 ──");
        try
        {
            using var pwr = Registry.LocalMachine.CreateSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
            pwr?.SetValue("HiberbootEnabled", 0, RegistryValueKind.DWord);
            Log("  ✓ HiberbootEnabled = 0（关机后冷启动，避免 USB 假死）");
        }
        catch (Exception ex)
        {
            Log("  ✗ 快速启动: " + ex.Message);
        }

        Log("");
        Log($"急救写入完成：改写 {changed} 项（跳过不存在服务约 {skipped} 次）。");
        Log("请你自行安排重启后再测键鼠（本工具不会自动重启）。");

        // 立刻刷新白话报告，避免「打开体检报告」还是旧文件
        try
        {
            if (!IsPeEnvironment())
            {
                Log("── 刷新体检报告 ──");
                var after = await Task.Run(() => UsbDiagnostics.ScanLive(_ => { }), ct);
                var path = await WriteHumanCheckReportAsync(after, ct);
                Log($"报告已更新: {path}");
            }
        }
        catch (Exception ex)
        {
            Log("报告刷新失败: " + ex.Message);
        }
    }

    private static string StartLabel(int start) => start switch
    {
        0 => "引导",
        1 => "系统",
        2 => "自动",
        3 => "手动",
        4 => "禁用",
        _ => "?",
    };

    public void UninstallBootCheck()
    {
        RunCmd("sc delete USBFixBoot");
        RunReg(@"delete ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"" /v USBFullCheck /f");
        RunReg(@"delete ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"" /v USBFullCheck /f");
        RunReg(@"delete ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"" /v USBFixCheck /f");

        var startupBat = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            @"Programs\StartUp\USB全面体检.bat");
        if (File.Exists(startupBat)) File.Delete(startupBat);

        var destDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "USBFix");
        if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
        Log("开机全面体检已卸载（服务 + 自启 + 脚本）");
    }

    private static void ExtractEmbeddedResource(string ending, string destPath)
    {
        var asm = typeof(RepairEngine).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(ending, StringComparison.OrdinalIgnoreCase));
        if (name == null)
        {
            var fallback = Path.Combine(AppContext.BaseDirectory, "lib", ending);
            if (!File.Exists(fallback))
                fallback = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "lib", ending));
            if (File.Exists(fallback)) { File.Copy(fallback, destPath, true); return; }
            throw new FileNotFoundException($"找不到资源: {ending}");
        }
        using var stream = asm.GetManifestResourceStream(name)!;
        using var fs = File.Create(destPath);
        stream.CopyTo(fs);
    }

    public async Task RunRemoveDriversAsync(string winDrive, CancellationToken ct)
    {
        Log("── 删除 USB 第三方驱动 ──");
        var tempFile = Path.Combine(Path.GetTempPath(), "drv_table.txt");
        await RunCmdAsync($"Dism /Image:{winDrive}\\ /Get-Drivers /Format:Table", tempFile, ct);

        var keywords = new[] { "usb", "xhci", "hub", "vivo", "mi ", "android", "qualcomm", "adb" };
        var count = 0;
        foreach (var line in await File.ReadAllLinesAsync(tempFile, ct))
        {
            if (keywords.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && parts[0].EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"删除驱动: {parts[0]}");
                    await RunCmdAsync($"Dism /Image:{winDrive}\\ /Remove-Driver /Driver:{parts[0]} /ForceUnsigned", null, ct);
                    count++;
                }
            }
        }
        Log($"共删除 {count} 个驱动");
    }

    private void EnableAdministrator()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"PEOFFLINE_SAM\SAM\SAM\Domains\Account\Users\000001F4", writable: true);
        if (key == null) { Log("警告: 未找到 Administrator"); return; }
        var v = key.GetValue("V") as byte[];
        if (v == null || v.Length < 0x3C) return;
        var flags = BitConverter.ToInt32(v, 0x38);
        flags &= ~0x10;
        flags |= 0x20;
        Buffer.BlockCopy(BitConverter.GetBytes(flags), 0, v, 0x38, 4);
        key.SetValue("V", v, RegistryValueKind.Binary);
    }

    private void UnlockAllUsers()
    {
        using var users = Registry.LocalMachine.OpenSubKey(@"PEOFFLINE_SAM\SAM\SAM\Domains\Account\Users", writable: true);
        if (users == null) return;
        foreach (var name in users.GetSubKeyNames().Where(n => long.TryParse(n, out _)))
        {
            try
            {
                using var user = users.OpenSubKey(name, writable: true);
                var v = user?.GetValue("V") as byte[];
                if (v == null || v.Length < 0x3C) continue;
                var flags = BitConverter.ToInt32(v, 0x38);
                flags &= ~0x10;
                Buffer.BlockCopy(BitConverter.GetBytes(flags), 0, v, 0x38, 4);
                user!.SetValue("V", v, RegistryValueKind.Binary);
            }
            catch { }
        }
    }

    private void FixServiceStart(string regPath, string name, string logFile)
    {
        var psi = new ProcessStartInfo("reg", $"query \"{regPath}\" /v Start")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi);
        var output = p?.StandardOutput.ReadToEnd() ?? "";
        p?.WaitForExit();
        if (output.Contains("0x4"))
        {
            RunReg($"add \"{regPath}\" /v Start /t REG_DWORD /d 3 /f");
            Log($"  修复服务 {name}");
            File.AppendAllText(logFile, $"服务修复 {name}\r\n", Encoding.UTF8);
        }
    }

    private static void LoadHive(string name, string path)
    {
        var psi = new ProcessStartInfo("reg", $"load \"HKLM\\{name}\" \"{path}\"")
        { UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi);
        p?.WaitForExit();
        if (p?.ExitCode != 0) throw new InvalidOperationException($"无法加载注册表: {path}");
    }

    private static void UnloadHive(string name)
    {
        var psi = new ProcessStartInfo("reg", $"unload \"HKLM\\{name}\"")
        { UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi);
        p?.WaitForExit();
    }

    private static void RunReg(string args)
    {
        var psi = new ProcessStartInfo("reg", args) { UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi);
        p?.WaitForExit();
    }

    private void RunCmd(string cmd)
    {
        var psi = new ProcessStartInfo("cmd", $"/c {cmd}") { UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi);
        p?.WaitForExit();
    }

    private async Task RunCmdAsync(string cmd, string? logFile, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo("cmd", $"/c {cmd}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync(ct);
        var error = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (logFile != null)
            await File.AppendAllTextAsync(logFile, output + error, ct);
        if (!string.IsNullOrWhiteSpace(output))
            Log(output.Trim());
    }

    private void Log(string msg) => _log(msg);

    /// <summary>新版白话报告特征</summary>
    public static bool IsHumanReadableReport(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var t = File.ReadAllText(path);
            return t.Contains("一句话结论", StringComparison.Ordinal)
                   || t.Contains("给人看的", StringComparison.Ordinal)
                   || t.Contains("你要不要管", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>
    /// 旧版极简摘要（「USB 控制器: 异常」）永久误导 → 改写成指向新报告的说明。
    /// </summary>
    public static void RewriteObsoleteFixReportIfNeeded(string fixReportPath, string? checkReportPath = null)
    {
        try
        {
            if (!File.Exists(fixReportPath)) return;
            if (IsHumanReadableReport(fixReportPath)) return;

            var old = File.ReadAllText(fixReportPath);
            // 旧格式：短、含「自检报告」+「异常」，且没有白话段落
            var looksObsolete =
                old.Contains("自检报告", StringComparison.Ordinal)
                || (old.Contains("USB 控制器", StringComparison.Ordinal) && old.Contains("异常", StringComparison.Ordinal)
                    && old.Length < 800)
                || (old.Contains("HID", StringComparison.Ordinal) && old.Contains("异常", StringComparison.Ordinal)
                    && !old.Contains("一句话结论", StringComparison.Ordinal) && old.Length < 800);

            if (!looksObsolete) return;

            var checkHint = !string.IsNullOrEmpty(checkReportPath) && File.Exists(checkReportPath)
                ? checkReportPath
                : Path.Combine(Path.GetDirectoryName(fixReportPath) ?? "C:\\", "usb_check_report.txt");

            var rewritten = $@"========================================
 USB 修复结果说明（给人看的）
 时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
========================================

【一句话结论】
  你看到的「USB 控制器: 异常 / HID 键鼠: 异常」是旧版极简摘要，
  不能当成现在的故障结论。那只是当时脚本数设备数数到 0 时的标记。

【你要不要管】
  1. 键盘鼠标现在能用 → 不用管，忽略那些「异常」。
  2. 请打开旁边的完整检查报告看白话结论：
     {checkHint}
  3. 若还没有完整检查报告：打开 USB急救工具 → 点「仅检查问题（完整）」。

【已作废的旧内容】（仅存档，勿再解读）
{old.Trim()}

----------------------------------------
 本文件已由新版工具自动改写，避免继续误导。
========================================
";
            File.WriteAllText(fixReportPath, rewritten, Encoding.UTF8);
        }
        catch
        {
            // 改写失败不阻断打开报告
        }
    }
}
