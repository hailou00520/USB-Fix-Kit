using System.Text;

namespace USBFixTool;

/// <summary>
/// 部署远程绿色版自启（独立功能）。
/// 禁止把 exe 放进 Startup（会弹「打开文件-安全警告」）；只写 bat + 去掉 Zone.Identifier。
/// </summary>
public sealed class RemoteDeployer
{
    private readonly Action<string> _log;
    private readonly Action<string, string>? _extract;

    public RemoteDeployer(Action<string> log, Action<string, string>? extractEmbedded = null)
    {
        _log = log;
        _extract = extractEmbedded;
    }

    /// <summary>仅部署远程软件开机自启（不附带 USB 穷尽修复）</summary>
    public void DeployRemoteAutostart(string winDrive, string? toolDir = null)
    {
        toolDir ??= AppContext.BaseDirectory;
        var remoteSrc = FindRemoteFolder(toolDir);
        if (remoteSrc == null)
            throw new InvalidOperationException(
                "未找到 Remote 绿色版。请把 ToDesk/向日葵绿色版整个文件夹放到程序同目录的 Remote\\ 下。");

        var dest = Path.Combine(winDrive, @"Tools\RemoteFix");
        Directory.CreateDirectory(dest);
        CopyDir(remoteSrc, dest);
        _log($"已复制远程软件到 {dest}");

        UnblockTree(dest);
        _log("已清除下载标记（Zone.Identifier），避免弹安全警告");

        var exeRel = FindMainExe(dest);
        if (exeRel == null)
            throw new InvalidOperationException("Remote 文件夹里找不到 exe 主程序");
        var exeWin = @"C:\Tools\RemoteFix\" + exeRel.Replace('/', '\\');
        _log($"主程序: {exeWin}");

        // 清掉 Startup 里已有的远程 exe（正是弹安全警告的元凶）
        var removed = PurgeRemoteExesFromStartup(winDrive);
        if (removed > 0)
            _log($"已从启动文件夹移除 {removed} 个远程 exe（改为 bat 启动）");

        var fixDir = Path.Combine(winDrive, @"Windows\USBFix");
        Directory.CreateDirectory(fixDir);

        // 用 bat 启动，不要把 exe 放进 Startup
        var startBat = Path.Combine(fixDir, "start_remote.bat");
        File.WriteAllText(startBat, BuildRemoteStartBat(exeWin), Encoding.ASCII);

        var startup = Path.Combine(winDrive, @"ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp");
        Directory.CreateDirectory(startup);
        // 只放 bat；文件名不用 Remote 以免和旧 exe 混淆
        File.WriteAllText(Path.Combine(startup, "USB远程自启.bat"),
            "@echo off\r\ncall \"C:\\Windows\\USBFix\\start_remote.bat\"\r\n", Encoding.ASCII);

        // 删掉旧的可能有问题的启动项名
        TryDelete(Path.Combine(startup, "RemoteFix.bat"));
        TryDelete(Path.Combine(startup, "ToDesk_Lite.exe"));
        TryDelete(Path.Combine(startup, "ToDesk.exe"));

        LoadSoft(winDrive);
        try
        {
            // 附件策略：不再保存「来自 Internet」区域信息
            RunReg(@"add ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows\CurrentVersion\Policies\Attachments"" /v SaveZoneInformation /t REG_DWORD /d 2 /f");
            RunReg(@"add ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows\CurrentVersion\Policies\Associations"" /v LowRiskFileTypes /t REG_SZ /d "".exe;.bat;.cmd;.vbs"" /f");

            RunReg(@"add ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows\CurrentVersion\Run"" /v USBRemoteAutostart /t REG_SZ /d ""C:\Windows\USBFix\start_remote.bat"" /f");
            // 去掉旧键
            RunReg(@"delete ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows\CurrentVersion\Run"" /v RemoteFix /f");
            RunReg(@"delete ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows\CurrentVersion\Run"" /v USBWatchDash /f");

            ApplySpectatorLogon();
            _log("已写入自启：Run + Startup\\USB远程自启.bat → 启动 Tools\\RemoteFix 内程序");
            _log("不会把 exe 放进 Startup，开机不应再弹「打开文件-安全警告」");
        }
        finally
        {
            Unload("PEOFFLINE_SOFT");
        }

        _log("完成。拔 U 盘重启即可；远程会静默拉起。");
    }

