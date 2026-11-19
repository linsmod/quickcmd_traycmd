// ============================================================================
// quickcmd — Store.cs: JSONL 存储
// ============================================================================

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace QuickCmd;

// ---------------------------------------------------------------------------
// JSONL 存储
// ---------------------------------------------------------------------------
internal static class Store
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "quickcmd");

    public static string FilePath => Path.Combine(Dir, "commands.jsonl");

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static List<CommandDef> Load(out int badLines)
    {
        badLines = 0;
        var list = new List<CommandDef>();
        if (!File.Exists(FilePath)) return list;

        foreach (var line in File.ReadLines(FilePath))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            try
            {
                var c = JsonSerializer.Deserialize<CommandDef>(t, JsonOpts);
                if (c == null) { badLines++; continue; }
                // 兜底修正（手工编辑过的文件）
                if (string.IsNullOrWhiteSpace(c.Id)) c.Id = Guid.NewGuid().ToString("N");
                if (c.MaxInstances < 1) c.MaxInstances = 1;
                if (c.Title == null) c.Title = "";
                if (c.Env == null) c.Env = new List<string>();
                MigrateLegacy(c);
                list.Add(c);
            }
            catch { badLines++; }
        }
        return list;
    }

    // 旧版 command_text/command_file/shell 字段 → program / shell-script
    private static void MigrateLegacy(CommandDef c)
    {
        if (c.Extra == null || c.Extra.Count == 0) return;
        if (string.IsNullOrWhiteSpace(c.Program) && string.IsNullOrWhiteSpace(c.ShellScript))
        {
            if (c.Extra.TryGetValue("commandFile", out var cf) && cf.ValueKind == JsonValueKind.String)
            {
                var f = cf.GetString() ?? "";
                var ext = Path.GetExtension(f ?? "").ToLowerInvariant();
                if (ext == ".py" || ext == ".pyw" || ext == ".bat" || ext == ".cmd" || ext == ".ps1")
                    c.ShellScript = f;
                else
                    c.Program = f;
            }
            else if (c.Extra.TryGetValue("commandText", out var ct) && ct.ValueKind == JsonValueKind.String)
            {
                c.Program = ct.GetString() ?? ""; // 经 cmd 兜底路径执行，行为等价旧内联命令
            }
        }
        c.Extra = null; // 下次 SaveAll 时写出的即为干净的新格式
    }

    public static void SaveAll(IEnumerable<CommandDef> list)
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        var sb = new StringBuilder();
        foreach (var c in list) sb.AppendLine(JsonSerializer.Serialize(c, JsonOpts));
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
        File.Move(tmp, FilePath, true); // 原子替换，避免 CLI / qchost 读到半截文件
    }

    // 读-改-写 串行化：CLI 的 add/update/delete 都是 Load → 修改 → SaveAll，
    // 两个进程（CLI / 面板）同时写会互相覆盖。用命名互斥体把整段包起来；
    // 上一个持有者崩溃时 WaitOne 抛 AbandonedMutexException，视为已获得锁。
    public static T WithWriteLock<T>(Func<T> body)
    {
        using var mutex = new Mutex(false, MutexName);
        var locked = false;
        try
        {
            try { locked = mutex.WaitOne(10000); }
            catch (AbandonedMutexException) { locked = true; }
            return body();
        }
        finally { if (locked) { try { mutex.ReleaseMutex(); } catch { } } }
    }

    private const string MutexName = @"Local\quickcmd-store";

    public static void EnsureFile()
    {
        Directory.CreateDirectory(Dir);
        if (!File.Exists(FilePath)) File.WriteAllText(FilePath, "", new UTF8Encoding(false));
    }

    // ----- 应用设置（settings.json：控制台端口 + 首页固定项）-----

    public static string SettingsPath => Path.Combine(Dir, "settings.json");

    public const int DefaultUiPort = 41777;

    public static AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOpts);
            if (s == null) return new AppSettings();
            if (s.UiPort <= 1024 || s.UiPort >= 65536) s.UiPort = DefaultUiPort;
            s.Pinned ??= new List<string>();
            return s;
        }
        catch { return new AppSettings(); }
    }

    public static void SaveSettings(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, JsonOpts), new UTF8Encoding(false));
            File.Move(tmp, SettingsPath, true); // 原子替换
        }
        catch { }
    }

    public static int LoadUiPort() => LoadSettings().UiPort;

    // 保留固定项：端口顺延写回时不能把 pinned 覆盖掉
    public static void SaveUiPort(int port) => WithWriteLock(() =>
    {
        var s = LoadSettings();
        s.UiPort = port;
        SaveSettings(s);
        return 0;
    });

    // 首页固定项：shortId 列表（按固定顺序显示）。返回最新列表。
    public static List<string> SetPinned(string shortId, bool pinned) => WithWriteLock(() =>
    {
        var s = LoadSettings();
        var list = s.Pinned ?? new List<string>();
        list.RemoveAll(x => string.Equals(x, shortId, StringComparison.OrdinalIgnoreCase));
        if (pinned) list.Add(shortId);
        s.Pinned = list;
        SaveSettings(s);
        return list;
    });
}

internal sealed class AppSettings
{
    public int UiPort { get; set; } = Store.DefaultUiPort;
    public List<string> Pinned { get; set; } = new(); // 首页固定项的 ShortId
}

