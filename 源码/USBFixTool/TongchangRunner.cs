using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace USBFixTool;

/// <summary>
/// 调用捆绑的「畅通匣」Python 脚本（安全网络/占用/共享），不碰 USB 设备删除。
/// </summary>
public sealed class TongchangRunner
{
    private readonly Action<string> _log;

    public TongchangRunner(Action<string> log) => _log = log;

    public async Task<JsonElement> RunAsync(string cmd, Dictionary<string, object?>? extra = null, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?> { ["cmd"] = cmd };
        if (extra != null)
        {
            foreach (var kv in extra)
                payload[kv.Key] = kv.Value;
        }

        var json = JsonSerializer.Serialize(payload);
        var (exe, argsPrefix, workDir) = ResolveInvoker();
        _log($"畅通匣 · {cmd}");

        var tmp = Path.Combine(Path.GetTempPath(), $"tc_{Guid.NewGuid():N}.json");
        // .NET Encoding.UTF8 默认带 BOM，Python json.loads(utf-8) 会炸；写无 BOM
        await File.WriteAllTextAsync(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"{argsPrefix}\"--json-file\" \"{tmp}\"",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            // 实时刷 stderr 进度，避免要等整段跑完才有日志
            psi.Environment["PYTHONUNBUFFERED"] = "1";
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUTF8"] = "1";

            using var proc = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var sawProgress = 0;
            long lastProgressAt = Environment.TickCount64;

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) stdout.AppendLine(e.Data);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                stderr.AppendLine(e.Data);
                // Python _progress → ":: 文本"
                if (e.Data.StartsWith(":: ", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref sawProgress);
                    Interlocked.Exchange(ref lastProgressAt, Environment.TickCount64);
                    _log(e.Data[3..]);
                }
            };

            if (!proc.Start())
                throw new InvalidOperationException("无法启动畅通匣后端");

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // 总时限：有线硬复位+协议栈可能超过 3 分钟
            const int overallSeconds = 300;
            using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            overallCts.CancelAfter(TimeSpan.FromSeconds(overallSeconds));

            // 超过 25s 没新进度才写一次心跳（少刷）
            var heartbeat = Task.Run(async () =>
            {
                var n = 0;
                while (!proc.HasExited)
                {
                    try { await Task.Delay(25000, overallCts.Token); }
                    catch (OperationCanceledException) { return; }
                    if (proc.HasExited) return;
                    n++;
                    var idle = Environment.TickCount64 - Volatile.Read(ref lastProgressAt);
                    if (idle >= 25000)
                        _log($"…当前子步骤仍在跑（已约 {n * 25}s）");
                }
            }, overallCts.Token);

            try
            {
                await proc.WaitForExitAsync(overallCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log($"错误: 畅通匣超时（>{overallSeconds}s），正在强制结束…");
                try
                {
                    using var kill = Process.Start(new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = $"/F /T /PID {proc.Id}",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    kill?.WaitForExit(8000);
                }
                catch { /* ignore */ }
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                throw new InvalidOperationException($"畅通匣执行超时（>{overallSeconds} 秒）。请重试；若反复超时，可改点单项修复。");
            }
            try { await heartbeat; } catch { /* ignore */ }

            var text = stdout.ToString().Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                var err = stderr.ToString().Trim();
                // 去掉已刷过的进度行，只留真正错误
                var errLines = err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(l => !l.StartsWith(":: ", StringComparison.Ordinal))
                    .ToArray();
                var errClean = string.Join("\n", errLines);
                throw new InvalidOperationException(string.IsNullOrEmpty(errClean)
                    ? $"畅通匣无输出 (exit {proc.ExitCode})"
                    : errClean);
            }

            var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Last();
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement.Clone();

            // 已实时刷过进度则不再把 steps 整段重打；只补建议/错误
            if (sawProgress == 0 && root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in steps.EnumerateArray())
                {
                    var t = s.GetString();
                    if (!string.IsNullOrWhiteSpace(t)) _log(t!);
                }
            }

            if (root.TryGetProperty("suggestions", out var sug) && sug.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in sug.EnumerateArray())
                {
                    var t = s.GetString();
                    if (!string.IsNullOrWhiteSpace(t)) _log("建议: " + t);
                }
            }
            if (root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in msgs.EnumerateArray())
                {
                    var t = s.GetString();
                    if (!string.IsNullOrWhiteSpace(t)) _log(t!);
                }
            }
            if (root.TryGetProperty("note", out var note))
            {
                var t = note.GetString();
                if (!string.IsNullOrWhiteSpace(t)) _log(t!);
            }
            if (root.TryGetProperty("error", out var errProp))
            {
                var t = errProp.GetString();
                if (!string.IsNullOrWhiteSpace(t)) _log("错误: " + t);
            }

            return root;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private (string exe, string argsPrefix, string workDir) ResolveInvoker()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var candidates = new[]
        {
            Path.Combine(baseDir, "畅通匣", "unlock_folder.py"),
            Path.Combine(baseDir, "lib", "畅通匣", "unlock_folder.py"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "畅通匣", "unlock_folder.py")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "畅通匣", "unlock_folder.py")),
        };

        string? script = candidates.FirstOrDefault(File.Exists);
        if (script == null)
        {
            var dev = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "畅通匣", "unlock_folder.py"));
            if (File.Exists(dev)) script = dev;
        }

        var py = FindPython();
        if (script != null && py != null)
            return (py, $"-X utf8 \"{script}\" ", Path.GetDirectoryName(script)!);

        var exeCandidates = new[]
        {
            Path.Combine(baseDir, "畅通匣.exe"),
            Path.Combine(baseDir, "畅通匣", "畅通匣.exe"),
        };
        var frozen = exeCandidates.FirstOrDefault(File.Exists);
        if (frozen != null)
            return (frozen, "", Path.GetDirectoryName(frozen)!);

        throw new FileNotFoundException(
            "未找到畅通匣后端。请确认 发布/畅通匣/unlock_folder.py 存在，并安装 Python 3；或放入 畅通匣.exe。");
    }

    private static string? FindPython()
    {
        foreach (var name in new[] { "python", "py" })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = name,
                    Arguments = name == "py" ? "-3 -c \"import sys; print(sys.executable)\"" : "-c \"import sys; print(sys.executable)\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) continue;
                var path = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(5000);
                if (p.ExitCode == 0 && File.Exists(path)) return path;
            }
            catch { }
        }

        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Python", "Python312", "python.exe");
        return File.Exists(local) ? local : null;
    }
}
