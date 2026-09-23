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
                Log($"◇ 提示（{report.Attention.Count}）— 键鼠正常通常可忽略：");
                for (var i = 0; i < report.Attention.Count; i++)
                    Log($"  {i + 1}. {report.Attention[i]}");
            }
            if (report.Critical.Count == 0)
            {
                Log("");
                Log("结论: 无严重问题。提示项多为「与修复建议值不同」，你电脑能用就不用管。");
            }
            else
            {
                Log("");
                Log("结论: 存在严重问题。本操作未做修改，需要时请点修复按钮。");
            }
        }

        try
        {
            var reportDir = IsPeEnvironment()
                ? FindWindowsDrive() ?? Environment.CurrentDirectory
                : (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:");
            var path = Path.Combine(reportDir.TrimEnd('\\') + "\\", "usb_check_report.txt");
            var sb = new StringBuilder();
            sb.AppendLine("========================================");
            sb.AppendLine(" USB 完整检查结果说明（给人看的）");
            sb.AppendLine($" 时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($" 模式: {(IsPeEnvironment() ? "PE 离线检查（只读，未改系统）" : "当前 Windows（只读，未改系统）")}");
            sb.AppendLine("========================================");
            sb.AppendLine();
            sb.AppendLine("【一句话结论】");
            if (report.Critical.Count == 0)
            {
                sb.AppendLine("  没有发现会导致键鼠失效的严重问题。");
                sb.AppendLine(report.Attention.Count == 0
                    ? "  可以正常使用。"
                    : "  下面有一些「提示」，键鼠能用就可以忽略。");
            }
            else
            {
                sb.AppendLine($"  发现 {report.Critical.Count} 项严重问题（可能导致键鼠/USB 不可用）。");
                sb.AppendLine("  本检查没有修改任何东西；需要时请回到工具点修复。");
            }
            sb.AppendLine();
            sb.AppendLine("【你要不要管】");
            if (report.Critical.Count == 0)
                sb.AppendLine("  不用修。提示项 ≠ 故障。");
            else
                sb.AppendLine("  建议先看「严重」列表；确认键鼠异常后再点修复。");
            sb.AppendLine();
            sb.AppendLine($"【统计】检查了 {report.CheckedItems} 项 · 严重 {report.Critical.Count} · 提示 {report.Attention.Count}");
            sb.AppendLine();
            sb.AppendLine("【严重】（会导致键鼠/USB 失效的项）");
            if (report.Critical.Count == 0) sb.AppendLine("  （无）");
            else foreach (var i in report.Critical) sb.AppendLine("  · " + i);
            sb.AppendLine();
            sb.AppendLine("【提示】（与建议值不同或需关注；键鼠正常可忽略）");
            if (report.Attention.Count == 0) sb.AppendLine("  （无）");
            else foreach (var i in report.Attention) sb.AppendLine("  · " + i);
            sb.AppendLine();
            sb.AppendLine("----------------------------------------");
            sb.AppendLine(" 说明: 「提示」里的 Start=手动/引导 等，只是和修复脚本");
            sb.AppendLine("       建议值不同，不等于坏了。电脑能用就不用管。");
            sb.AppendLine($" 本文件: {path}");
            await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, ct);
            // 顺带清掉磁盘上旧的「USB 控制器: 异常」极简摘要，避免再点开误导
            var fixReport = Path.Combine(reportDir.TrimEnd('\\') + "\\", "usb_fix_report.txt");
            RewriteObsoleteFixReportIfNeeded(fixReport, path);
            Log($"报告已保存: {path}");
        }
        catch (Exception ex)
        {
            Log("报告写入失败: " + ex.Message);
        }
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
    }

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
