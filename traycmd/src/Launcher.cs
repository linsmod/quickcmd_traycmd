// ============================================================================
// quickcmd — Launcher.cs: 启动器：命令定义 → 真实进程
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
// 启动器：把一条命令定义变成一个真实进程
// ---------------------------------------------------------------------------
internal static class Launcher
{
    public static string Expand(string s) =>
        string.IsNullOrWhiteSpace(s) ? s : Environment.ExpandEnvironmentVariables(s);

    public static string EffectiveWorkdir(CommandDef c)
    {
        var d = Expand(c.Workdir);
        if (string.IsNullOrWhiteSpace(d))
            d = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return d;
    }

    // 启动结果（作业跟踪模式）：Proc 为 null 表示 root 在查询瞬间已退出，
    // 但 Job 非空时孙进程可能仍在，实例依旧存活；Job 为 null = 退化为普通跟踪
    internal sealed class LaunchResult
    {
        public Process Proc;
        public Job Job;
        public int Pid;
    }

    // 非跟踪启动（CLI 在 qchost 未运行时的兜底）：返回 null = 失败（error 为原因）
    public static Process Launch(CommandDef c, out string error)
    {
        var psi = BuildPsi(c, out error);
        if (psi == null) return null;
        bool hasScript = !string.IsNullOrWhiteSpace(c.ShellScript);

        try
        {
            return Process.Start(psi);
        }
        catch (Win32Exception wex) when (!hasScript && (wex.NativeErrorCode == 2 || wex.NativeErrorCode == 3))
        {
            ApplyCmdFallback(psi, c);
            try { return Process.Start(psi); }
            catch (Exception ex) { error = "启动失败: " + ex.Message; return null; }
        }
        catch (Exception ex) { error = "启动失败: " + ex.Message; return null; }
    }

    // 跟踪启动（qchost 路径）。useJob=true（JOB 模式）：进程挂起创建 → 指派进作业对象 →
    // 恢复。作业成员可继承：launcher 自退后孙进程仍在作业内，存活判断与整树击杀不受
    // root pid 生命周期影响。作业不可用（qchost 自身已在别的作业内等）时
    // 自动退化为 PID 模式（Job=null）。
    // useJob=false（PID 模式）：直接 Process.Start，仅按 root pid 及其子孙树跟踪。
    public static LaunchResult LaunchTracked(CommandDef c, bool useJob, out string error)
    {
        var psi = BuildPsi(c, out error);
        if (psi == null) return null;
        bool hasScript = !string.IsNullOrWhiteSpace(c.ShellScript);
        var env = EnvOverrides(c);

        var job = useJob ? Job.Create() : null;
        if (job != null)
        {
            var ok = job.TryLaunch(psi.FileName, psi.Arguments, psi.WorkingDirectory, env, out var pid, out var w32);
            if (!ok && !hasScript && (w32 == 2 || w32 == 3))
            {
                // 找不到目标（如 npm 实际是 npm.cmd）：回退交给 cmd 解析后重试
                ApplyCmdFallback(psi, c);
                ok = job.TryLaunch(psi.FileName, psi.Arguments, psi.WorkingDirectory, env, out pid, out w32);
            }

            if (ok)
            {
                Process proc = null;
                try { proc = Process.GetProcessById(pid); } catch { } // root 秒退：靠 Job 继续跟踪
                return new LaunchResult { Proc = proc, Job = job, Pid = pid };
            }

            job.Dispose(); // 启动失败或无法入作业：走退化路径
        }

        var p = Launch(c, out error);
        return p == null ? null : new LaunchResult { Proc = p, Job = null, Pid = p.Id };
    }

    // 裸命令直接启动找不到（如 npm 实际是 npm.cmd）：交给 cmd 解析 PATH、扩展名与内置命令
    private static void ApplyCmdFallback(ProcessStartInfo psi, CommandDef c)
    {
        var args = Expand(c.Args ?? "") ?? "";
        psi.FileName = "cmd.exe";
        psi.Arguments = "/d /s " + (c.KeepOpen ? "/k" : "/c") + " \""
            + Expand(c.Program.Trim()) + (args.Length > 0 ? " " + args : "") + "\"";
    }

    // 校验命令定义并组装 ProcessStartInfo（解释器解析、环境变量）
    private static ProcessStartInfo BuildPsi(CommandDef c, out string error)
    {
        error = "";
        var workdir = EffectiveWorkdir(c);
        if (!Directory.Exists(workdir)) { error = "执行目录不存在: " + workdir; return null; }

        var hasProg = !string.IsNullOrWhiteSpace(c.Program);
        var hasScript = !string.IsNullOrWhiteSpace(c.ShellScript);
        if (hasProg && hasScript) { error = "program 与 shell-script 只能二选一"; return null; }
        if (!hasProg && !hasScript) { error = "program 和 shell-script 至少要有一个"; return null; }

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            WorkingDirectory = workdir,
        };

