// ============================================================================
// quickcmd-host — WebConsole.cs: 本地 Web 控制台（127.0.0.1）
//   qchost 启动时顺手起一个只读+启停的 HTTP 服务：
//     GET  /                单页控制台（命令 / 实例 / 历史）
//     GET  /api/state       一次返回命令+实例+历史快照
//     POST /api/run {key}   启动注册命令（与 IPC run 同一套逻辑）
//     POST /api/kill {key}  结束实例
//   安全：仅绑 loopback；校验 Host（防 DNS rebinding）与 Origin；写操作要求
//   X-QC-Token（token 每次宿主启动随机生成，注入页面）。
// ============================================================================

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace QuickCmd;

internal static class WebConsole
{
    private static TcpListener _listener;
    private static int _port;
    private static readonly DateTime StartedAt = DateTime.Now;
    private static readonly string Token = Guid.NewGuid().ToString("N");

    public static string Url => _port > 0 ? "http://127.0.0.1:" + _port + "/" : null;

    // 首选端口写在 settings.json；被占用时从首选起顺延（最多 20 个）并回写
    public static void Start()
    {
        var preferred = Store.LoadUiPort();
        for (int i = 0; i < 20; i++)
        {
            var port = preferred + i;
            if (port > 65000) break;
            var l = new TcpListener(IPAddress.Loopback, port);
            try { l.Start(); }
            catch { continue; }

            _listener = l;
            _port = port;
            if (port != preferred) Store.SaveUiPort(port);

            var t = new Thread(AcceptLoop) { IsBackground = true, Name = "quickcmd-webui" };
            t.Start();
            return;
        }
    }

