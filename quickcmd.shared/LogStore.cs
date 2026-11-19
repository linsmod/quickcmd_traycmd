// ============================================================================
// quickcmd — LogStore.cs: 命令输出日志（%APPDATA%\quickcmd\logs）
//   CaptureLogs 的命令启动时把 stdout/stderr 落到 <key>-<时间>.log：
//     · qchost（JOB 模式）→ 给子进程一个可继承的文件句柄，child 直接写文件
//       （无管道、无中继线程，不会因为缓冲区满把服务卡死）；
//     · CLI 本地兜底 / PID 模式 → Process 重定向 + 中继线程（同一文件，UTF-8）。
//   读取（quickcmd logs / 控制台）走 Tail：从文件尾部取，UTF-8 失败按 GBK 解。
// ============================================================================

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace QuickCmd;

internal static class LogStore
{
    public static string Dir => Path.Combine(Store.Dir, "logs");

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    static LogStore()
    {
        // .NET Core 默认不带 GBK(936)；日志里混 cmd / 老工具输出时需要
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
    }

    // 文件名前缀：注册项用 short id；exec 直跑用 exec-<8位>（adhoc- 前缀相同，会撞）
    public static string Key(CommandDef c)
    {
        var id = c?.Id ?? "";
        if (id.StartsWith("adhoc-", StringComparison.OrdinalIgnoreCase))
            return "exec-" + (id.Length > 14 ? id.Substring(6, 8) : "unknown");
        return string.IsNullOrWhiteSpace(c?.ShortId) ? "cmd" : c.ShortId;
    }

    // 每次执行一个文件：<key>-yyyyMMdd-HHmmss-fff.log
    public static string NewPath(CommandDef c)
    {
        try { Directory.CreateDirectory(Dir); } catch { }
        return Path.Combine(Dir, Key(c) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".log");
    }

    // 整个目录里最新的若干日志（新 → 旧）
    public static List<LogFile> List(int limit)
    {
        var result = new List<LogFile>();
        try
        {
            if (!Directory.Exists(Dir)) return result;
            foreach (var f in new DirectoryInfo(Dir).GetFiles("*.log"))
            {
                result.Add(new LogFile
                {
                    Path = f.FullName,
                    Name = f.Name,
                    Size = f.Length,
                    Modified = f.LastWriteTime,
                });
            }
        }
        catch { }
        result.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        if (limit > 0 && result.Count > limit) result = result.Take(limit).ToList();
        return result;
    }

