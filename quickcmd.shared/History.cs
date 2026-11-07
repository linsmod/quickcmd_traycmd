// ============================================================================
// quickcmd — History.cs: 执行历史（审计）JSONL 存储
//   %APPDATA%\quickcmd\history.jsonl
//   写入者：qchost（执行事件）与 CLI（本地兜底 / 宿主不可用）；
//   读侧：CLI history、托盘“历史记录”菜单。追加用命名互斥体串行化。
// ============================================================================

using System.Text;
using System.Text.Json;

namespace QuickCmd;

internal static class History
{
    public static string FilePath => Path.Combine(Store.Dir, "history.jsonl");

    private const string MutexName = @"Local\quickcmd-history";

    public static bool Append(HistoryEntry e, out string error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(Store.Dir);
            var line = JsonSerializer.Serialize(e, Store.JsonOpts) + "\n";
            using var mutex = new Mutex(false, MutexName);
            var locked = false;
            try { locked = mutex.WaitOne(3000); } catch (AbandonedMutexException) { locked = true; }
            try
            {
                // FileShare.Read：history 读取（CLI/托盘）永远不被追加阻塞；
                // 追加本身由互斥体串行化，不会写出半行。
                using var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                var bytes = Encoding.UTF8.GetBytes(line);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush();
            }
            finally { if (locked) mutex.ReleaseMutex(); }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    // 读取最近的事件：坏行跳过（审计文件允许被手改/截断），grep 对命令行/title/status
    // 做不区分大小写的包含匹配；limit<=0 = 全部。返回时间顺序（旧 → 新）。
    public static List<HistoryEntry> Read(int limit, string grep, out int badLines)
    {
        badLines = 0;
        var list = new List<HistoryEntry>();
        if (!File.Exists(FilePath)) return list;

        string[] lines;
        try { lines = File.ReadAllLines(FilePath); }
        catch { return list; }

        foreach (var raw in lines)
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            try
            {
                var e = JsonSerializer.Deserialize<HistoryEntry>(t, Store.JsonOpts);
                if (e == null) { badLines++; continue; }
                if (!string.IsNullOrWhiteSpace(grep))
                {
                    var hay = (e.CommandLine + " " + e.Title + " " + e.Status + " " + e.Source + " " + e.ShortId);
                    if (hay.IndexOf(grep, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
                list.Add(e);
            }
            catch { badLines++; }
        }

        if (limit > 0 && list.Count > limit)
            list = list.Skip(list.Count - limit).ToList();
        return list;
    }

    // 清空历史（审计文件的显式操作）。调用方负责确认（CLI 用 -y，托盘弹框）。
    public static bool Clear(out string error)
    {
        error = null;
        try
        {
            using var mutex = new Mutex(false, MutexName);
            var locked = false;
            try { locked = mutex.WaitOne(3000); } catch (AbandonedMutexException) { locked = true; }
            try
            {
                Directory.CreateDirectory(Store.Dir);
                File.WriteAllText(FilePath, "", new UTF8Encoding(false));
            }
            finally { if (locked) mutex.ReleaseMutex(); }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    // 注册项/直跑命令的“实际命令行”文本（审计展示与判据）
    public static string CommandLineOf(CommandDef c)
    {
        if (c == null) return "";
        var args = c.Args ?? "";
        if (!string.IsNullOrWhiteSpace(c.ShellScript))
            return c.ShellScript + (args.Length > 0 ? " " + args : "");
        return (c.Program ?? "") + (args.Length > 0 ? " " + args : "");
    }

    // 给 CLI / 托盘共用的状态文案
    public static string StatusText(string status) => status switch
    {
        "started" => "已启动",
        "exited" => "已退出",
        "killed" => "已结束",
        "failed" => "失败",
        "max" => "超实例上限",
        "host-unavailable" => "宿主不可用",
        "local" => "本地直启",
        _ => status ?? "",
    };
}