    private static void AcceptLoop()
    {
        while (true)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch { break; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Handle(client); }
                catch { }
                finally { try { client.Close(); } catch { } }
            });
        }
    }

    private static void Handle(TcpClient client)
    {
        using var ns = client.GetStream();
        var (head, body) = ReadRequest(ns);
        if (head == null) return;

        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 2) { WriteText(ns, 400, "bad request"); return; }

        var method = parts[0].ToUpperInvariant();
        var target = parts[1];
        var q = target.IndexOf('?');
        var path = q >= 0 ? target.Substring(0, q) : target;
        var query = ParseQuery(q >= 0 ? target.Substring(q + 1) : "");
        var headers = ParseHeaders(lines);

        // DNS rebinding：只接受 loopback 的 Host
        if (headers.TryGetValue("host", out var host) && !HostAllowed(host))
        {
            WriteText(ns, 403, "host not allowed");
            return;
        }

        if (method == "GET" && (path == "/" || path == "/index.html"))
        {
            WriteBytes(ns, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(
                LoadHtml().Replace("__QC_TOKEN__", Token)
                    .Replace("__QC_PORT__", _port.ToString())));
            return;
        }

        if (method == "GET" && path == "/api/state")
        {
            WriteJson(ns, 200, BuildState());
            return;
        }

        // 输出日志（只读）：key = 命令 short-id / 实例 ID / 日志文件名
        if (method == "GET" && path == "/api/log")
        {
            query.TryGetValue("key", out var lkey);
            var tail = 300;
            if (query.TryGetValue("tail", out var t) && int.TryParse(t, out var tn)) tail = Math.Clamp(tn, 1, 5000);

            var file = LogStore.Resolve(lkey, out var why);
            if (file == null) { WriteText(ns, 404, why ?? "没有日志"); return; }

            var text = LogStore.Tail(file.Path, tail);
            var banner = "# " + file.Name + "  (" + file.SizeText + ", " + file.Modified.ToString("yyyy-MM-dd HH:mm:ss") + ")\n";
            WriteBytes(ns, 200, "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes(banner + "\n" + text + "\n"));
            return;
        }

        if (method == "GET" && path == "/favicon.ico")
        {
            WriteBytes(ns, 204, null, Array.Empty<byte>());
            return;
        }

        // 所有写操作：同一个 token + Origin 校验
        if (method == "POST" && path.StartsWith("/api/", StringComparison.Ordinal))
        {
            if (!headers.TryGetValue("x-qc-token", out var tok) || tok != Token)
            {
                WriteJson(ns, 403, new { ok = false, message = "token 无效（刷新页面后重试）" });
                return;
            }
            if (!OriginAllowed(headers))
            {
                WriteJson(ns, 403, new { ok = false, message = "origin 不允许" });
                return;
            }

            string key = null;
            bool pinned = true;
            try
            {
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(body));
                key = doc.RootElement.GetProperty("key").GetString();
                if (doc.RootElement.TryGetProperty("pinned", out var pv))
                    pinned = pv.ValueKind != JsonValueKind.False;
            }
            catch { }
            if (string.IsNullOrWhiteSpace(key))
            {
                WriteJson(ns, 400, new { ok = false, message = "请求体需要 {\"key\":\"...\"}" });
                return;
            }

            // 首页固定：key 接受 short-id / 完整 id / title，落到 shortId 存储
            if (path == "/api/pin")
            {
                var list = Store.Load(out _);
                var cmd = Resolver.Resolve(key, list, out var perr);
                if (cmd == null)
                {
                    WriteJson(ns, 404, new { ok = false, message = perr });
                    return;
                }
                var after = Store.SetPinned(cmd.ShortId, pinned);
                WriteJson(ns, 200, new { ok = true, message = (pinned ? "已固定：" : "已取消固定：") + cmd.Title, pinned = after });
                return;
            }

            if (path != "/api/run" && path != "/api/kill" && path != "/api/front")
            {
                WriteText(ns, 404, "not found");
                return;
            }

            var op = path switch
            {
                "/api/run" => "run",
                "/api/kill" => "kill",
                _ => "front",
            };
            var resp = IpcServer.HandleMessage(new IpcMessage
            {
                Op = op,
                Key = key,
                Source = "webui",
                RequesterPid = 0,
            }, out _);

            WriteJson(ns, resp.Ok ? 200 : 400, new { ok = resp.Ok, message = resp.Message });
            return;
        }

        WriteText(ns, 404, "not found");
    }

    private static object BuildState()
    {
        var cmds = Store.Load(out var badLines);
        var snap = Manager.Snapshot();

        var byShort = new Dictionary<string, InstanceInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in snap)
            if (!string.IsNullOrEmpty(s.ShortId) && !byShort.ContainsKey(s.ShortId))
                byShort[s.ShortId] = s;

        // 每个命令最近一次日志（cmdlogs：prefix → 最新文件），没有则 null
        var latestLog = new Dictionary<string, LogFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in LogStore.List(0))
        {
            var dash = f.Name.IndexOf('-');
            if (dash <= 0) continue;
            var prefix = f.Name.Substring(0, dash);
            if (!latestLog.ContainsKey(prefix)) latestLog[prefix] = f; // 列表已按时间新→旧
        }

        var commands = cmds.Select(c =>
        {
            byShort.TryGetValue(c.ShortId, out var inst);
            LogFile lf = null;
            if (c.CaptureLogs) latestLog.TryGetValue(LogStore.Key(c), out lf);
            return new
            {
                id = c.Id,
                shortId = c.ShortId,
                title = c.Title,
                proj = c.Proj,
                type = c.IsWeb ? "web" : "app",
                url = c.Url,
                autoStart = c.AutoStart,
                captureLogs = c.CaptureLogs,
                log = lf == null ? null : lf.Name,
                state = inst != null ? "running" : "stopped",
                instanceId = inst?.Id,
                pid = inst?.Pid ?? 0,
                started = inst?.Started,
                kindText = c.KindText,
                commandLine = History.CommandLineOf(c),
                program = c.Program,
                args = c.Args,
                shellScript = c.ShellScript,
                interpreter = c.Interpreter,
                env = c.Env,
                workdir = c.Workdir,
                effectiveWorkdir = Launcher.EffectiveWorkdir(c),
                maxInstances = c.MaxInstances,
                keepOpen = c.KeepOpen,
                createdAt = c.CreatedAt,
                updatedAt = c.UpdatedAt,
            };
        }).ToList();

        var history = History.Read(60, null, out _);
        // 日志文件可能已被清理（或换机复制），不存在的就不给按钮
        foreach (var h in history)
            if (!LogStore.Exists(h.LogPath)) h.LogPath = "";
        history.Reverse(); // 新的在前

        return new
        {
            host = new
            {
                version = typeof(WebConsole).Assembly.GetName().Version?.ToString() ?? "dev",
                port = _port,
                uptimeSeconds = (long)(DateTime.Now - StartedAt).TotalSeconds,
                badLines,
                pinned = Store.LoadSettings().Pinned,
            },
            commands,
            instances = snap,
            history,
        };
    }

    private static bool HostAllowed(string host)
    {
        host = host.Trim().ToLowerInvariant();
        return host == "127.0.0.1:" + _port || host == "localhost:" + _port
            || host == "127.0.0.1" || host == "localhost";
    }

    private static bool OriginAllowed(Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("origin", out var o) || string.IsNullOrWhiteSpace(o))
            return true; // curl / 本地脚本没有 Origin
        return o == "http://127.0.0.1:" + _port || o == "http://localhost:" + _port;
    }

    private static Dictionary<string, string> ParseQuery(string qs)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(qs)) return d;
        foreach (var part in qs.Split('&'))
        {
            if (part.Length == 0) continue;
            var i = part.IndexOf('=');
            var k = i >= 0 ? part.Substring(0, i) : part;
            var v = i >= 0 ? part.Substring(i + 1) : "";
            try { d[Uri.UnescapeDataString(k)] = Uri.UnescapeDataString(v.Replace('+', ' ')); }
            catch { d[k] = v; }
        }
        return d;
    }

    private static Dictionary<string, string> ParseHeaders(string[] lines)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) break;
            var c = line.IndexOf(':');
            if (c > 0) d[line.Substring(0, c).Trim()] = line.Substring(c + 1).Trim();
        }
        return d;
    }

    // 读请求头（到 \r\n\r\n），返回已读到的 body 片段；再按 Content-Length 补齐
    private static (string head, byte[] body) ReadRequest(NetworkStream ns)
    {
        var ms = new MemoryStream();
        var buf = new byte[4096];
        var marker = new byte[] { 13, 10, 13, 10 };
        int delim = -1;

        while (ms.Length < 256 * 1024)
        {
            int n;
            try { n = ns.Read(buf, 0, buf.Length); }
            catch { return (null, Array.Empty<byte>()); }
            if (n <= 0) break;
            ms.Write(buf, 0, n);
            var arr = ms.GetBuffer();
            delim = IndexOf(arr, (int)ms.Length, marker);
            if (delim >= 0) break;
        }
        if (delim < 0) return (null, Array.Empty<byte>());

        var all = ms.GetBuffer();
        var head = Encoding.ASCII.GetString(all, 0, delim + marker.Length);

        var contentLength = 0;
        foreach (var line in head.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(line.Substring(15).Trim(), out contentLength);
                break;
            }
        }
        contentLength = Math.Clamp(contentLength, 0, 64 * 1024);

        var bodyStart = delim + marker.Length;
        var available = (int)ms.Length - bodyStart;
        var body = new byte[contentLength];
        var total = Math.Clamp(available, 0, contentLength);
        if (total > 0) Array.Copy(all, bodyStart, body, 0, total);

        while (total < contentLength)
        {
            int n;
            try { n = ns.Read(body, total, contentLength - total); }
            catch { break; }
            if (n <= 0) break;
            total += n;
        }
        return (head, body);
    }

    private static int IndexOf(byte[] hay, int len, byte[] needle)
    {
        for (int i = 0; i <= len - needle.Length; i++)
        {
            int j = 0;
            for (; j < needle.Length; j++)
                if (hay[i + j] != needle[j]) break;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    private static void WriteJson(NetworkStream ns, int status, object body) =>
        WriteBytes(ns, status, "application/json; charset=utf-8",
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, Store.JsonOpts)));

    private static void WriteText(NetworkStream ns, int status, string text) =>
        WriteBytes(ns, status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text + "\n"));

    private static void WriteBytes(NetworkStream ns, int status, string contentType, byte[] body)
    {
        var reason = status switch
        {
            200 => "OK",
            204 => "No Content",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            _ => "",
        };
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        sb.Append("Date: ").Append(DateTime.UtcNow.ToString("R")).Append("\r\n");
        sb.Append("Server: quickcmd-ui\r\n");
        if (contentType != null) sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        sb.Append("Cache-Control: no-store\r\n");
        sb.Append("X-Content-Type-Options: nosniff\r\n");
        sb.Append("Connection: close\r\n\r\n");

        var head = Encoding.ASCII.GetBytes(sb.ToString());
        try
        {
            ns.Write(head, 0, head.Length);
            if (body.Length > 0) ns.Write(body, 0, body.Length);
            ns.Flush();
        }
        catch { }
    }

    // ------------------------------------------------------------------------
    // 单页控制台（web/index.html；token / 端口在响应时注入）
    // ------------------------------------------------------------------------
    // 页面放在 web/index.html（随输出复制）：每次请求都读磁盘，改完刷新即可，不用重启宿主。
    // 文件缺失时退回编译进程序集的内嵌副本。
    private static string LoadHtml()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "web", "index.html");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        catch { }

        using var s = typeof(WebConsole).Assembly.GetManifestResourceStream("QuickCmd.WebConsole.html");
        if (s == null) return "<!doctype html><meta charset=\"utf-8\"><h1>web/index.html 缺失</h1>";
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }
}
