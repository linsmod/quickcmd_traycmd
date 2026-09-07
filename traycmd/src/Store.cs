// ============================================================================
// quickcmd — Store.cs: JSONL 存储
// ============================================================================

using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

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

    public static void EnsureFile()
    {
        Directory.CreateDirectory(Dir);
        if (!File.Exists(FilePath)) File.WriteAllText(FilePath, "", new UTF8Encoding(false));
    }
}

