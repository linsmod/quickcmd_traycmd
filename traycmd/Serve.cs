// ============================================================================
// quickcmd — Serve.cs: `quickcmd serve` 内置静态文件服务（前台运行，被 kill 即停）
//   默认托管当前工作目录（注册为 web 命令时 --workdir 就是站点根目录）。
//   只绑 127.0.0.1；支持 GET/HEAD、Range、ETag/Last-Modified、index.html、
//   --spa 前端路由回退；拒绝路径逃逸，默认不列目录。
// ============================================================================

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace QuickCmd;

internal static class StaticServer
{
    public static int Run(string[] a)
    {
        string dir = null;
        int port = 0;
        bool spa = false;

        for (int i = 0; i < a.Length; i++)
        {
            var t = a[i];
            var eq = t.IndexOf('=');
            var key = (eq > 0 ? t.Substring(0, eq) : t).ToLowerInvariant();
            var val = eq > 0 ? t.Substring(eq + 1) : null;

            switch (key)
            {
                case "--dir":
                    dir = val ?? NextValue(a, ref i);
                    if (string.IsNullOrWhiteSpace(dir)) { Console.WriteLine("错误: --dir 缺少目录"); return 2; }
                    break;
                case "--port":
                    var pv = val ?? NextValue(a, ref i);
                    if (!int.TryParse(pv, out port) || port < 1 || port > 65535)
                    {
                        Console.WriteLine("错误: --port 需要 1-65535 的整数，实际: " + pv);
                        return 2;
                    }
                    break;
                case "--spa":
                    spa = true;
                    break;
                case "-h":
                case "--help":
                    Help();
                    return 0;
                default:
                    Console.WriteLine("错误: 未知选项 " + t + "（quickcmd serve --help 查看用法）");
                    return 2;
            }
        }

        if (port == 0)
        {
            Console.WriteLine("错误: 必须指定 --port（例: quickcmd serve --port 41001）");
            return 2;
        }

        string root;
        try { root = Path.GetFullPath(dir ?? Directory.GetCurrentDirectory()); }
        catch (Exception ex) { Console.WriteLine("错误: 目录无效: " + ex.Message); return 2; }
        if (!Directory.Exists(root))
        {
            Console.WriteLine("错误: 目录不存在: " + root);
            return 2;
        }
        if (!File.Exists(Path.Combine(root, "index.html")))
            Console.WriteLine("⚠ 提示: 根目录没有 index.html（直接访问 / 会 404）");

        var listener = new TcpListener(IPAddress.Loopback, port);
        try { listener.Start(); }
        catch (SocketException ex)
        {
            Console.WriteLine("错误: 无法监听 127.0.0.1:" + port + "（端口被占用？）— " + ex.Message);
            return 1;
        }

        var url = "http://127.0.0.1:" + port + "/";
        Console.WriteLine("服务目录: " + root + (spa ? "  (SPA 回退开启)" : ""));
        Console.WriteLine("LISTENING " + url);
        Console.WriteLine("按 Ctrl+C 停止");

        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            try { listener.Stop(); } catch { }
        };