    /// <summary>远程自启 + 干看着总控台（IP + 远程 + USB 自修）</summary>
    public void DeploySpectatorWithRemote(string winDrive, string? toolDir = null)
    {
        DeployRemoteAutostart(winDrive, toolDir);

        var fixDir = Path.Combine(winDrive, @"Windows\USBFix");
        Directory.CreateDirectory(fixDir);
        _extract?.Invoke("win_usb_fullcheck.ps1", Path.Combine(fixDir, "win_usb_fullcheck.ps1"));
        _extract?.Invoke("win_watch_dashboard.ps1", Path.Combine(fixDir, "win_watch_dashboard.ps1"));
        _extract?.Invoke("win_usb_fix.bat", Path.Combine(fixDir, "win_usb_fix.bat"));
        File.WriteAllText(Path.Combine(fixDir, "state.txt"), "phase=soft\nattempt=0\n", Encoding.ASCII);

        var exeRel = FindMainExe(Path.Combine(winDrive, @"Tools\RemoteFix"));
        var exeWin = exeRel != null
            ? @"C:\Tools\RemoteFix\" + exeRel.Replace('/', '\\')
            : "";

        var watchBat = Path.Combine(fixDir, "start_watch.bat");
        File.WriteAllText(watchBat,
            "@echo off\r\n" +
            "call \"C:\\Windows\\USBFix\\start_remote.bat\"\r\n" +
            (string.IsNullOrEmpty(exeWin)
                ? "start \"\" powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Normal -File \"C:\\Windows\\USBFix\\win_watch_dashboard.ps1\" -SkipRemote\r\n"
                : $"start \"\" powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Normal -File \"C:\\Windows\\USBFix\\win_watch_dashboard.ps1\" -RemoteExe \"{exeWin}\"\r\n"),
            Encoding.ASCII);

        var startup = Path.Combine(winDrive, @"ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp");
        Directory.CreateDirectory(startup);
        File.WriteAllText(Path.Combine(startup, "USB干看着总控台.bat"),
            "@echo off\r\ncall \"C:\\Windows\\USBFix\\start_watch.bat\"\r\n", Encoding.ASCII);

        LoadSoft(winDrive);
        try
        {
            RunReg(@"add ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows\CurrentVersion\Run"" /v USBWatchDash /t REG_SZ /d ""C:\Windows\USBFix\start_watch.bat"" /f");
            _log("已叠加部署干看着总控台（全屏 IP + 远程 + USB 自修）");
        }
        finally
        {
            Unload("PEOFFLINE_SOFT");
        }
    }

    private static string BuildRemoteStartBat(string exeWin)
    {
        // 启动前再清一次 Zone；用 start 拉起，不阻塞登录
        return
            "@echo off\r\n" +
            "if exist \"" + exeWin + ":Zone.Identifier\" del /f /q \"" + exeWin + ":Zone.Identifier\" >nul 2>&1\r\n" +
            "powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"Get-ChildItem 'C:\\Tools\\RemoteFix' -Recurse -Force -EA SilentlyContinue | Unblock-File -EA SilentlyContinue\" >nul 2>&1\r\n" +
            "start \"\" \"" + exeWin + "\"\r\n" +
            "exit /b 0\r\n";
    }

