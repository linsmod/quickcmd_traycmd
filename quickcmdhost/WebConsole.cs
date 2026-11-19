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
                Html.Replace("__QC_TOKEN__", Token)
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
    // 单页控制台（内嵌；token / 端口在响应时注入）
    // ------------------------------------------------------------------------
    private const string Html = """
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>quickcmd 控制台</title>
<style>
:root{color-scheme:dark}
*{box-sizing:border-box}
body{margin:0;font:14px/1.45 "Segoe UI",system-ui,sans-serif;background:#1b1b1f;color:#e8e8ea;height:100vh;display:flex;flex-direction:column;overflow:hidden}
button{background:#2b2b33;color:#e8e8ea;border:1px solid #3a3a44;border-radius:6px;padding:5px 10px;cursor:pointer;font-size:12px;font-family:inherit}
button:hover{background:#35353f}
button:disabled{opacity:.45;cursor:default}
button:disabled:hover{background:#2b2b33}
button.primary{background:#22384d;border-color:#33536f;color:#a9c7e4}
button.primary:hover{background:#294458}
button.danger{background:#332529;border-color:#4e343b;color:#cf9ba4}
button.danger:hover{background:#3d2c31}
button.ghost{background:transparent;border-color:transparent;color:#a9a9b4}
button.ghost:hover{background:#2b2b33}

header{display:flex;justify-content:space-between;align-items:center;gap:12px;padding:12px 20px;border-bottom:1px solid #2e2e35;flex:0 0 auto;min-width:0}
.brand{font-weight:600;font-size:16px;white-space:nowrap}
.brand .sub{color:#8b8b95;font-weight:400;margin-left:6px;font-size:13px}
.meta{display:flex;gap:8px;align-items:center;color:#8b8b95;font-size:12px;min-width:0;overflow:hidden}
.meta #host{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.kbd{opacity:.7}

.layout{flex:1;display:flex;min-height:0;min-width:0;position:relative}
.left{display:flex;min-height:0;flex:0 0 auto}
.right{flex:1;min-width:0;display:flex;position:relative;min-height:0;border-left:1px solid #2e2e35}
.right-empty{display:none;flex:1;align-items:center;justify-content:center;color:#75777f;font-size:13px}
body:not([data-route="app"]) .right-empty{display:flex}
.re-inner{text-align:center}
.re-t{font-size:13px;color:#a9a9b4}
.re-s{font-size:11.5px;color:#75777f;margin-top:4px}

/* Launcher */
.launcher-wrap{flex:0 0 clamp(300px, 29vw, 462px);min-width:0;position:relative;display:flex;flex-direction:column}
/* 宽屏：左半固定（项目栏 + Launcher），右半放 记录 + 日志 两屏 */
body[data-w="wide"] .app{flex:1;min-width:0}
body[data-w="wide"] .pane{flex:0 0 340px;border-left:1px solid #2e2e35}

/* 中等：右半只放记录；日志覆盖右半 */
body[data-w="mid"] .app{flex:1;min-width:0}
body[data-w="mid"] .pane{position:absolute;inset:0;z-index:5;border-left:0}

/* 窄屏：只留栈顶，整屏覆盖 */
body[data-rail="0"] .side{display:none}   /* 放不下项目栏时隐藏 #2，用底部指示器切项目 */
body[data-w="narrow"]:not([data-route="app"]) .right{display:none}
body[data-w="narrow"][data-route="app"] .left{display:none}
body[data-w="narrow"] .right-empty{display:none}
body[data-w="narrow"] .left{flex:1}
body[data-w="narrow"] .launcher-wrap{flex:1}
body[data-w="narrow"] .right{position:absolute;inset:0;z-index:5;border-left:0}
body[data-w="narrow"] .app,
body[data-w="narrow"] .pane{position:absolute;inset:0}
body[data-w="narrow"] .pane{position:absolute;inset:0;z-index:8}

.side{width:216px;flex:0 0 216px;border-right:1px solid #2e2e35;padding:10px 8px;overflow:auto}
.side-head{color:#8b8b95;font-size:11px;letter-spacing:.04em;padding:4px 10px 8px}
.pitem{display:flex;align-items:center;gap:8px;padding:6px 10px;border-radius:6px;cursor:pointer;font-size:13px;color:#c4c4ce}
.pitem:hover{background:#212127}
.pitem.on{background:#2f6fd0;color:#fff}
.pitem .pname{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.pitem .cnt{font-size:11px;color:#8b8b95;font-variant-numeric:tabular-nums}
.pitem.on .cnt{color:#dce8ff}
.dot{display:inline-block;width:7px;height:7px;border-radius:50%;flex:0 0 auto}
.dot.up{background:#3fbf6f}
.dot.down{background:#6b6b76}
.dot.fail{background:#cf5b6b}
.psep{height:1px;background:#2e2e35;margin:8px 10px}

.launcher{flex:1;min-height:0;overflow:auto;padding:22px 24px 62px}
/* 滚动条不占位：桌面用滚轮/触控板，视觉上按 overlay 处理 */
.launcher,.app-body,.pane-body,.side,.modal-body,.search-results{scrollbar-width:none;-ms-overflow-style:none}
.launcher::-webkit-scrollbar,.app-body::-webkit-scrollbar,
.pane-body::-webkit-scrollbar,.side::-webkit-scrollbar,.modal-body::-webkit-scrollbar,
.search-results::-webkit-scrollbar{width:0;height:0}
.home{display:grid;grid-template-columns:repeat(auto-fill,minmax(120px,1fr));gap:18px 12px}

.dots{position:absolute;left:0;right:0;bottom:14px;display:flex;justify-content:center;align-items:center;gap:7px;pointer-events:auto}
.dots i{width:7px;height:7px;border-radius:50%;background:#3a3a44;cursor:pointer;transition:all .15s}
.dots i:hover{background:#55555f}
.dots i.on{width:20px;border-radius:4px;background:#8b8b95}
.dots.hide{display:none}
/* Launcher 顶部搜索触发：点开「全部」搜索浮层 */
.lsearch{display:flex;align-items:center;gap:8px;margin:12px 24px 0;padding:8px 12px;
  background:#212127;border:1px solid #2e2e35;border-radius:10px;color:#75777f;text-align:left;
  font:13px/1.4 inherit;font-family:inherit;cursor:text}
.lsearch:hover{background:#26262e;border-color:#3a3a44}
.lsearch .ico{opacity:.65;font-size:12px}
.lsearch .ph{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.lsearch .kbd{font-size:11px;opacity:.6}
/* 「全部」= 搜索浮层：盖住 Launcher，不改变右侧内容 */
.searchpage{display:none;position:absolute;inset:0;z-index:12;flex-direction:column;background:#1b1b1f}
body.search-open .searchpage{display:flex}
.search-head{display:flex;align-items:center;gap:8px;margin:12px 24px 0;padding:8px 12px;
  background:#212127;border:1px solid #3a3a44;border-radius:10px;flex:0 0 auto}
.search-head .ico{opacity:.65;font-size:12px}
.search-head input{flex:1;min-width:0;background:transparent;border:0;outline:none;color:#e8e8ea;
  font:13px/1.4 inherit;font-family:inherit}
.search-head input::placeholder{color:#75777f}
.search-head button{width:24px;height:24px;padding:0;line-height:20px;border-radius:6px;flex:0 0 auto}
.search-results{flex:1;min-height:0;overflow:auto;padding:18px 24px 24px}
.search-foot{padding:8px 24px;border-top:1px solid #2e2e35;color:#75777f;font-size:11px;flex:0 0 auto}
.search-empty{padding:40px 20px;text-align:center;color:#75777f;font-size:12.5px;line-height:1.7}
.tile.on{background:#232a36;border-color:#3d4f63}
.tile{position:relative;display:flex;flex-direction:column;align-items:center;gap:7px;padding:14px 8px 12px;
  border-radius:12px;border:1px solid transparent;cursor:pointer;user-select:none}
.tile:hover{background:#212127;border-color:#2e2e35}
.tile .ico{position:relative;width:56px;height:56px;border-radius:15px;display:flex;align-items:center;justify-content:center;
  font-size:21px;font-weight:600;color:#fff;box-shadow:0 2px 8px rgba(0,0,0,.35)}
.tile .ico .dot{position:absolute;right:-3px;top:-3px;width:12px;height:12px;border:2px solid #1b1b1f}
.tile .tname{font-size:12.5px;color:#d8d8e0;text-align:center;line-height:1.3;max-width:112px;
  overflow:hidden;display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;word-break:break-all}
.tile .tproj{font-size:10.5px;color:#75777f;max-width:112px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}

/* App（栈第 2 层） */
.app{display:none;flex-direction:column;min-width:0;min-height:0;background:#1b1b1f}
body[data-route="app"] .app{display:flex}

.app-head{display:flex;align-items:center;gap:10px;padding:0 16px;height:52px;min-height:52px;
  border-bottom:1px solid #2e2e35;flex:0 0 auto;flex-wrap:nowrap;overflow:hidden}
.app-head .back{width:28px;height:28px;padding:0;font-size:15px;line-height:26px;border-radius:8px;flex:0 0 auto}
.app-head .ico{width:38px;height:38px;border-radius:10px;display:flex;align-items:center;justify-content:center;
  font-weight:600;font-size:16px;color:#fff;flex:0 0 auto}
.app-head .title{min-width:0;flex:1}
.app-head .title .t{font-size:15px;font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.app-head .title .s{font-size:11px;color:#8b8b95;margin-top:1px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.app-head .acts{display:flex;gap:6px;flex-wrap:nowrap;flex:0 0 auto}
.app-body{flex:1;min-height:0;overflow:auto;padding:12px 16px 24px}
.app-body thead th{position:sticky;top:0;background:#1b1b1f;z-index:1}

/* Side（栈第 3 层：日志 / 属性） */
.pane{display:none;flex-direction:column;min-width:0;min-height:0;background:#1e1e23}
body[data-sheet] .pane{display:flex}
.pane-head{display:flex;align-items:center;gap:8px;padding:0 14px;height:52px;min-height:52px;
  border-bottom:1px solid #2e2e35;flex:0 0 auto;flex-wrap:nowrap;overflow:hidden}
.pane-head .back{width:28px;height:28px;padding:0;font-size:15px;line-height:26px;border-radius:8px;flex:0 0 auto}
body[data-w="wide"] .pane-head .back{display:none}         /* 宽屏三栏：列表还在，不需要返回 */
.pane-head .t{font-size:13px;font-weight:600;white-space:nowrap;min-width:0;overflow:hidden;text-overflow:ellipsis}
.pane-head .s{font-size:11px;color:#8b8b95;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.pane-head .sp{flex:1}
.tabs{display:flex;gap:6px;flex:0 0 auto}
.tabs button.on{background:#2e3947;border-color:#3d4f63;color:#a9c7e4}
.pane-body{flex:1;min-height:0;overflow:auto}
pre.log{margin:0;padding:10px 14px;font:11.5px/1.6 Consolas,monospace;color:#c9c9d3;white-space:pre-wrap;word-break:break-all}
.logempty{padding:30px 16px;text-align:center;color:#75777f;font-size:12.5px;line-height:1.7}

table{width:100%;border-collapse:collapse}
th,td{text-align:left;padding:6px 8px;border-bottom:1px solid #2a2a31;vertical-align:middle;white-space:nowrap}
th{color:#8b8b95;font-weight:500;font-size:12px}
tr:hover td{background:#212127}
tr.sel td{background:#232a36}
.mono{font-family:Consolas,monospace;font-size:12px;color:#a9a9b4}
.num{font-variant-numeric:tabular-nums}
.muted{color:#75757f}
.failtext{color:#ff9aa8}
.badge{display:inline-block;padding:1px 6px;border-radius:999px;background:#2c2c35;color:#a9a9b4;font-size:11px}
.sect{color:#8b8b95;font-size:11px;letter-spacing:.04em;margin:14px 0 6px}
.kv{display:flex;gap:10px;padding:5px 0;border-bottom:1px solid #26262c;font-size:12px}
.kv b{flex:0 0 64px;color:#75757f;font-weight:400}
.kv span{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:#b9b9c4}
.empty{padding:40px 20px;text-align:center;color:#75777f;font-size:12.5px;line-height:1.7}
.filters{display:flex;gap:6px;flex-wrap:wrap;margin:2px 0 8px}
.fpill{padding:2px 9px;font-size:11px;border-radius:999px;background:#24242b;border:1px solid #33333c;color:#a9a9b4;cursor:pointer}
.fpill:hover{background:#2c2c35}
.fpill.on{background:#2e3947;border-color:#3d4f63;color:#a9c7e4}
.hintbar{padding:8px 16px;border-top:1px solid #2e2e35;color:#75777f;font-size:11px;flex:0 0 auto}

/* 执行历史浮层 */
.modal{position:fixed;inset:0;background:rgba(0,0,0,.55);display:none;z-index:50;
  justify-content:center;align-items:flex-start;padding:7vh 4vw}
.modal.on{display:flex}
.modal-box{width:min(1120px,94vw);max-height:82vh;display:flex;flex-direction:column;
  background:#222228;border:1px solid #3a3a44;border-radius:12px;
  box-shadow:0 18px 48px rgba(0,0,0,.55);overflow:hidden}
.modal-head{display:flex;align-items:center;gap:10px;padding:10px 14px;border-bottom:1px solid #2e2e35;flex:0 0 auto}
.modal-head .t{font-size:13px;font-weight:600}
.modal-head .s{font-size:11px;color:#8b8b95}
.modal-head .sp{flex:1}
.modal-body{overflow:auto;min-height:0}
.modal-body table{min-width:980px}
.modal-body thead th{position:sticky;top:0;background:#222228;z-index:1}
.modal-foot{padding:8px 14px;border-top:1px solid #2e2e35;color:#75777f;font-size:11px;flex:0 0 auto}
.clip{display:inline-block;max-width:360px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;vertical-align:bottom}

/* 窄屏：顶栏与记录页头部允许换行，避免按钮被裁掉 */
body[data-w="narrow"] header{flex-wrap:wrap;padding:10px 12px}
body[data-w="narrow"] .meta #host{display:none}
body[data-w="narrow"] .kbd{display:none}
body[data-w="narrow"] .app-head{height:auto;min-height:52px;padding:8px 12px;flex-wrap:wrap;row-gap:8px;overflow:visible}
body[data-w="narrow"] .app-head .acts{width:100%;justify-content:flex-end;flex-wrap:wrap}
body[data-w="narrow"] .pane-head .s{display:none}
/* 窄屏命令页里打开搜索：把 Launcher 临时铺到整屏（不改变 route / 右侧内容） */
body[data-w="narrow"][data-route="app"].search-open .left{display:flex;position:absolute;inset:0;z-index:12;background:#1b1b1f}
</style>
</head>
<body data-route="home" data-w="wide">

<header>
  <div class="brand">quickcmd <span class="sub">控制台</span></div>
  <div class="meta">
    <span id="host">连接中…</span>
    <button class="ghost" id="histbtn" title="最近 60 条执行记录">执行历史</button>
    <button class="ghost" id="searchbtn" title="搜索命令（Ctrl+K）">🔍 搜索<span class="kbd"> Ctrl+K</span></button>
    <button class="ghost" id="refresh" title="重新读取状态">刷新</button>
  </div>
</header>

<div class="layout" id="work">
  <div class="left" id="left">
    <aside class="side">
      <div class="side-head">项目</div>
      <div id="projects"></div>
    </aside>

    <section class="launcher-wrap" id="launcherwrap">
      <button class="lsearch" id="lsearchbtn" title="搜索全部命令（Ctrl+K）">
        <span class="ico">🔍</span>
        <span class="ph">搜索全部命令…</span>
        <span class="kbd">Ctrl+K</span>
      </button>
      <div class="launcher" id="launcher"></div>
      <div class="dots" id="dots"></div>
      <div class="searchpage" id="searchpage">
        <div class="search-head">
          <span class="ico">🔍</span>
          <input id="sq" placeholder="搜索全部命令…" autocomplete="off" spellcheck="false">
          <button class="ghost" id="sclose" title="关闭 (Esc)">✕</button>
        </div>
        <div class="search-results" id="sresults"></div>
        <div class="search-foot">↑↓ 选择 · Enter 打开 · Esc 关闭</div>
      </div>
    </section>
  </div>

  <div class="right" id="right">
    <div class="right-empty" id="rightempty">
      <div class="re-inner">
        <div class="re-t">从左侧选择一个命令</div>
        <div class="re-s">启动 / 停止、查看执行记录与日志</div>
        <div class="re-s">滚轮或 ←→ 切换项目屏</div>
      </div>
    </div>
    <section class="app" id="app"></section>
    <section class="pane" id="pane"></section>
  </div>
</div>

<div id="hist" class="modal">
  <div class="modal-box">
    <div class="modal-head">
      <div class="t">执行历史</div>
      <div class="s">最近 60 条 · 来自 history.jsonl</div>
      <span class="sp"></span>
      <button id="histclose">关闭 (Esc)</button>
    </div>
    <div class="modal-body" id="histbody"></div>
    <div class="modal-foot">日志会在新标签页打开（尾部 300 行）</div>
  </div>
</div>

<script>
const TOKEN = "__QC_TOKEN__";
const PORT = __QC_PORT__;
const INT_MIN = -2147483648;
const $ = s => document.querySelector(s);

let state = null;
let project = "";            // 屏 = 项目名（不再有「全部」；全部=搜索浮层）
let route = "home";          // home | app
let currentId = "";          // 当前命令 short id
let sheet = null;            // null | "log" | "props"
let selKey = "";             // 选中执行记录的稳定 key
let runFilter = "all";       // all | live | ok | fail
let histOpen = false;
let searchOpen = false;
let searchIndex = 0;
let searchRows = [];
let inited = false;
let lastSig = null;
let loading = false;
let logSeq = 0;

const STATUS = {started:"已启动", exited:"已退出", killed:"已结束", failed:"失败", max:"超实例上限", "host-unavailable":"宿主不在", local:"本地直跑"};
const logCache = new Map();

const esc = v => String(v == null ? "" : v).replace(/[&<>"']/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[c]));
const cmds = () => (state && state.commands) || [];
const insts = () => (state && state.instances) || [];
const hist = () => (state && state.history) || [];
const projOf = c => (c.proj || "").trim();
const projLabel = p => p === "" ? "未分组" : p;
const sameShort = (a, b) => String(a || "").toLowerCase() === String(b || "").toLowerCase();
const appById = id => cmds().find(c => c.shortId === id) || null;
const liveOf = c => insts().filter(i => sameShort(i.shortId, c.shortId));
const isWide = () => document.body.dataset.w === "wide";

function hueOf(s) {
  let h = 0;
  for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) % 360;
  return h;
}
function initialsOf(s) {
  const t = (s || "?").trim();
  if (/^[\x20-\x7e]+$/.test(t)) {
    const w = t.split(/\s+/).filter(Boolean);
    return (w.length > 1 ? w[0][0] + w[1][0] : t.slice(0, 2)).toUpperCase();
  }
  return t.slice(0, 1);
}
function time(iso) {
  if (!iso) return "-";
  const d = new Date(iso);
  if (isNaN(d.getTime())) return "-";
  const p = n => String(n).padStart(2, "0");
  return (d.getMonth() + 1) + "-" + p(d.getDate()) + " " + p(d.getHours()) + ":" + p(d.getMinutes()) + ":" + p(d.getSeconds());
}
const stripSec = s => String(s).replace(/:\d\d$/, "");
function dur(iso) {
  if (!iso) return "-";
  const ms = Date.now() - new Date(iso).getTime();
  if (isNaN(ms) || ms < 0) return "-";
  return durMs(ms);
}
function durMs(ms) {
  if (ms == null || ms < 0) return "-";
  const s = Math.floor(ms / 1000);
  if (s < 60) return s + " 秒";
  if (s < 3600) return Math.floor(s / 60) + " 分";
  if (s < 86400) return Math.floor(s / 3600) + " 小时 " + (Math.floor(s / 60) % 60) + " 分";
  return Math.floor(s / 86400) + " 天 " + (Math.floor(s / 3600) % 24) + " 小时";
}

/* ---------- 项目栏 / Launcher 分屏 ---------- */
function screenNames() {
  const named = [...new Set(cmds().map(projOf).filter(Boolean))].sort((a, b) => a.localeCompare(b, "zh"));
  const none = cmds().some(c => !projOf(c));
  return [...named, ...(none ? ["未分组"] : [])];
}
function screenIndex() {
  const i = screenNames().indexOf(project);
  return i < 0 ? 0 : i;
}
function commandsInScreen() {
  if (project === "未分组") return cmds().filter(c => !projOf(c));
  if (!project) return [];
  return cmds().filter(c => projOf(c) === project);
}
function saveProject() {
  try { localStorage.setItem("qc.proj", project); } catch (e) { }
}
// 默认项目：上次浏览的项目 → 有运行中命令的项目 → 最近更新的命令所属项目 → 第一个项目
function defaultProject(names) {
  let saved = "";
  try { saved = localStorage.getItem("qc.proj") || ""; } catch (e) { }
  if (names.includes(saved)) return saved;
  const running = cmds().find(c => c.state === "running");
  if (running && names.includes(projLabel(projOf(running)))) return projLabel(projOf(running));
  const latest = cmds().slice().sort((a, b) => new Date(b.updatedAt) - new Date(a.updatedAt))[0];
  if (latest && names.includes(projLabel(projOf(latest)))) return projLabel(projOf(latest));
  return names[0];
}

function renderProjects() {
  const all = cmds();
  let html = "";

  for (const n of screenNames()) {
    const list = n === "未分组" ? all.filter(c => !projOf(c)) : all.filter(c => projOf(c) === n);
    if (n === "未分组") html += '<div class="psep"></div>';
    const running = list.filter(c => c.state === "running").length;
    html += '<div class="pitem' + (project === n ? " on" : "") + '" data-proj="' + esc(n) + '" title="' + esc(n) + '">' +
      '<span class="pname">' + esc(n) + '</span>' +
      (running ? '<span class="dot up" title="' + running + ' 个运行中"></span>' : '') +
      '<span class="cnt">' + list.length + '</span></div>';
  }
  $("#projects").innerHTML = html;
}

function renderDots() {
  const names = screenNames();
  const cur = screenIndex();
  $("#dots").innerHTML = names.map((n, i) =>
    '<i class="' + (i === cur ? "on" : "") + '" data-screen="' + i + '" title="' + esc(n) + '"></i>').join("");
  $("#dots").classList.toggle("hide", searchOpen || names.length < 2);
}

function goScreen(i) {
  closeSearch();
  const names = screenNames();
  if (!names.length) {
    project = "";
    renderProjects();
    renderLauncher();
    renderDots();
    setHash();
    return;
  }
  const next = Math.max(0, Math.min(names.length - 1, i));
  project = names[next];
  saveProject();
  renderProjects();
  renderLauncher();
  $("#launcher").scrollTop = 0;
  setHash();
}

/* ---------- Launcher ---------- */
function tileHtml(c, on) {
  const running = c.state === "running";
  return '<div class="tile' + (on ? " on" : "") + '" data-tile="' + esc(c.shortId) + '" title="' +
    esc(c.title + (running ? "（运行中）" : "") + " · 点击查看执行记录") + '">' +
    '<div class="ico" style="background:hsl(' + hueOf(c.title) + ',24%,32%)">' + esc(initialsOf(c.title)) +
      (running ? '<span class="dot up"></span>' : '') + '</div>' +
    '<div class="tname">' + esc(c.title) + '</div>' +
    '<div class="tproj">' + esc(projLabel(projOf(c))) + '</div>' +
    '</div>';
}

function renderLauncher() {
  if (!state) {
    $("#launcher").innerHTML = '<div class="empty">连接中…</div>';
    renderDots();
    return;
  }

  const list = commandsInScreen();
  if (!list.length) {
    $("#launcher").innerHTML = '<div class="empty">' +
      (cmds().length
        ? (project ? '这个项目还没有命令' : '选择一个项目')
        : '还没有注册任何命令。<br><br>用 <span class="mono">quickcmd add ...</span> 注册，或让 agent 帮你加。') +
      '</div>';
    renderDots();
    return;
  }

  $("#launcher").innerHTML = '<div class="home">' + list.map(c => tileHtml(c)).join("") + '</div>';
  renderDots();
}

// 滚轮：Launcher 内先滚动；到边界后切项目屏（搜索浮层打开时不响应）
function onLauncherWheel(e) {
  if (searchOpen || !e.deltaY) return;
  const el = $("#launcher");
  const atTop = el.scrollTop <= 0;
  const atBottom = el.scrollTop + el.clientHeight >= el.scrollHeight - 1;
  const goingDown = e.deltaY > 0;
  if ((goingDown && !atBottom) || (!goingDown && !atTop)) return;
  goScreen(screenIndex() + (goingDown ? 1 : -1));
}

// 首页右侧空白区：滚轮直接切项目屏
function onHomeWheel(e) {
  if (route !== "home" || searchOpen || !e.deltaY) return;
  if (e.target.closest(".side") || e.target.closest(".launcher-wrap")) return;
  e.preventDefault();
  goScreen(screenIndex() + (e.deltaY > 0 ? 1 : -1));
}

/* ---------- 执行记录（实例 + 历史） ---------- */
function runsOf(c) {
  const out = [];
  const live = liveOf(c).slice().sort((a, b) => new Date(b.started) - new Date(a.started));
  for (const i of live) {
    out.push({
      live: true, id: i.id, mode: i.mode || "-", started: i.started,
      procs: (i.tree || []).length, code: null, status: "started",
      logKey: i.logPath || c.shortId, hasLog: !!(i.logPath || c.captureLogs),
    });
  }
  for (const h of hist()) {
    if (h.event !== "finish" || !sameShort(h.shortId, c.shortId)) continue;
    out.push({
      live: false, id: h.instanceId || "-", mode: h.mode || "-",
      started: startOf(h), durText: durMs(h.durationMs),
      procs: 0,
      code: (h.exitCode === INT_MIN || h.exitCode == null) ? null : h.exitCode,
      status: h.status || "exited",
      logKey: h.logPath || "", hasLog: !!h.logPath,
    });
  }
  return out;
}

function startOf(h) {
  const t = new Date(h.timestamp).getTime();
  if (isNaN(t)) return h.timestamp;
  if (h.durationMs != null && h.durationMs >= 0) return new Date(t - h.durationMs).toISOString();
  return h.timestamp;
}

const runKey = r => (r.live ? "L:" : "H:") + r.id + "@" + r.started;

function runKind(r) {
  if (r.live) return "live";
  const bad = (r.code != null && r.code !== 0) || ["failed", "max", "host-unavailable"].includes(r.status);
  return bad ? "fail" : "ok";
}

function pickRun(runs) {
  let r = runs.find(x => runKey(x) === selKey);
  if (!r) r = runs[0] || null;
  if (r) selKey = runKey(r);
  return r;
}

function statusCell(r) {
  if (r.live) return '<span class="dot up"></span> 运行中 <span class="muted">· ' + r.procs + ' 进程</span>';
  const bad = runKind(r) === "fail";
  if (bad) {
    return '<span class="dot fail"></span> <span class="failtext">' + esc(STATUS[r.status] || "已退出") +
      (r.code != null ? " (" + r.code + ")" : "") + '</span>';
  }
  return '<span class="dot down"></span> <span class="muted">已结束</span>';
}

/* ---------- 栈第 2 层：命令页 ---------- */
function renderApp() {
  const c = appById(currentId);
  const app = $("#app");
  if (!c) { app.innerHTML = ""; return; }
  const runs = runsOf(c);
  const run = pickRun(runs);
  const live = liveOf(c);
  const running = live.length > 0;
  const first = live[0];
  const openUrl = (first && first.url) || c.url || "";

  let acts = running
    ? (openUrl
        ? '<button class="primary" data-open="' + esc(openUrl) + '">打开</button>'
        : '<button class="primary" data-front="' + esc(first.id) + '">置前</button>') +
      '<button class="danger" data-stopall="1">停止</button>'
    : '<button class="primary" data-start="' + esc(c.shortId) + '">启动</button>';

  const pinned = isPinned(c.shortId);
  acts += '<button data-open-sheet="props">属性</button>' +
    '<button data-pin="' + esc(c.shortId) + '" data-next="' + (pinned ? "0" : "1") + '" title="' +
      (pinned ? "从首页移除" : "固定到首页") + '">' + (pinned ? "已固定" : "固定") + '</button>';

  const sub = [projLabel(projOf(c)), c.shortId, c.type === "web" ? "网站" : "程序",
      running ? "运行中" + (live.length > 1 ? " " + live.length + " 个实例" : "") : ""]
    .filter(Boolean).join(" · ");

  app.innerHTML =
    '<div class="app-head">' +
      '<button class="back" data-pop="app" title="返回 (Esc)">←</button>' +
      '<div class="ico" style="background:hsl(' + hueOf(c.title) + ',24%,32%)">' + esc(initialsOf(c.title)) + '</div>' +
      '<div class="title"><div class="t">' + esc(c.title) + '</div><div class="s">' + esc(sub) + '</div></div>' +
      '<div class="acts">' + acts + '</div>' +
    '</div>' +
    '<div class="app-body">' + runsHtml(c, runs) + '</div>' +
    '<div class="hintbar">点执行记录看日志 · 输入即搜索 · Esc 返回</div>';
}

function runsHtml(c, all) {
  if (!all.length) {
    return '<div class="empty">还没有执行记录<br><br>' +
      '<button class="primary" data-start="' + esc(c.shortId) + '">启动一次</button></div>';
  }

  const counts = {all: all.length, live: 0, ok: 0, fail: 0};
  all.forEach(r => counts[runKind(r)]++);
  const pill = (k, label) =>
    '<button class="fpill' + (runFilter === k ? " on" : "") + '" data-rfilter="' + k + '">' + label + ' ' + counts[k] + '</button>';
  const filters = '<div class="filters">' +
    pill("all", "全部") + pill("live", "运行中") + pill("ok", "已结束") + pill("fail", "失败") + '</div>';

  const filtered = all.map((r, i) => ({r, i}))
    .filter(x => runFilter === "all" || runKind(x.r) === runFilter);
  if (!filtered.length) return filters + '<div class="empty">没有符合筛选的执行记录</div>';

  const compact = document.body.dataset.compact === "1";
  const rows = filtered.map(({r}) => {
    const act = r.live
      ? '<button class="danger" data-killinst="' + esc(r.id) + '">停止</button>'
      : '<button data-replay="' + esc(c.shortId) + '">重跑</button>';
    const durCell = r.live
      ? '<span data-since="' + esc(r.started) + '">' + dur(r.started) + '</span>'
      : esc(r.durText);
    return '<tr class="' + (runKey(r) === selKey ? "sel" : "") + '" data-run="' + esc(runKey(r)) + '">' +
      '<td>' + statusCell(r) + '</td>' +
      '<td class="mono">' + esc(r.id) + ' <span class="badge">' + esc(r.mode) + '</span></td>' +
      '<td class="mono">' + esc(stripSec(time(r.started))) +
        (compact ? ' <span class="muted">· ' + durCell + '</span>' : '') + '</td>' +
      (compact ? '' : '<td class="mono num">' + durCell + '</td>') +
      '<td>' + act + '</td></tr>';
  }).join("");

  return filters +
    '<table><thead><tr><th>状态</th><th>实例</th><th>启动</th>' +
    (compact ? '' : '<th>时长</th>') + '<th>操作</th></tr></thead><tbody>' + rows + '</tbody></table>';
}

/* ---------- 栈第 3 层：日志 / 属性 ---------- */
function renderPane() {
  const c = appById(currentId);
  const pane = $("#pane");
  if (!c || !sheet) { pane.innerHTML = ""; return; }
  const runs = runsOf(c);
  const run = pickRun(runs);
  const logKey = run && run.hasLog ? run.logKey : "";

  const head =
    '<div class="pane-head">' +
      '<button class="back" data-pop="sheet" title="关闭 (Esc)">←</button>' +
      (sheet === "log"
        ? '<div class="t">' + esc(run ? run.id : "日志") + '</div>' +
          '<div class="s">' + esc(run ? time(run.started) + " · " + (run.live ? "运行中" : (STATUS[run.status] || "已结束")) : "") + '</div>'
        : '<div class="t">属性</div><div class="s">' + esc(c.title) + '</div>') +
      '<span class="sp"></span>' +
      (sheet === "log" && logKey ? '<button class="ghost" data-logkey="' + esc(logKey) + '" title="在新标签页打开日志">↗</button>' : '') +
      '<div class="tabs">' +
        '<button class="' + (sheet === "log" ? "on" : "") + '" data-tab="log">日志</button>' +
        '<button class="' + (sheet === "props" ? "on" : "") + '" data-tab="props">属性</button>' +
      '</div>' +
    '</div>';

  let body;
  if (sheet === "props") {
    body = '<div class="pane-body">' + propsHtml(c, runs) + '</div>';
  } else if (!run) {
    body = '<div class="pane-body"><div class="logempty">没有选中的执行记录</div></div>';
  } else if (!run.hasLog) {
    body = '<div class="pane-body"><div class="logempty">这次执行没有日志<br>（启动时未捕获输出，或日志已被清理）</div></div>';
  } else {
    const hit = logCache.get(run.logKey);
    body = '<div class="pane-body"><pre class="log" id="logpre">' + esc(hit ? hit.text : "加载中…") + '</pre></div>';
  }
  pane.innerHTML = head + body;

  if (sheet === "log" && run && run.hasLog) {
    const box = pane.querySelector(".pane-body");
    if (box && logCache.has(run.logKey)) box.scrollTop = box.scrollHeight;
    refreshLog();
  }
}

function propsHtml(c, runs) {
  const row = (k, v) => '<div class="kv"><b>' + k + '</b><span title="' + esc(v) + '">' + esc(v) + '</span></div>';
  const live = liveOf(c);
  const last = runs.find(r => !r.live);
  const env = c.env && c.env.length ? c.env.join("  ") : "(无)";
  return '<div class="sect">属性</div>' +
    row("类型", c.type === "web" ? "网站 / 常驻服务" : "程序") +
    (c.url ? row("URL", c.url) : '') +
    row("项目", projLabel(projOf(c))) +
    row("short id", c.shortId) +
    row("启动命令", c.commandLine || "-") +
    row("工作目录", c.effectiveWorkdir || c.workdir || "-") +
    row("环境变量", env) +
    row("实例上限", c.maxInstances) +
    row("root pid", live.length ? live.map(i => i.pid).join(", ") : "-") +
    row("窗口", c.keepOpen ? "结束后保持" : "结束后关闭") +
    row("日志", c.captureLogs ? "捕获（尾部 300 行）" : "未捕获") +
    row("首页固定", isPinned(c.shortId) ? "是" : "否") +
    row("更新", time(c.updatedAt)) +
    '<div class="sect">执行</div>' +
    row("次数", runs.length + " 次") +
    row("上次", last ? time(last.started) + " · " + (runKind(last) === "fail" ? "失败" : "已结束") + " · " + last.durText : "无");
}

/* ---------- 日志读取 ---------- */
function applyLogText(pre, text) {
  const box = pre.parentElement;
  const atBottom = !box || (box.scrollTop + box.clientHeight >= box.scrollHeight - 20);
  if (pre.textContent !== text) pre.textContent = text;
  if (atBottom && box) box.scrollTop = box.scrollHeight;
}

async function refreshLog() {
  const c = appById(currentId);
  if (!c || sheet !== "log") return;
  const run = pickRun(runsOf(c));
  if (!run || !run.hasLog || !run.logKey) return;
  const pre = $("#logpre");
  if (!pre) return;

  const key = run.logKey;
  const hit = logCache.get(key);
  if (hit && (!run.live || Date.now() - hit.at < 1500)) return;

  const seq = ++logSeq;
  let text;
  try {
    const res = await fetch("/api/log?key=" + encodeURIComponent(key) + "&tail=300", {cache: "no-store"});
    if (res.ok) text = await res.text();
    else if (res.status === 404) text = "日志还没有内容（可能尚未写入，或已被清理）";
    else text = "读取日志失败：HTTP " + res.status;
  } catch (err) {
    text = "读取日志失败：" + err;
  }
  if (seq !== logSeq) return;
  logCache.set(key, {text, at: Date.now()});
  const cur = $("#logpre");
  if (cur) applyLogText(cur, text);
}

/* ---------- 栈操作 ---------- */
function openApp(id) {
  const c = appById(id);
  if (!c) return;
  const wasSearch = searchOpen;
  if (wasSearch) closeSearch();
  const cp = projLabel(projOf(c));
  if ((wasSearch || project !== cp) && screenNames().includes(cp)) {
    project = cp;
    saveProject();
    renderProjects();
    renderLauncher();
  }
  route = "app";
  currentId = c.shortId;
  selKey = "";
  runFilter = "all";
  sheet = (isWide() && runsOf(c).length) ? "log" : null;
  document.body.dataset.route = "app";
  syncSheet();
  renderApp();
  renderPane();
  updateTitle();
  setHash();
}

function popStack(level) {
  if (level === "sheet") {
    if (sheet) { sheet = null; syncSheet(); renderPane(); }
    return;
  }
  if (route !== "app") return;
  route = "home";
  currentId = "";
  selKey = "";
  sheet = null;
  document.body.dataset.route = "home";
  syncSheet();
  renderApp();
  renderPane();
  renderProjects();
  renderLauncher();
  updateTitle();
  setHash();
}

function openSheet(what) {
  sheet = what;
  syncSheet();
  renderPane();
}

function syncSheet() {
  if (sheet) document.body.dataset.sheet = sheet === "log" ? "log" : "props";
  else delete document.body.dataset.sheet;
}

function isPinned(shortId) {
  const list = (state && state.host && state.host.pinned) || [];
  return list.some(x => sameShort(x, shortId));
}

/* ---------- 请求 ---------- */
async function post(path, key, reload = true) {
  try {
    const r = await fetch(path, {
      method: "POST",
      headers: {"Content-Type": "application/json", "X-QC-Token": TOKEN},
      body: JSON.stringify({key})
    });
    const j = await r.json().catch(() => ({}));
    if (!r.ok || j.ok === false) alert(j.message || ("HTTP " + r.status));
  } catch (e) {
    alert("请求失败：" + e);
  }
  if (reload) await load();
}

async function setPinned(key, on) {
  try {
    const r = await fetch("/api/pin", {
      method: "POST",
      headers: {"Content-Type": "application/json", "X-QC-Token": TOKEN},
      body: JSON.stringify({key, pinned: on})
    });
    const j = await r.json().catch(() => ({}));
    if (!r.ok || j.ok === false) alert(j.message || ("HTTP " + r.status));
  } catch (e) {
    alert("请求失败：" + e);
  }
  await load();
}

async function stopAll(c) {
  const live = liveOf(c);
  if (!live.length) return;
  for (const i of live) await post("/api/kill", i.id, false);
  await load();
}

/* ---------- 执行历史浮层 ---------- */
function renderHist() {
  const list = hist();
  const box = $("#histbody");
  if (!list.length) { box.innerHTML = '<div class="empty">还没有执行记录</div>'; return; }
  const rows = list.map(e => {
    const status = STATUS[e.status] || e.status || "-";
    const code = (e.exitCode != null && e.exitCode !== INT_MIN) ? e.exitCode : "-";
    const cmd = e.commandLine || e.title || "-";
    return '<tr>' +
      '<td class="mono">' + time(e.timestamp) + '</td>' +
      '<td>' + (e.event === "finish" ? "完成" : "启动") + '</td>' +
      '<td>' + (e.kind === "exec" ? "直跑" : "注册") + '</td>' +
      '<td class="mono">' + esc(e.source || "-") + '</td>' +
      '<td>' + esc(status) + '</td>' +
      '<td class="mono num">' + code + '</td>' +
      '<td class="mono">' + durMs(e.durationMs) + '</td>' +
      '<td class="mono">' + esc(e.instanceId || "-") +
        (e.mode ? ' <span class="badge">' + esc(e.mode) + '</span>' : '') + '</td>' +
      '<td><span class="clip" title="' + esc(cmd) + '">' + esc(cmd) + '</span></td>' +
      '<td>' + (e.logPath ? '<button data-logkey="' + esc(e.logPath) + '">日志</button>' : '<span class="muted">-</span>') + '</td>' +
      '</tr>';
  }).join("");
  box.innerHTML = '<table><thead><tr><th>时间</th><th>事件</th><th>类型</th><th>来源</th><th>状态</th>' +
    '<th>退出码</th><th>时长</th><th>实例</th><th>命令</th><th>日志</th></tr></thead><tbody>' + rows + '</tbody></table>';
}
function openHist() {
  histOpen = true;
  renderHist();
  $("#hist").classList.add("on");
}
function closeHist() {
  histOpen = false;
  $("#hist").classList.remove("on");
}

/* ---------- 顶栏 / 地址栏 / 标题 ---------- */
function updateMeta() {
  if (!state) return;
  const running = cmds().filter(c => c.state === "running").length;
  const el = $("#host");
  el.textContent = "qchost " + state.host.version + " · 端口 " + state.host.port + " · " + running + " 个运行中";
  el.title = "更新于 " + new Date().toLocaleTimeString();
}

function updateTitle() {
  const c = appById(currentId);
  document.title = (route === "app" && c ? c.title + " · " : "") + "quickcmd 控制台";
}

function setHash() {
  const h = "#" + encodeURIComponent(project || "全部") +
    (route === "app" && currentId ? "/" + encodeURIComponent(currentId) : "");
  try { history.replaceState(null, "", h); } catch (e) { }
}

function applyHash() {
  const raw = location.hash.slice(1);
  if (!raw) return;
  const i = raw.indexOf("/");
  let p = i < 0 ? raw : raw.slice(0, i);
  let sid = i < 0 ? "" : raw.slice(i + 1);
  try { p = decodeURIComponent(p); } catch (e) { }
  try { sid = decodeURIComponent(sid); } catch (e) { }
  const names = screenNames();
  if (names.includes(p)) { project = p; saveProject(); }
  if (sid) {
    const c = appById(sid);
    if (c) {
      const cp = projLabel(projOf(c));
      if (names.includes(cp)) { project = cp; saveProject(); }
      route = "app";
      currentId = c.shortId;
      sheet = (isWide() && runsOf(c).length) ? "log" : null;
      document.body.dataset.route = "app";
      syncSheet();
    }
  }
}

/* ---------- 渲染入口 ---------- */
function render() {
  const names = screenNames();
  if (names.length && !names.includes(project)) {
    project = defaultProject(names);
    saveProject();
    setHash();
  }
  if (route === "app" && !appById(currentId)) {
    route = "home"; currentId = ""; sheet = null; selKey = "";
    document.body.dataset.route = "home";
    syncSheet();
  }
  renderProjects();
  renderLauncher();
  renderApp();
  renderPane();
  if (searchOpen) renderSearchResults();
  if (histOpen) renderHist();
  updateMeta();
  updateTitle();
}

// 轮询重绘会重建 innerHTML：记录/恢复滚动位置，避免页面"跳回顶部"
function captureScroll() {
  const g = s => { const el = $(s); return el ? {top: el.scrollTop, left: el.scrollLeft} : null; };
  return {launcher: g("#launcher"), app: g(".app-body"), pane: g(".pane-body"), search: g(".search-results")};
}
function restoreScroll(s) {
  const put = (sel, v) => { const el = $(sel); if (el && v) { el.scrollTop = v.top; el.scrollLeft = v.left; } };
  put("#launcher", s.launcher);
  put(".app-body", s.app);
  put(".pane-body", s.pane);
  put(".search-results", s.search);
}

async function load() {
  if (loading) return;
  loading = true;
  try {
    const r = await fetch("/api/state", {cache: "no-store"});
    if (!r.ok) throw new Error("HTTP " + r.status);
    state = await r.json();

    // 只比较数据本身（host.uptimeSeconds 每次都变，不参与判断）
    const sig = JSON.stringify([state.commands, state.instances, state.history, state.host.pinned]);
    if (sig !== lastSig) {
      const sc = captureScroll();
      lastSig = sig;
      if (!inited) { inited = true; applyHash(); }
      // 宽屏下新出现的执行记录：直接把日志铺到右边
      if (isWide() && route === "app" && !sheet) {
        const c = appById(currentId);
        if (c && runsOf(c).length) { sheet = "log"; syncSheet(); }
      }
      render();
      restoreScroll(sc);
      if (sheet === "log") refreshLog();
    } else {
      updateMeta();
      if (route === "app" && sheet === "log") refreshLog();
    }
  } catch (e) {
    $("#host").textContent = "控制台未连接（qchost 可能已退出）";
  } finally {
    loading = false;
  }
}

/* ---------- 「全部」搜索浮层 ---------- */
function searchQuery() {
  const inp = $("#sq");
  return inp ? inp.value.trim().toLowerCase() : "";
}
function searchList() {
  const q = searchQuery();
  if (!q) return cmds();
  return cmds().filter(c =>
    [c.title, c.proj, c.shortId, c.commandLine, c.url, c.effectiveWorkdir].some(v => (v || "").toLowerCase().includes(q)));
}
function renderSearchResults() {
  const box = $("#sresults");
  if (!box) return;
  searchRows = searchList();
  if (searchIndex >= searchRows.length) searchIndex = 0;
  if (searchIndex < 0) searchIndex = 0;

  if (!searchRows.length) {
    const q = ($("#sq").value || "").trim();
    box.innerHTML = '<div class="search-empty">' +
      (q ? '没有匹配「' + esc(q) + '」的命令' : '还没有注册任何命令') + '</div>';
    return;
  }

  box.innerHTML = '<div class="home">' +
    searchRows.map((c, i) => tileHtml(c, i === searchIndex)).join("") + '</div>';
}

function openSearch(seed) {
  if (histOpen) closeHist();
  const inp = $("#sq");
  const wasOpen = searchOpen;
  searchOpen = true;
  document.body.classList.add("search-open");
  if (typeof seed === "string") {
    inp.value = seed;
    searchIndex = 0;
  } else if (!wasOpen) {
    inp.value = "";
    searchIndex = 0;
  }
  renderProjects();
  renderDots();
  renderSearchResults();
  $("#sresults").scrollTop = 0;
  inp.focus();
  if (typeof seed === "string") {
    const n = inp.value.length;
    inp.setSelectionRange(n, n);
  } else {
    inp.select();
  }
}

function closeSearch() {
  if (!searchOpen) return;
  searchOpen = false;
  document.body.classList.remove("search-open");
  searchRows = [];
  searchIndex = 0;
  const inp = $("#sq");
  if (inp) { inp.value = ""; try { inp.blur(); } catch (e) { } }
  renderProjects();
  renderDots();
}

function moveSearch(delta) {
  if (!searchOpen || !searchRows.length) return;
  searchIndex = (searchIndex + delta + searchRows.length) % searchRows.length;
  renderSearchResults();
  const on = $("#sresults .tile.on");
  if (on) on.scrollIntoView({block: "nearest", inline: "nearest"});
}

function openSelectedSearch() {
  if (!searchOpen || !searchRows.length) return;
  const i = Math.max(0, Math.min(searchRows.length - 1, searchIndex));
  openApp(searchRows[i].shortId);
}

/* ---------- 交互 ---------- */

document.addEventListener("click", e => {
  if (e.target.id === "hist") { closeHist(); return; }

  const tile = e.target.closest("[data-tile]");
  if (tile) { openApp(tile.dataset.tile); return; }

  // 搜索浮层里点空白处：把焦点还给输入框
  if (searchOpen && e.target.closest("#searchpage") && !e.target.closest("button")) {
    $("#sq").focus();
    return;
  }

  const pop = e.target.closest("[data-pop]");
  if (pop) { popStack(pop.dataset.pop); return; }

  const rfilter = e.target.closest("[data-rfilter]");
  if (rfilter) {
    runFilter = rfilter.dataset.rfilter;
    const c = appById(currentId);
    if (c) {
      const runs = runsOf(c);
      const sel = runs.find(r => runKey(r) === selKey);
      const visible = runs.filter(r => runFilter === "all" || runKind(r) === runFilter);
      if (!sel || !visible.includes(sel)) selKey = visible[0] ? runKey(visible[0]) : "";
    }
    renderApp();
    if (sheet) renderPane();
    return;
  }

  const row = e.target.closest("[data-run]");
  if (row && !e.target.closest("button")) {
    selKey = row.dataset.run;
    openSheet("log");
    renderApp();
    return;
  }

  const tab = e.target.closest("[data-tab]");
  if (tab) { openSheet(tab.dataset.tab); return; }

  const sheetBtn = e.target.closest("[data-open-sheet]");
  if (sheetBtn) { openSheet(sheetBtn.dataset.openSheet); return; }

  const start = e.target.closest("[data-start]");
  if (start) { post("/api/run", start.dataset.start); return; }

  const replay = e.target.closest("[data-replay]");
  if (replay) { post("/api/run", replay.dataset.replay); return; }

  const killInst = e.target.closest("[data-killinst]");
  if (killInst) { post("/api/kill", killInst.dataset.killinst); return; }

  const stopAllBtn = e.target.closest("[data-stopall]");
  if (stopAllBtn) {
    const c = appById(currentId);
    if (c) stopAll(c);
    return;
  }

  const front = e.target.closest("[data-front]");
  if (front) { post("/api/front", front.dataset.front); return; }

  const openBtn = e.target.closest("[data-open]");
  if (openBtn) { window.open(openBtn.dataset.open, "_blank"); return; }

  const pinBtn = e.target.closest("[data-pin]");
  if (pinBtn) { setPinned(pinBtn.dataset.pin, pinBtn.dataset.next === "1"); return; }

  const logBtn = e.target.closest("[data-logkey]");
  if (logBtn) {
    window.open("/api/log?key=" + encodeURIComponent(logBtn.dataset.logkey) + "&tail=300", "_blank");
    return;
  }

  const proj = e.target.closest("[data-proj]");
  if (proj) {
    const i = screenNames().indexOf(proj.dataset.proj);
    if (i >= 0) goScreen(i);
    return;
  }

  const dot = e.target.closest("[data-screen]");
  if (dot) { goScreen(+dot.dataset.screen); return; }
});

document.addEventListener("keydown", e => {
  if (e.isComposing) return;
  if (e.key === "Escape") {
    if (searchOpen) { e.preventDefault(); closeSearch(); return; }
    if (histOpen) { e.preventDefault(); closeHist(); return; }
    if (document.body.dataset.w === "wide") popStack("app");
    else popStack(sheet ? "sheet" : "app");
    return;
  }
  if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey && (e.key === "k" || e.key === "K")) {
    e.preventDefault();
    openSearch();
    return;
  }
  if (searchOpen) {
    if (e.key === "ArrowDown") { e.preventDefault(); moveSearch(1); }
    else if (e.key === "ArrowUp") { e.preventDefault(); moveSearch(-1); }
    else if (e.key === "Enter") { e.preventDefault(); openSelectedSearch(); }
    return;
  }
  if (histOpen) return;
  if (e.ctrlKey || e.metaKey || e.altKey) return;
  const ae = document.activeElement;
  const tag = (ae && ae.tagName) || "";
  const editing = tag === "INPUT" || tag === "TEXTAREA" || !!(ae && ae.isContentEditable);
  // 直接输入即搜索：任意可打印字符打开「全部」搜索浮层（不改变右侧）
  if (!editing) {
    if (e.key === "/") { e.preventDefault(); openSearch(""); return; }
    if (e.key.length === 1 && e.key !== " ") { e.preventDefault(); openSearch(e.key); return; }
  }
  if (route === "home" && !sheet) {
    if (editing) return;
    if (e.key === "ArrowRight" || e.key === "PageDown") { e.preventDefault(); goScreen(screenIndex() + 1); }
    if (e.key === "ArrowLeft" || e.key === "PageUp") { e.preventDefault(); goScreen(screenIndex() - 1); }
  }
});

$("#launcherwrap").addEventListener("wheel", onLauncherWheel, {passive: true});
$("#work").addEventListener("wheel", onHomeWheel, {passive: false});

$("#sq").addEventListener("input", () => {
  searchIndex = 0;
  renderSearchResults();
  $("#sresults").scrollTop = 0;
});

$("#refresh").addEventListener("click", load);
$("#histbtn").addEventListener("click", openHist);
$("#histclose").addEventListener("click", closeHist);
$("#searchbtn").addEventListener("click", () => openSearch());
$("#lsearchbtn").addEventListener("click", () => openSearch());
$("#sclose").addEventListener("click", closeSearch);

/* ---------- 响应式：宽=水平铺开，窄=只显示栈顶 ---------- */
function applyWidth() {
  const w = $("#work").clientWidth || document.documentElement.clientWidth;
  // 分档看的是「右半区」还剩多少，而不是整窗：
  //   右半 >= 820 → 记录 + 日志并排；>=600 → 日志盖在右半上；再窄 → 手机式整屏
  const rail = w >= 1500;
  const sideW = rail ? 216 : 0;
  const launcherW = Math.min(462, Math.max(300, w * 0.29));
  const rightW = w - sideW - launcherW;
  const mode = rightW >= 820 ? "wide" : (rightW >= 600 ? "mid" : "narrow");
  document.body.dataset.w = mode;
  document.body.dataset.rail = rail ? "1" : "0";

  const appW = rightW - (mode === "wide" ? 340 : 0);
  document.body.dataset.compact = appW < 560 ? "1" : "0";

  // 布局一变宽，且当前在命令页：直接把日志铺到右边
  const c = appById(currentId);
  if (mode === "wide" && route === "app" && !sheet && c && runsOf(c).length) {
    sheet = "log";
    syncSheet();
  }
  renderApp();
  renderPane();
}

document.addEventListener("visibilitychange", () => {
  if (!document.hidden) load();
});

renderProjects();
renderLauncher();
new ResizeObserver(applyWidth).observe($("#work"));
applyWidth();
load();
setInterval(() => { if (!document.hidden) load(); }, 3000);

// 运行时长每秒就地刷新：不重绘表格，也不影响滚动位置
setInterval(() => {
  document.querySelectorAll("[data-since]").forEach(el => {
    const t = dur(el.dataset.since);
    if (el.textContent !== t) el.textContent = t;
  });
}, 1000);
</script>
</body>
</html>
""";
}