    // 某个命令（short id / exec-xxxx）最新的日志；没有返回 null
    public static LogFile Latest(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var all = List(0);
        var prefix = key.Trim();
        foreach (var f in all) // 已按时间新→旧
            if (f.Name.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))
                return f;
        return null;
    }

    public static bool Exists(string path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    private static LogFile Describe(string path) => new()
    {
        Path = path,
        Name = Path.GetFileName(path),
        Size = new FileInfo(path).Length,
        Modified = File.GetLastWriteTime(path),
    };

    // key 解析（CLI / 控制台共用）：
    //   命令（short-id / 完整 id / title）→ 该命令最新的日志
    //   实例 ID（job-N / pid）        → 这次执行的日志（从审计历史反查）
    //   文件路径 / 文件名             → 直接当日志文件
    public static LogFile Resolve(string key, out string why)
    {
        why = "";
        key = (key ?? "").Trim();
        if (key.Length == 0) { why = "缺少 key"; return null; }

        if (File.Exists(key)) return Describe(Path.GetFullPath(key));

        var list = Store.Load(out _);
        var c = Resolver.Resolve(key, list, out _);
        if (c != null)
        {
            var f = Latest(Key(c));
            if (f != null) return f;
            why = "「" + c.Title + "」还没有日志。开启捕获后再运行: quickcmd update "
                  + c.ShortId + " --capture-logs";
            return null;
        }

        var entries = History.Read(0, null, out _);
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            if (!string.Equals(e.InstanceId, key, StringComparison.OrdinalIgnoreCase)) continue;
            if (Exists(e.LogPath)) return Describe(e.LogPath);
            if (e.InstanceId.Length > 0)
            {
                why = "实例 " + key + " 没有开启日志捕获（命令需 --capture-logs）";
                return null;
            }
        }

        var byName = Latest(key);
        if (byName != null) return byName;
        if (File.Exists(Path.Combine(Dir, key))) return Describe(Path.Combine(Dir, key));

        why = "找不到与「" + key + "」相关的日志（可用 quickcmd logs 列出最近的日志）";
        return null;
    }

    // ------------------------------------------------------------------
    // 子进程直接写文件：可继承句柄
    // ------------------------------------------------------------------

    // 追加写句柄（FILE_APPEND_DATA：多个句柄同时写也不会互相覆盖）
    public static IntPtr OpenInheritableAppend(string path)
    {
        var sa = new Native.SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<Native.SECURITY_ATTRIBUTES>(),
            bInheritHandle = true,
        };
        var h = Native.CreateFileW(path, Native.FILE_APPEND_DATA,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE | Native.FILE_SHARE_DELETE,
            ref sa, Native.OPEN_ALWAYS, Native.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        return h == IntPtr.Zero || h == (IntPtr)(-1) ? IntPtr.Zero : h;
    }

    // 只读句柄（stdin 用 NUL 设备：捕获输出时命令不该再等键盘输入）
    public static IntPtr OpenInheritableRead(string path)
    {
        var sa = new Native.SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<Native.SECURITY_ATTRIBUTES>(),
            bInheritHandle = true,
        };
        var h = Native.CreateFileW(path, Native.GENERIC_READ,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            ref sa, Native.OPEN_EXISTING, Native.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        return h == IntPtr.Zero || h == (IntPtr)(-1) ? IntPtr.Zero : h;
    }

    // ------------------------------------------------------------------
    // Process 重定向 → 同一文件（PID 模式 / CLI 本地兜底）
    //   两个后台线程各自读到 EOF，最后一个负责关闭文件。
    // ------------------------------------------------------------------
    public static void Pump(Process p, string path)
    {
        if (p == null || string.IsNullOrWhiteSpace(path)) return;
        FileStream fs;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            fs = new FileStream(path, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete, 4096);
        }
        catch { return; }

        var gate = new object();
        var remaining = 2;

        void Drain(StreamReader reader)
        {
            var buf = new char[8192];
            try
            {
                int n;
                while ((n = reader.Read(buf, 0, buf.Length)) > 0)
                {
                    var bytes = Encoding.UTF8.GetBytes(buf, 0, n);
                    lock (gate) { try { fs.Write(bytes, 0, bytes.Length); fs.Flush(); } catch { } }
                }
            }
            catch { }
            if (Interlocked.Decrement(ref remaining) == 0)
                lock (gate) { try { fs.Dispose(); } catch { } }
        }

        try
        {
            var o = p.StandardOutput;
            var e = p.StandardError;
            new Thread(() => Drain(o)) { IsBackground = true, Name = "quickcmd-log-out" }.Start();
            new Thread(() => Drain(e)) { IsBackground = true, Name = "quickcmd-log-err" }.Start();
        }
        catch { try { fs.Dispose(); } catch { } }
    }

    // ------------------------------------------------------------------
    // 读取
    // ------------------------------------------------------------------

    private const int TailMaxBytes = 512 * 1024;

    // 取文件末尾 n 行（n<=0 = 尽力多取）。读不到返回 ""。
    // 从中间截断时丢掉首行残片；UTF-8 严格解码失败则按 GBK（cmd / 老工具）。
    public static string Tail(string path, int n)
    {
        if (!Exists(path)) return "";
        byte[] bytes;
        bool partial;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var len = (int)Math.Min(fs.Length, TailMaxBytes);
            partial = fs.Length > len;
            bytes = new byte[len];
            fs.Seek(-len, SeekOrigin.End);
            var read = 0;
            while (read < len)
            {
                var got = fs.Read(bytes, read, len - read);
                if (got <= 0) break;
                read += got;
            }
            if (read < len) Array.Resize(ref bytes, read);
        }
        catch { return ""; }

        var text = Decode(bytes);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        if (partial && lines.Count > 0) lines.RemoveAt(0); // 首行可能是半个字符/半行
        while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (n > 0 && lines.Count > n) lines = lines.Skip(lines.Count - n).ToList();
        return string.Join(Environment.NewLine, lines);
    }

    private static string Decode(byte[] bytes)
    {
        try { return StrictUtf8.GetString(bytes); } catch { }
        try { return Encoding.GetEncoding(936).GetString(bytes); } catch { }
        return Encoding.UTF8.GetString(bytes);
    }

    // 清理：超过 keepDays 天的日志删掉；总数上限 keepCount（保留最新）。
    // 每次新建日志时调用，best-effort。
    public static void Prune(int keepDays = 14, int keepCount = 300)
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            var files = new DirectoryInfo(Dir).GetFiles("*.log");
            Array.Sort(files, (a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
            var cutoff = DateTime.Now.AddDays(-keepDays);
            for (int i = 0; i < files.Length; i++)
            {
                if (i < keepCount && files[i].LastWriteTime >= cutoff) continue;
                try { files[i].Delete(); } catch { }
            }
        }
        catch { }
    }
}

internal sealed class LogFile
{
    public string Path { get; set; }
    public string Name { get; set; }
    public long Size { get; set; }
    public DateTime Modified { get; set; }

    public string SizeText =>
        Size < 1024 ? Size + " B"
        : Size < 1024 * 1024 ? (Size / 1024) + " KB"
        : (Size / 1024.0 / 1024.0).ToString("0.0") + " MB";
}
