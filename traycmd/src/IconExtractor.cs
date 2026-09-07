// ============================================================================
// quickcmd — IconExtractor.cs: 图标提取
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
// 图标提取器
// ---------------------------------------------------------------------------
internal static class IconExtractor
{
    public static Icon GetIcon(CommandDef c)
    {
        try
        {
            string exePath = null;

            // 优先使用 program 字段
            if (!string.IsNullOrWhiteSpace(c.Program))
            {
                exePath = Launcher.Expand(c.Program.Trim());
            }
            // 其次使用 shell-script 的解释器
            else if (!string.IsNullOrWhiteSpace(c.ShellScript))
            {
                var script = Launcher.Expand(c.ShellScript.Trim());
                var ext = Path.GetExtension(script).ToLowerInvariant();
                switch (ext)
                {
                    case ".py":
                    case ".pyw":
                        exePath = "python.exe";
                        break;
                    case ".bat":
                    case ".cmd":
                        exePath = "cmd.exe";
                        break;
                    case ".ps1":
                        exePath = "powershell.exe";
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(exePath))
                return null;

            // 如果不是完整路径，尝试从 PATH 查找
            if (!File.Exists(exePath))
            {
                var path = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in path.Split(';'))
                {
                    if (dir.Trim().Length > 0)
                    {
                        var fullPath = Path.Combine(dir.Trim(), exePath);
                        if (File.Exists(fullPath))
                        {
                            exePath = fullPath;
                            break;
                        }
                    }
                }
            }

            if (!File.Exists(exePath))
                return null;

            return Icon.ExtractAssociatedIcon(exePath);
        }
        catch
        {
            return null;
        }
    }
}