        if (hasScript)
        {
            if (!ConfigureScript(c, psi, out error)) return null;
        }
        else
        {
            psi.FileName = Expand(c.Program.Trim());
            psi.Arguments = Expand(c.Args ?? "") ?? "";
        }

        ApplyEnv(c, psi);
        return psi;
    }

    // c.Env（"KEY=VAL"）→ 覆盖字典；VAL 支持 %VAR% 占位符。作业启动路径单独用，
    // 不依赖 ProcessStartInfo 环境语义
    private static Dictionary<string, string> EnvOverrides(CommandDef c)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in c.Env)
        {
            var i = e.IndexOf('=');
            if (i <= 0) continue;
            var k = e.Substring(0, i).Trim();
            if (k.Length > 0) d[k] = Expand(e.Substring(i + 1)) ?? "";
        }
        return d;
    }

    // shell_script：按扩展名选择解释器
    private static bool ConfigureScript(CommandDef c, ProcessStartInfo psi, out string error)
    {
        error = "";
        var script = Expand(c.ShellScript.Trim());
        if (!File.Exists(script)) { error = "找不到脚本文件: " + script; return false; }
        var args = Expand(c.Args ?? "") ?? "";
        var ext = Path.GetExtension(script).ToLowerInvariant();

        switch (ext)
        {
            case ".py":
            case ".pyw":
            {
                // python 多版本：interpreter 覆盖查找顺序（';' 分隔），未指定则落系统 PATH
                var py = ResolveInterpreter(ext == ".pyw" ? "pythonw.exe" : "python.exe", c.Interpreter, out error);
                if (py == null) return false;
                psi.FileName = py;
                // KeepOpen: python -i 让脚本跑完停在交互界面，窗口不闪退
                psi.Arguments = (c.KeepOpen ? "-i " : "") + "\"" + script + "\""
                    + (args.Length > 0 ? " " + args : "");
                return true;
            }

            case ".bat":
            case ".cmd":
                psi.FileName = "cmd.exe";
                psi.Arguments = "/d " + (c.KeepOpen ? "/k" : "/c") + " call \"" + script + "\""
                    + (args.Length > 0 ? " " + args : "");
                return true;

            case ".ps1":
            {
                var shell = ResolveInterpreter("pwsh.exe", c.Interpreter, out error);
                if (shell == null) return false;
                if (shell == "pwsh.exe" && !OnPath("pwsh.exe")) shell = "powershell.exe"; // 未装 pwsh 自动回退
                psi.FileName = shell;
                psi.Arguments = "-NoLogo -ExecutionPolicy Bypass " + (c.KeepOpen ? "-NoExit " : "")
                    + "-File \"" + script + "\""
                    + (args.Length > 0 ? " " + args : "");
                return true;
            }

            default:
                error = "不支持的脚本扩展名 " + (ext.Length == 0 ? "(无扩展名)" : ext)
                    + "（支持 .py .pyw .bat .cmd .ps1）；可执行程序请用 --program";
                return false;
        }
    }

    // 解释器解析：override（';' 分隔的文件/目录，按顺序）→ 系统 PATH
    private static string ResolveInterpreter(string defaultExe, string overrideChain, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(overrideChain)) return defaultExe; // 交给 CreateProcess 搜 PATH
        foreach (var part in overrideChain.Split(';'))
        {
            var p = Expand(part.Trim());
            if (p.Length == 0) continue;
            if (File.Exists(p)) return p;
            if (Directory.Exists(p))
            {
                var cand = Path.Combine(p, defaultExe);
                if (File.Exists(cand)) return cand;
            }
        }
        error = "interpreter 指定的路径都无效: " + overrideChain;
        return null;
    }

    private static bool OnPath(string exe)
    {
        try
        {
            if (File.Exists(exe)) return true;
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(';'))
            {
                if (dir.Trim().Length > 0 && File.Exists(Path.Combine(dir.Trim(), exe))) return true;
            }
        }
        catch { }
        return false;
    }

    private static void ApplyEnv(CommandDef c, ProcessStartInfo psi)
    {
        foreach (var e in c.Env)
        {
            var i = e.IndexOf('=');
            if (i <= 0) continue;
            var k = e.Substring(0, i).Trim();
            var v = Expand(e.Substring(i + 1)) ?? "";
            if (k.Length > 0) psi.EnvironmentVariables[k] = v;
        }
    }
}

