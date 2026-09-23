using System.Collections.Concurrent;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace USBFixTool;

public sealed class LocalApiServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly RepairEngine _engine;
    private readonly ConcurrentQueue<string> _logs = new();
    private readonly List<HttpListenerResponse> _sseClients = new();
    private readonly object _sseLock = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _busy;

    public int Port { get; } = 17890;
    public string BaseUrl => $"http://127.0.0.1:{Port}/";

    public LocalApiServer()
    {
        _engine = new RepairEngine(BroadcastLog);
        _listener.Prefixes.Add(BaseUrl);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _loop = Task.Run(() => AcceptLoop(_cts.Token));
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().WaitAsync(ct); }
            catch { break; }

            _ = Task.Run(() => Handle(ctx), ct);
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            res.Headers.Add("Access-Control-Allow-Origin", "*");
            res.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            res.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 204;
                res.Close();
                return;
            }

            var path = req.Url?.AbsolutePath ?? "/";

            if (path == "/api/status" && req.HttpMethod == "GET")
            {
                await WriteJson(res, new
                {
                    isPe = RepairEngine.IsPeEnvironment(),
                    drive = RepairEngine.FindWindowsDrive(),
                    isAdmin = IsAdmin()
                });
                return;
            }

            if (path == "/api/logs" && req.HttpMethod == "GET")
            {
                await HandleSse(res);
                return;
            }

            if (path == "/api/action" && req.HttpMethod == "POST")
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var action = doc.RootElement.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";

                if (_busy)
                {
                    await WriteJson(res, new { ok = false, message = "已有任务在运行" }, 409);
                    return;
                }

                try
                {
                    _busy = true;
                    BroadcastLog($"开始时间  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    BroadcastLog("");
                    await RunAction(action);
                    BroadcastLog("");
                    BroadcastLog(action == "check"
                        ? "检查完成（未修改系统）。"
                        : "完成 — 请重启电脑测试键鼠。");
                    await WriteJson(res, new { ok = true, message = "完成" });
                }
                catch (Exception ex)
                {
                    BroadcastLog($"错误  {ex.Message}");
                    await WriteJson(res, new { ok = false, message = ex.Message }, 500);
                }
                finally
                {
                    _busy = false;
                }
                return;
            }

            // Static files from wwwroot
            await ServeStatic(res, path);
        }
        catch (Exception ex)
        {
            try
            {
                res.StatusCode = 500;
                var bytes = Encoding.UTF8.GetBytes(ex.Message);
                await res.OutputStream.WriteAsync(bytes);
                res.Close();
            }
            catch { }
        }
    }

    private async Task RunAction(string action)
    {
        var ct = CancellationToken.None;
        switch (action)
        {
            case "check":
                await _engine.RunCheckOnlyAsync(ct);
                break;
            case "full":
                await _engine.RunFullPeRepairAsync(RequireDrive(), ct);
                break;
            case "usb":
            case "usbdk":
                await _engine.RunPeUsbFixAsync(RequireDrive(), ct);
                break;
            case "account":
                await _engine.RunAccountUnlockAsync(RequireDrive(), ct);
                break;
            case "deploy":
                await _engine.RunDeployBootCheckAsync(RequireDrive(), ct);
                break;
            case "remote":
                // PE 需要系统盘；Windows 内可直接修当前机（传占位盘符即可）
                if (RepairEngine.IsPeEnvironment())
                    _engine.RunRemoteDeploy(RequireDrive());
                else
                    _engine.RunRemoteDeploy(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:");
                break;
            case "drivers":
                await _engine.RunRemoveDriversAsync(RequireDrive(), ct);
                break;
            case "winFix":
                await _engine.RunWinUsbFixAsync(ct);
                break;
            case "uninstall":
                _engine.UninstallBootCheck();
                break;
            case "openLog":
            {
                var drive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
                var report = Path.Combine(drive, "usb_fix_report.txt");
                var check = Path.Combine(drive, "usb_check_report.txt");
                var log = Path.Combine(drive, "usb_fix_log.txt");

                // 旧版极简「USB 控制器: 异常」会一直误导人 → 打开前先改写成说明
                RepairEngine.RewriteObsoleteFixReportIfNeeded(report, check);

                // 只打开「给人看的」报告：优先完整检查；修复报告仅在新格式时打开
                string? target = null;
                if (File.Exists(check)) target = check;
                else if (File.Exists(report) && RepairEngine.IsHumanReadableReport(report)) target = report;
                else if (File.Exists(log)) target = log;

                if (target == null)
                    throw new FileNotFoundException("还没有报告。请先点「仅检查问题（完整）」生成白话报告。");

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
                BroadcastLog($"已打开: {target}");
                break;
            }
            default:
                throw new InvalidOperationException($"未知操作: {action}");
        }
    }

    private static string RequireDrive()
    {
        var d = RepairEngine.FindWindowsDrive();
        if (d == null) throw new InvalidOperationException("未找到 Windows 系统盘");
        return d;
    }

    private async Task HandleSse(HttpListenerResponse res)
    {
        res.ContentType = "text/event-stream";
        res.Headers.Add("Cache-Control", "no-cache");
        res.Headers.Add("Connection", "keep-alive");
        res.StatusCode = 200;

        lock (_sseLock) _sseClients.Add(res);

        // Replay recent logs
        foreach (var line in _logs)
            await WriteSse(res, line);

        try
        {
            // Keep connection open until client disconnects
            while (res.OutputStream.CanWrite)
                await Task.Delay(15000);
        }
        catch { }
        finally
        {
            lock (_sseLock) _sseClients.Remove(res);
            try { res.Close(); } catch { }
        }
    }

    private void BroadcastLog(string msg)
    {
        _logs.Enqueue(msg);
        while (_logs.Count > 500 && _logs.TryDequeue(out _)) { }

        List<HttpListenerResponse> clients;
        lock (_sseLock) clients = _sseClients.ToList();

        foreach (var c in clients)
        {
            try { WriteSse(c, msg).GetAwaiter().GetResult(); }
            catch
            {
                lock (_sseLock) _sseClients.Remove(c);
            }
        }
    }

    private static async Task WriteSse(HttpListenerResponse res, string msg)
    {
        var data = $"data: {msg.Replace("\r", "").Replace("\n", " ")}\n\n";
        var bytes = Encoding.UTF8.GetBytes(data);
        await res.OutputStream.WriteAsync(bytes);
        await res.OutputStream.FlushAsync();
    }

    private async Task ServeStatic(HttpListenerResponse res, string path)
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var www = Path.Combine(exeDir, "wwwroot");
        if (!Directory.Exists(www))
            www = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!Directory.Exists(www))
            www = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "wwwroot"));

        if (path == "/" || string.IsNullOrEmpty(path))
            path = "/index.html";

        var file = Path.GetFullPath(Path.Combine(www, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        if (!file.StartsWith(Path.GetFullPath(www), StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
        {
            // SPA fallback
            file = Path.Combine(www, "index.html");
            if (!File.Exists(file))
            {
                res.StatusCode = 404;
                var msg = Encoding.UTF8.GetBytes("wwwroot not found. Run: cd web && npm run build");
                await res.OutputStream.WriteAsync(msg);
                res.Close();
                return;
            }
        }

        res.ContentType = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".json" => "application/json",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream"
        };
        var bytes = await File.ReadAllBytesAsync(file);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    private static async Task WriteJson(HttpListenerResponse res, object obj, int status = 200)
    {
        var json = JsonSerializer.Serialize(obj);
        var bytes = Encoding.UTF8.GetBytes(json);
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    private static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener.Stop(); } catch { }
        _listener.Close();
    }
}