        try
        {
            while (true)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch { break; } // Stop() 或 Ctrl+C

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Handle(client, root, spa); }
                    catch { }
                    finally { try { client.Close(); } catch { } }
                });
            }
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }

        Console.WriteLine("已停止。");
        return 0;
    }

    private static string NextValue(string[] a, ref int i) =>
        i + 1 < a.Length ? a[++i] : "";

    private static void Handle(TcpClient client, string root, bool spa)
    {
        using var ns = client.GetStream();
        var head = ReadHead(ns);
        if (head == null) return;

        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 2) { WriteText(ns, "400 Bad Request", "bad request"); return; }

        var method = parts[0].ToUpperInvariant();
        var target = parts[1];

        if (method != "GET" && method != "HEAD")
        {
            WriteText(ns, "405 Method Not Allowed", "only GET/HEAD supported");
            return;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) break;
            var c = line.IndexOf(':');
            if (c > 0) headers[line.Substring(0, c).Trim()] = line.Substring(c + 1).Trim();
        }

        var q = target.IndexOf('?');
        var rawPath = q >= 0 ? target.Substring(0, q) : target;
        string rel;
        try { rel = Uri.UnescapeDataString(rawPath); }
        catch { rel = rawPath; }
        if (rel.IndexOf('\0') >= 0) { WriteText(ns, "400 Bad Request", "bad path"); return; }

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(root, rel.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar)));
        }
        catch { WriteText(ns, "400 Bad Request", "bad path"); return; }

        if (!InRoot(root, full)) { WriteText(ns, "404 Not Found", "not found"); return; }

        if (Directory.Exists(full)) full = Path.Combine(full, "index.html");

        if (!File.Exists(full))
        {
            // SPA 回退：无扩展名的前端路由交给 index.html；静态资源缺失仍然 404
            if (spa && !Path.HasExtension(rel))
            {
                full = Path.Combine(root, "index.html");
                if (!File.Exists(full)) { WriteText(ns, "404 Not Found", "not found"); return; }
            }
            else { WriteText(ns, "404 Not Found", "not found", rawPath); return; }
        }

        ServeFile(ns, method, full, rawPath, headers);
    }

    private static void ServeFile(NetworkStream ns, string method, string path, string logPath,
        Dictionary<string, string> headers)
    {
        FileStream fs;
        try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch { WriteText(ns, "403 Forbidden", "access denied", logPath); return; }

        using (fs)
        {
            var len = fs.Length;
            var lastMod = File.GetLastWriteTimeUtc(path);
            var etag = "\"" + len.ToString("x") + "-" + lastMod.Ticks.ToString("x") + "\"";

            if (headers.TryGetValue("if-none-match", out var inm) && inm == etag)
            {
                WriteHead(ns, "304 Not Modified", null, 0, 0, 0, 0, etag, lastMod);
                Log(304, method, logPath);
                return;
            }

            long start = 0, end = len - 1;
            bool partial = false;
            if (headers.TryGetValue("range", out var rangeHeader) &&
                rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                var spec = rangeHeader.Substring(6).Split(',')[0].Trim();
                var dash = spec.IndexOf('-');
                if (dash >= 0)
                {
                    var s = spec.Substring(0, dash).Trim();
                    var e = spec.Substring(dash + 1).Trim();
                    if (s.Length == 0)
                    {
                        if (!long.TryParse(e, out var suffix) || suffix <= 0) { WriteRangeNotSatisfiable(ns, len); return; }
                        start = Math.Max(0, len - suffix);
                        end = len - 1;
                    }
                    else
                    {
                        if (!long.TryParse(s, out start) || start < 0 || start >= len) { WriteRangeNotSatisfiable(ns, len); return; }
                        if (e.Length == 0) end = len - 1;
                        else if (!long.TryParse(e, out end) || end < start) { WriteRangeNotSatisfiable(ns, len); return; }
                        if (end >= len) end = len - 1;
                    }
                    partial = true;
                }
            }

            var bodyLen = end - start + 1;
            var status = partial ? "206 Partial Content" : "200 OK";
            WriteHead(ns, status, Mime(path), bodyLen, start, end, len, etag, lastMod, partial);

            if (method != "HEAD" && bodyLen > 0)
            {
                fs.Seek(start, SeekOrigin.Begin);
                var buf = new byte[64 * 1024];
                var left = bodyLen;
                while (left > 0)
                {
                    var n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left));
                    if (n <= 0) break;
                    ns.Write(buf, 0, n);
                    left -= n;
                }
                ns.Flush();
            }
            Log(partial ? 206 : 200, method, logPath);
        }
    }

    // 读取请求头（到空行为止），上限 16KB，避免慢速/超大请求占资源
    private static string ReadHead(NetworkStream ns)
    {
        var buf = new byte[4096];
        var sb = new StringBuilder();
        while (sb.Length < 16 * 1024)
        {
            int n;
            try { n = ns.Read(buf, 0, buf.Length); }
            catch { return null; }
            if (n <= 0) break;
            sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            if (sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        var text = sb.ToString();
        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return end >= 0 ? text.Substring(0, end + 4) : text;
    }

    private static bool InRoot(string root, string full)
    {
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteText(NetworkStream ns, string status, string text, string logPath = null)
    {
        var body = Encoding.UTF8.GetBytes(text + "\n");
        WriteHead(ns, status, null, body.Length, 0, 0, 0, null, null);
        try
        {
            ns.Write(body, 0, body.Length);
            ns.Flush();
        }
        catch { }
        if (logPath != null) Log(int.Parse(status.Substring(0, 3)), "GET", logPath);
    }

    private static void WriteRangeNotSatisfiable(NetworkStream ns, long total)
    {
        var body = Encoding.UTF8.GetBytes("range not satisfiable\n");
        var head = "HTTP/1.1 416 Range Not Satisfiable\r\n"
            + "Date: " + DateTime.UtcNow.ToString("R") + "\r\n"
            + "Server: quickcmd-serve\r\n"
            + "Content-Type: text/plain; charset=utf-8\r\n"
            + "Content-Length: " + body.Length + "\r\n"
            + "Content-Range: bytes */" + total + "\r\n"
            + "Connection: close\r\n\r\n";
        try
        {
            var b = Encoding.ASCII.GetBytes(head);
            ns.Write(b, 0, b.Length);
            ns.Write(body, 0, body.Length);
            ns.Flush();
        }
        catch { }
    }

    private static void WriteHead(NetworkStream ns, string status, string contentType, long bodyLen,
        long rangeStart, long rangeEnd, long total, string etag, DateTime? lastMod,
        bool partial = false)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append("\r\n");
        sb.Append("Date: ").Append(DateTime.UtcNow.ToString("R")).Append("\r\n");
        sb.Append("Server: quickcmd-serve\r\n");
        if (contentType != null)
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(bodyLen).Append("\r\n");
        if (partial)
            sb.Append("Content-Range: bytes ").Append(rangeStart).Append('-')
              .Append(rangeEnd).Append('/').Append(total).Append("\r\n");
        if (etag != null) sb.Append("ETag: ").Append(etag).Append("\r\n");
        if (lastMod != null) sb.Append("Last-Modified: ").Append(lastMod.Value.ToString("R")).Append("\r\n");
        sb.Append("Accept-Ranges: bytes\r\n");
        sb.Append("Cache-Control: no-cache\r\n");
        sb.Append("X-Content-Type-Options: nosniff\r\n");
        sb.Append("Connection: close\r\n\r\n");

        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        try { ns.Write(bytes, 0, bytes.Length); ns.Flush(); } catch { }
    }

    private static void Log(int status, string method, string path)
    {
        try { Console.WriteLine(status + " " + method + " " + path); } catch { }
    }

    private static string Mime(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".json" or ".map" or ".webmanifest" => "application/json; charset=utf-8",
            ".txt" or ".md" or ".log" => "text/plain; charset=utf-8",
            ".csv" => "text/csv; charset=utf-8",
            ".xml" => "application/xml; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".ico" => "image/x-icon",
            ".avif" => "image/avif",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            ".wasm" => "application/wasm",
            ".pdf" => "application/pdf",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".zip" => "application/zip",
            _ => "application/octet-stream",
        };
    }

    private static void Help()
    {
        Console.WriteLine("""
用法: quickcmd serve [--dir <目录>] --port <端口> [--spa]

  --dir <目录>   托管目录（默认 = 当前工作目录；注册为 web 命令时即 --workdir）
  --port <端口>  监听端口（必填，仅绑定 127.0.0.1）
  --spa          前端路由回退：无扩展名的未命中路径返回 index.html

例:
  quickcmd serve --port 41001 --spa
  quickcmd add --type web --title "站点预览" --url http://127.0.0.1:41001/ ^
    --workdir D:\out --program quickcmd --args "serve --port 41001 --spa"
""");
    }
}