    private static void UnblockTree(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                // 删除 ADS：path:Zone.Identifier
                File.Delete(file + ":Zone.Identifier");
            }
            catch { }
        }
    }

    private static int PurgeRemoteExesFromStartup(string winDrive)
    {
        var n = 0;
        var dirs = new List<string>
        {
            Path.Combine(winDrive, @"ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp")
        };
        try
        {
            var users = Path.Combine(winDrive, "Users");
            if (Directory.Exists(users))
            {
                foreach (var u in Directory.EnumerateDirectories(users))
                {
                    dirs.Add(Path.Combine(u, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup"));
                }
            }
        }
        catch { }

        var patterns = new[] { "ToDesk*.exe", "*Sunlogin*.exe", "Oray*.exe", "*todesk*.exe", "Remote*.exe" };
        foreach (var dir in dirs.Where(Directory.Exists))
        {
            foreach (var pat in patterns)
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, pat))
                    {
                        try { File.Delete(f); n++; } catch { }
                    }
                }
                catch { }
            }
        }
        return n;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>当前已在 Windows 内：修复自启（清 Startup 里的 exe + 改 bat + 去 MOTW）</summary>
    public void DeployRemoteAutostartLive(string? toolDir = null)
    {
        toolDir ??= AppContext.BaseDirectory;
        var winDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))?.TrimEnd('\\')
                       ?? Environment.GetEnvironmentVariable("SystemDrive")
                       ?? "C:";

        var remoteSrc = FindRemoteFolder(toolDir);
        var dest = Path.Combine(winDrive, @"Tools\RemoteFix");
        Directory.CreateDirectory(dest);

        if (remoteSrc != null)
        {
            CopyDir(remoteSrc, dest);
            _log($"已复制远程软件到 {dest}");
        }
        else if (!Directory.EnumerateFiles(dest, "*.exe", SearchOption.AllDirectories).Any())
        {
            throw new InvalidOperationException(
                "未找到 Remote 绿色版，且 C:\\Tools\\RemoteFix 也没有 exe。请先放入 Remote\\ 再部署。");
        }
        else
        {
            _log($"使用已有目录 {dest}");
        }

        UnblockTree(dest);
        _log("已清除下载标记（Zone.Identifier）");

        var removed = PurgeRemoteExesFromStartup(winDrive);
        // 也清当前用户 Startup
        try
        {
            var userStartup = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup));
            removed += PurgeDirRemoteExes(userStartup);
        }
        catch { }
        if (removed > 0)
            _log($"已从启动文件夹移除 {removed} 个远程 exe");

        var exeRel = FindMainExe(dest);
        if (exeRel == null) throw new InvalidOperationException("找不到远程主程序 exe");
        var exeWin = Path.Combine(dest, exeRel);
        _log($"主程序: {exeWin}");

        var fixDir = Path.Combine(winDrive, @"Windows\USBFix");
        Directory.CreateDirectory(fixDir);
        var startBat = Path.Combine(fixDir, "start_remote.bat");
        File.WriteAllText(startBat, BuildRemoteStartBat(exeWin), Encoding.ASCII);

        var commonStartup = Path.Combine(winDrive, @"ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp");
        Directory.CreateDirectory(commonStartup);
        File.WriteAllText(Path.Combine(commonStartup, "USB远程自启.bat"),
            "@echo off\r\ncall \"C:\\Windows\\USBFix\\start_remote.bat\"\r\n", Encoding.ASCII);

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Attachments");
            key?.SetValue("SaveZoneInformation", 2, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch { _log("提示: 写 Attachments 策略需要管理员"); }

        try
        {
            using var run = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
            run?.SetValue("USBRemoteAutostart", @"C:\Windows\USBFix\start_remote.bat");
            run?.DeleteValue("RemoteFix", false);
        }
        catch { _log("提示: 写 Run 键需要管理员"); }

        // 立即试启一次（验证不弹窗）
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(startBat)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exeWin)!
            });
            _log("已试启远程（若仍弹窗，请对本机 ToDesk 右键 → 属性 → 解除锁定）");
        }
        catch (Exception ex)
        {
            _log("试启失败: " + ex.Message);
        }

        _log("完成。Startup 里只保留 bat，不应再弹「打开文件-安全警告」。");
    }

    private static int PurgeDirRemoteExes(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        var n = 0;
        foreach (var pat in new[] { "ToDesk*.exe", "*Sunlogin*.exe", "Oray*.exe", "*todesk*.exe" })
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, pat))
                {
                    try { File.Delete(f); n++; } catch { }
                }
            }
            catch { }
        }
        return n;
    }

    private void ApplySpectatorLogon()
    {
        RunReg(@"add ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows NT\CurrentVersion\Winlogon"" /v AutoAdminLogon /t REG_SZ /d 1 /f");
        RunReg(@"add ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows NT\CurrentVersion\Winlogon"" /v DefaultUserName /t REG_SZ /d Administrator /f");
        RunReg(@"add ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows NT\CurrentVersion\Winlogon"" /v DefaultPassword /t REG_SZ /d """" /f");
        RunReg(@"add ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows NT\CurrentVersion\Winlogon"" /v ForceAutoLogon /t REG_SZ /d 1 /f");
        RunReg(@"add ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows\CurrentVersion\Policies\System"" /v DisableCAD /t REG_DWORD /d 1 /f");
        RunReg(@"delete ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows\CurrentVersion\Policies\System"" /v legalnoticecaption /f");
        RunReg(@"delete ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows\CurrentVersion\Policies\System"" /v legalnoticetext /f");
        RunReg(@"add ""HKLM\PEOFFLINE_SOFT\Microsoft\Windows\CurrentVersion\Policies\System"" /v EnableFirstLogonAnimation /t REG_DWORD /d 0 /f");
        RunReg(@"add ""HKLM\PEOFFLINE_SOFT\Policies\Microsoft\Windows\OOBE"" /v DisablePrivacyExperience /t REG_DWORD /d 1 /f");
    }

    private static string? FindRemoteFolder(string toolDir)
    {
        var candidates = new List<string>
        {
            Path.Combine(toolDir, "Remote"),
            Path.Combine(Path.GetDirectoryName(toolDir.TrimEnd('\\')) ?? "", "Remote"),
            Path.Combine(Directory.GetCurrentDirectory(), "Remote"),
            Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", "Remote")
        };

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            candidates.Add(Path.Combine(drive.Name, "Remote"));
            candidates.Add(Path.Combine(drive.Name, "修复", "Remote"));
            candidates.Add(Path.Combine(drive.Name, "USB急救", "Remote"));
            candidates.Add(Path.Combine(drive.Name, "Tools", "Remote"));
            candidates.Add(Path.Combine(drive.Name, "ToDesk"));
            candidates.Add(Path.Combine(drive.Name, "向日葵"));
        }

        foreach (var p in candidates.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.EnumerateFiles(p, "*.exe", SearchOption.AllDirectories).Any())
                return p;
        }

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && (d.DriveType == DriveType.Removable || d.DriveType == DriveType.Fixed)))
        {
            try
            {
                foreach (var exeName in new[] { "ToDesk_Lite.exe", "ToDesk.exe", "SunloginClient.exe" })
                {
                    var hit = Directory.EnumerateFiles(drive.Name, exeName, SearchOption.TopDirectoryOnly).FirstOrDefault();
                    if (hit != null) return Path.GetDirectoryName(hit);
                    foreach (var sub in Directory.EnumerateDirectories(drive.Name))
                    {
                        hit = Directory.EnumerateFiles(sub, exeName, SearchOption.TopDirectoryOnly).FirstOrDefault();
                        if (hit != null) return Path.GetDirectoryName(hit);
                    }
                }
            }
            catch { }
        }

        return null;
    }

    private static string? FindMainExe(string dest)
    {
        if (!Directory.Exists(dest)) return null;
        foreach (var name in new[]
                 {
                     "ToDesk_Lite.exe", "ToDesk.exe", "SunloginClient.exe", "Sunlogin.exe", "OrayPortal.exe"
                 })
        {
            var hit = Directory.EnumerateFiles(dest, name, SearchOption.AllDirectories).FirstOrDefault();
            if (hit != null) return Path.GetRelativePath(dest, hit);
        }
        return Directory.EnumerateFiles(dest, "*.exe", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dest, f))
            .FirstOrDefault();
    }

    private static void CopyDir(string src, string dst)
    {
        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dst, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private void LoadSoft(string winDrive)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("reg",
            $"load \"HKLM\\PEOFFLINE_SOFT\" \"{Path.Combine(winDrive, @"Windows\System32\config\SOFTWARE")}\"")
        { UseShellExecute = false, CreateNoWindow = true };
        using var p = System.Diagnostics.Process.Start(psi);
        p?.WaitForExit();
        if (p?.ExitCode != 0) throw new InvalidOperationException("无法加载 SOFTWARE（可能被占用，请重启 PE 后立刻再试）");
    }

    private static void Unload(string name)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("reg", $"unload \"HKLM\\{name}\"")
        { UseShellExecute = false, CreateNoWindow = true };
        using var p = System.Diagnostics.Process.Start(psi);
        p?.WaitForExit();
    }

    private static void RunReg(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("reg", args)
        { UseShellExecute = false, CreateNoWindow = true };
        using var p = System.Diagnostics.Process.Start(psi);
        p?.WaitForExit();
    }
}
