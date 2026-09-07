// ============================================================================
// quickcmd — Resolver.cs: 命令定位（id / short-id / title）
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
// 定位命令：完整 id / 8 位 short id（可更长前缀）/ title
// ---------------------------------------------------------------------------
internal static class Resolver
{
    public static CommandDef Resolve(string key, List<CommandDef> list, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(key)) { error = "缺少 short-id / id / title"; return null; }
        var k = key.Trim();

        var byId = list.FirstOrDefault(c => string.Equals(c.Id, k, StringComparison.OrdinalIgnoreCase));
        if (byId != null) return byId;

        if (k.Length >= 4 && k.Length < 32 && IsHex(k))
        {
            var pref = list.Where(c => c.Id.StartsWith(k, StringComparison.OrdinalIgnoreCase)).ToList();
            if (pref.Count == 1) return pref[0];
            if (pref.Count > 1)
            {
                error = "id 前缀不唯一（匹配 " + pref.Count + " 条），请用更长前缀："
                        + string.Join(", ", pref.Select(c => c.ShortId + " " + c.Title));
                return null;
            }
        }

        var byTitle = list.Where(c => string.Equals(c.Title.Trim(), k, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byTitle.Count == 1) return byTitle[0];
        if (byTitle.Count > 1)
        {
            error = "title「" + k + "」有 " + byTitle.Count + " 条同名，请用 short id："
                    + string.Join(", ", byTitle.Select(c => c.ShortId + " " + c.Title));
            return null;
        }

        error = "找不到命令: " + k;
        return null;
    }

    private static bool IsHex(string s)
    {
        foreach (var ch in s) if (!Uri.IsHexDigit(ch)) return false;
        return true;
    }
}

