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
        await File.WriteAllTextAsync(tmp, json, Encoding.UTF8, ct);
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

            using var proc = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            if (!proc.Start())
                throw new InvalidOperationException("无法启动畅通匣后端");

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(ct);

            var text = stdout.ToString().Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                var err = stderr.ToString().Trim();
                throw new InvalidOperationException(string.IsNullOrEmpty(err)
                    ? $"畅通匣无输出 (exit {proc.ExitCode})"
                    : err);
            }

            var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Last();
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement.Clone();

            if (root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
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
