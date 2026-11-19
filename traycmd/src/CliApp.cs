// ============================================================================
// quickcmd — CliApp.cs: CLI（带参数启动）：CRUD
// ============================================================================

using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace QuickCmd;

// ---------------------------------------------------------------------------
// CLI（带参数启动）：CRUD
// ---------------------------------------------------------------------------
internal sealed class Options
{
    public Dictionary<string, List<string>> Map = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Positional = new();

    public bool Has(string k) => Map.ContainsKey(k);
    public string Value(string k) => Has(k) ? Map[k].LastOrDefault() : null;
    public List<string> Values(string k) => Has(k) ? Map[k] : null;
}

internal static class CliApp
{
    public static int Run(string[] args)
    {
        if (args.Length == 0) { Help(); return 0; }
        var verb = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();
        switch (verb)
        {
            case "add": return CmdAdd(rest);
            case "list": case "ls": return CmdList(rest);
            case "get": return CmdGet(rest);
            case "update": case "edit": return CmdUpdate(rest);
            case "delete": case "rm": return CmdDelete(rest);
            case "run": return CmdRun(rest, useJob: false);
            case "run-job": return CmdRun(rest, useJob: true);
            case "exec": return CmdExec(rest);
            case "serve": return StaticServer.Run(rest);
            case "url": return CmdUrl(rest);
            case "logs": case "log": return CmdLogs(rest);
            case "ui": return CmdUi(rest);
            case "history": case "hist": return CmdHistory(rest);
            case "host": return CmdHost(rest);
            case "ps": return CmdPs(rest);
            case "kill": return CmdKill(rest);
            case "skill": case "/skill": return CmdSkill();
            case "install-skill": case "/install-skill": return CmdInstallSkill(rest);
            case "path": Console.WriteLine(Store.FilePath); return 0;
            case "help": case "-h": case "--help": Help(); return 0;
            default:
                Console.WriteLine("未知命令: " + verb);
                Console.WriteLine();
                Help();
                return 2;
        }
    }

    // 带值选项：其后紧跟的 token 一律当作值（即使以 - 开头，如 --args "--version"）
    private static readonly HashSet<string> ValueKeys = new(StringComparer.OrdinalIgnoreCase)
    { "title", "proj", "workdir", "env", "program", "shell-script", "args", "interpreter", "max-instances",
      "type", "url",
      "limit", "grep", "tail" };

    private static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "-y" || a == "-Y")
            {
                if (!o.Map.ContainsKey("yes")) o.Map["yes"] = new List<string> { "" };
                continue;
            }
            if (a.StartsWith("--"))
            {
                var key = a.Substring(2);
                string val = null;
                var eq = key.IndexOf('=');
                if (eq >= 0) { val = key.Substring(eq + 1); key = key.Substring(0, eq); }
                if (val == null)
                {
                    if (ValueKeys.Contains(key) && i + 1 < args.Length) val = args[++i];
                    else val = ""; // 开关选项（--json 等）或缺值
                }
                if (!o.Map.TryGetValue(key, out var l)) o.Map[key] = l = new List<string>();
                l.Add(val);
            }
            else o.Positional.Add(a);
        }
        return o;
    }

    private static readonly string[] AddUpdateKeys =
    {
        "title", "proj", "workdir", "env", "program", "shell-script", "args",
        "interpreter", "max-instances", "close-on-exit", "keep-open",
        "type", "url", "auto-start", "no-auto-start",
        "capture-logs", "no-capture-logs",
    };

    // --type：app/空 = 普通程序；web = 网站/常驻服务。返回 null = 非法值
    private static string NormalizeType(string t)
    {
        t = (t ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0 || t == "app") return "";
        return t == "web" ? "web" : null;
    }

    private static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    // 裸名 quickcmd / quickcmd.exe 解析成本进程的绝对路径。
    // 必须在这里（CLI 进程内）解析：qchost 里 Environment.ProcessPath 指向 qchost.exe，
    // 运行时再翻译会启动错程序。
    private static string ResolveSelfProgram(string prog)
    {
        if (string.IsNullOrWhiteSpace(prog)) return prog;
        var t = prog.Trim();
        if (t.IndexOf('\\') >= 0 || t.IndexOf('/') >= 0) return t;
        if (!t.Equals("quickcmd", StringComparison.OrdinalIgnoreCase) &&
            !t.Equals("quickcmd.exe", StringComparison.OrdinalIgnoreCase)) return t;
        return Environment.ProcessPath ?? t;
    }

    private static int CmdAdd(string[] a) => Store.WithWriteLock(() => CmdAddCore(a));

    private static int CmdAddCore(string[] a)
    {
        var o = Parse(a);
        var unknown = o.Map.Keys.Where(k => !AddUpdateKeys.Contains(k)).ToList();
        if (unknown.Count > 0)
        {
            Console.WriteLine("错误: 未知选项 --" + string.Join(" --", unknown) + "（quickcmd help 查看可用选项）");
            return 2;
        }

        var title = o.Value("title");
        if (string.IsNullOrWhiteSpace(title)) { Console.WriteLine("错误: 必须提供 --title"); return 2; }
        var prog = ResolveSelfProgram(o.Value("program"));
        var script = o.Value("shell-script");
        if (string.IsNullOrWhiteSpace(prog) && string.IsNullOrWhiteSpace(script))
        {
            Console.WriteLine("错误: --program 和 --shell-script 至少提供一个");
            return 2;
        }
        if (!string.IsNullOrWhiteSpace(prog) && !string.IsNullOrWhiteSpace(script))
        {
            Console.WriteLine("错误: --program 和 --shell-script 只能二选一");
            return 2;
        }
        if (!string.IsNullOrWhiteSpace(script))
        {
            var ext = Path.GetExtension(script.Trim()).ToLowerInvariant();
            if (ext != ".py" && ext != ".pyw" && ext != ".bat" && ext != ".cmd" && ext != ".ps1")
            {
                Console.WriteLine("错误: shell-script 不支持 " + (ext.Length == 0 ? "(无扩展名)" : ext)
                    + "，支持 .py .pyw .bat .cmd .ps1；可执行程序请用 --program");
                return 2;
            }
        }

        var env = (o.Values("env") ?? new List<string>()).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        foreach (var e in env)
            if (e.IndexOf('=') <= 0)
            {
                Console.WriteLine("错误: 环境变量格式必须是 KEY=VAL，实际: " + e);
                return 2;
            }

        var type = NormalizeType(o.Value("type"));
        if (type == null) { Console.WriteLine("错误: --type 只支持 app（默认）或 web"); return 2; }
        var url = (o.Value("url") ?? "").Trim();
        if (url.Length > 0 && !IsHttpUrl(url))
        {
            Console.WriteLine("错误: --url 必须是 http:// 或 https:// 开头的完整地址，实际: " + url);
            return 2;
        }
        if (type == "web" && url.Length == 0)
            Console.WriteLine("⚠ 注意: --type web 没有 --url，托盘/面板无法提供“打开网站”入口");

        var list = Store.Load(out _);
        var c = new CommandDef
        {
            Title = title.Trim(),
            Proj = (o.Value("proj") ?? "").Trim(),
            Workdir = (o.Value("workdir") ?? "").Trim(),
            Env = env,
            Program = prog?.Trim() ?? "",
            ShellScript = script?.Trim() ?? "",
            Args = o.Value("args") ?? "",
            Interpreter = (o.Value("interpreter") ?? "").Trim(),
            Type = type,
            Url = url,
            AutoStart = o.Has("auto-start"),
            CaptureLogs = o.Has("capture-logs"),
            KeepOpen = !o.Has("close-on-exit"),
        };
        var mi = o.Value("max-instances");
        if (mi != null)
        {
            if (!int.TryParse(mi, out var m) || m < 1)
            {
                Console.WriteLine("错误: --max-instances 必须是 >=1 的整数");
                return 2;
            }
            c.MaxInstances = m;
        }

        if (c.Workdir.Length > 0 && !Directory.Exists(Launcher.Expand(c.Workdir)))
        {
            // web 类型托管的是这个目录：不存在就直接拒绝，避免静默托管空目录/用户目录
            if (c.IsWeb)
            {
                Console.WriteLine("错误: web 类型的 --workdir（站点根目录）不存在: " + Launcher.Expand(c.Workdir));
                return 2;
            }
            Console.WriteLine("⚠ 注意: 执行目录当前不存在（仍会保存）: " + Launcher.Expand(c.Workdir));
        }
        if (c.IsWeb && c.Workdir.Length == 0)
            Console.WriteLine("⚠ 注意: web 类型未指定 --workdir，将使用用户目录作为站点根目录");

        // 生成 GUID，保证 8 位 short id 不与现有记录冲突
        for (int t = 0; t < 8; t++)
        {
            c.Id = Guid.NewGuid().ToString("N");
            if (!list.Any(x => x.Id.StartsWith(c.ShortId, StringComparison.OrdinalIgnoreCase))) break;
        }

        list.Add(c);
        Store.SaveAll(list);

        Console.WriteLine("已创建:");
        PrintRecord(c);
        WarnDuplicateTitle(c, list);
        return 0;
    }

    private static int CmdList(string[] a)
    {
        var o = Parse(a);
        var list = Store.Load(out var bad);
        if (o.Has("proj"))
        {
            var p = o.Value("proj");
            list = list.Where(c => string.Equals(c.Proj, p, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (bad > 0) Console.WriteLine("⚠ 跳过 " + bad + " 行无法解析的数据");

        if (o.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(list, Store.JsonOpts));
            return 0;
        }
        if (list.Count == 0)
        {
            Console.WriteLine("（没有命令。用 quickcmd add 注册，数据文件: " + Store.FilePath + "）");
            return 0;
        }
        Console.WriteLine("SHORT     TITLE                  PROJ           TYPE  MAX  RUN");
        foreach (var c in list)
        {
            var run = c.KindText + (string.IsNullOrEmpty(c.Args) ? "" : " " + c.Args);
            if (c.IsWeb && !string.IsNullOrWhiteSpace(c.Url)) run = "→ " + c.Url + "   (" + run + ")";
            run = Trunc(run, 64);
            Console.WriteLine(
                c.ShortId.PadRight(10)
                + Trunc(c.Title, 22).PadRight(24)
                + Trunc(string.IsNullOrEmpty(c.Proj) ? "-" : c.Proj, 14).PadRight(16)
                + (c.IsWeb ? "web" : "app").PadRight(6)
                + c.MaxInstances.ToString().PadRight(6)
                + run);
        }
        return 0;
    }

    private static int CmdGet(string[] a)
    {
        var o = Parse(a);
        var list = Store.Load(out _);
        var c = Resolver.Resolve(o.Positional.FirstOrDefault(), list, out var err);
        if (c == null) { Console.WriteLine("错误: " + err); return 1; }
        if (o.Has("json")) { Console.WriteLine(JsonSerializer.Serialize(c, Store.JsonOpts)); return 0; }
        PrintRecord(c);
        return 0;
    }

    // web 命令的访问地址（agent 用：一条命令拿到 URL）
    private static int CmdUrl(string[] a)
    {
        var o = Parse(a);
        var list = Store.Load(out _);
        var c = Resolver.Resolve(o.Positional.FirstOrDefault(), list, out var err);
        if (c == null) { Console.WriteLine("错误: " + err); return 1; }
        if (string.IsNullOrWhiteSpace(c.Url))
        {
            Console.WriteLine("错误: 「" + c.Title + "」没有 --url（update 时用 --url 设置）");
            return 1;
        }
        Console.WriteLine(c.Url);
        return 0;
    }

    private static int CmdUpdate(string[] a) => Store.WithWriteLock(() => CmdUpdateCore(a));

    private static int CmdUpdateCore(string[] a)
    {
        var o = Parse(a);
        var unknown = o.Map.Keys.Where(k => !AddUpdateKeys.Contains(k)).ToList();
        if (unknown.Count > 0)
        {
            Console.WriteLine("错误: 未知选项 --" + string.Join(" --", unknown) + "（quickcmd help 查看可用选项）");
            return 2;
        }

        var list = Store.Load(out _);
        var c = Resolver.Resolve(o.Positional.FirstOrDefault(), list, out var err);
        if (c == null) { Console.WriteLine("错误: " + err); return 1; }

        if (o.Has("title")) c.Title = (o.Value("title") ?? "").Trim();
        if (o.Has("proj")) c.Proj = (o.Value("proj") ?? "").Trim();
        if (o.Has("workdir")) c.Workdir = (o.Value("workdir") ?? "").Trim();
        if (o.Has("program")) c.Program = ResolveSelfProgram(o.Value("program"))?.Trim() ?? "";
        if (o.Has("shell-script")) c.ShellScript = (o.Value("shell-script") ?? "").Trim();
        if (o.Has("args")) c.Args = o.Value("args") ?? "";
        if (o.Has("interpreter")) c.Interpreter = (o.Value("interpreter") ?? "").Trim();
        if (o.Has("env")) c.Env = o.Values("env").Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (o.Has("max-instances"))
        {
            if (!int.TryParse(o.Value("max-instances"), out var m) || m < 1)
            {
                Console.WriteLine("错误: --max-instances 必须是 >=1 的整数");
                return 2;
            }
            c.MaxInstances = m;
        }
        if (o.Has("keep-open")) c.KeepOpen = true;
        if (o.Has("close-on-exit")) c.KeepOpen = false;

        if (o.Has("type"))
        {
            var t = NormalizeType(o.Value("type"));
            if (t == null) { Console.WriteLine("错误: --type 只支持 app（默认）或 web"); return 2; }
            c.Type = t;
        }
        if (o.Has("url"))
        {
            var u = (o.Value("url") ?? "").Trim();
            if (u.Length > 0 && !IsHttpUrl(u))
            {
                Console.WriteLine("错误: --url 必须是 http:// 或 https:// 开头的完整地址，实际: " + u);
                return 2;
            }
            c.Url = u;
        }
        if (o.Has("auto-start")) c.AutoStart = true;
        if (o.Has("no-auto-start")) c.AutoStart = false;
        if (o.Has("capture-logs")) c.CaptureLogs = true;
        if (o.Has("no-capture-logs")) c.CaptureLogs = false;

        if (string.IsNullOrWhiteSpace(c.Program) && string.IsNullOrWhiteSpace(c.ShellScript))
        {
            Console.WriteLine("错误: program / shell-script 不能都为空");
            return 2;
        }
        if (!string.IsNullOrWhiteSpace(c.Program) && !string.IsNullOrWhiteSpace(c.ShellScript))
        {
            Console.WriteLine("错误: program 与 shell-script 只能二选一");
            return 2;
        }
        if (!string.IsNullOrWhiteSpace(c.ShellScript))
        {
            var ext = Path.GetExtension(c.ShellScript.Trim()).ToLowerInvariant();
            if (ext != ".py" && ext != ".pyw" && ext != ".bat" && ext != ".cmd" && ext != ".ps1")
            {
                Console.WriteLine("错误: shell-script 不支持 " + (ext.Length == 0 ? "(无扩展名)" : ext)
                    + "，支持 .py .pyw .bat .cmd .ps1；可执行程序请用 --program");
                return 2;
            }
        }
        if (c.IsWeb && c.Workdir.Length > 0 && !Directory.Exists(Launcher.Expand(c.Workdir)))
        {
            Console.WriteLine("错误: web 类型的 --workdir（站点根目录）不存在: " + Launcher.Expand(c.Workdir));
            return 2;
        }

        c.UpdatedAt = DateTime.Now;
        Store.SaveAll(list);
        Console.WriteLine("已更新:");
        PrintRecord(c);
        WarnDuplicateTitle(c, list);
        return 0;
    }

    private static int CmdDelete(string[] a) => Store.WithWriteLock(() => CmdDeleteCore(a));

    private static int CmdDeleteCore(string[] a)
    {
        var o = Parse(a);
        var list = Store.Load(out _);
        var c = Resolver.Resolve(o.Positional.FirstOrDefault(), list, out var err);
        if (c == null) { Console.WriteLine("错误: " + err); return 1; }

        PrintRecord(c);
        if (!o.Has("yes"))
        {
            Console.Write("确认删除? [y/N]: ");
            var r = Console.ReadLine();
            if (r == null || !(r.Trim() == "y" || r.Trim() == "Y" || r.Trim() == "yes" || r.Trim() == "是"))
            {
                Console.WriteLine("已取消");
                return 1;
            }
        }
        list.Remove(c);
        Store.SaveAll(list);
        Console.WriteLine("已删除: " + c.ShortId + " " + c.Title);
        return 0;
    }

    // useJob=false → run（PID 模式：按 root pid 树跟踪）
    // useJob=true  → run-job（JOB 模式：作业对象整树跟踪，qchost 退化时仍可能落到 PID）
    // qchost 未运行时自动拉起 qchost.exe，再走 IPC（实例仍可被托盘跟踪管理）
    private static int CmdRun(string[] a, bool useJob)
    {
        var o = Parse(a);
        var list = Store.Load(out _);
        var c = Resolver.Resolve(o.Positional.FirstOrDefault(), list, out var err);
        if (c == null) { Console.WriteLine("错误: " + err); return 1; }

        // 1) qchost 已在运行：直接交给它执行
        int r = SendRunToQchost(c, useJob, connectTimeoutMs: 800);
        if (r != 2) return r;

        // 2) qchost 未运行：自动拉起，等待 IPC 就绪（pipe 在 qchost 构造器里启动）
        Console.WriteLine("qchost（托盘宿主）未运行，正在自动拉起…");
        if (StartQchostProcess())
        {
            r = SendRunToQchost(c, useJob, connectTimeoutMs: 12000);
            if (r != 2) return r;
            Console.WriteLine("⚠ qchost 拉起后 12 秒内仍连不上 IPC，改为直接启动。");
        }
        else
        {
            Console.WriteLine("⚠ 找不到 qchost（qchost.exe），改为直接启动。");
        }

        // 3) 兜底：直接启动（无 qchost 跟踪，run / run-job 行为相同）
        //    CLI 启动完就退出，管道中继线程会被一起收走，所以这条路径不捕获日志。
        if (c.CaptureLogs)
            Console.WriteLine("⚠ 未经过 qchost：本次不捕获日志（--capture-logs 依赖 qchost 的 JOB 模式）");
        var proc = Launcher.Launch(c, out var err2);
        if (proc == null) { Console.WriteLine("错误: " + err2); return 1; }
        History.Append(new HistoryEntry
        {
            Event = "start", Kind = "run", Source = "cli-local", ExecId = c.Id,
            Title = c.Title, ShortId = c.ShortId, CommandLine = History.CommandLineOf(c),
            Workdir = Launcher.EffectiveWorkdir(c), Status = "local", Pid = proc.Id,
            RequesterPid = Environment.ProcessId,
        }, out _);
        Console.WriteLine("已直接启动（该实例不会被跟踪，也不会出现在托盘菜单中）。pid=" + proc.Id);
        return 0;
    }

    // 免注册直跑：走 qchost（它常驻托盘，不受调用方沙盒限制），并自动留审计记录。
    // --local 显式降级为当前进程直启（记 local，提示可能受沙盒限制）。
    private static int CmdExec(string[] a)
    {
        ExecArgs x;
        try { x = ParseExec(a); }
        catch (Exception ex) { Console.WriteLine("错误: " + ex.Message); return 2; }

        var line = string.Join(" ", x.Command).Trim();
        if (line.Length == 0 && string.IsNullOrWhiteSpace(x.Program))
        {
            Console.WriteLine("用法: quickcmd exec [选项] -- <命令行>");
            Console.WriteLine("  例: quickcmd exec --workdir D:\\code\\app -- npm run build");
            return 2;
        }

        var msg = new IpcMessage
        {
            Op = "exec",
            Source = "cli",
            RequesterPid = Environment.ProcessId,
            Title = x.Title,
            CommandLine = line,
            Program = x.Program,
            Args = x.Args ?? "",
            Workdir = x.Workdir,
            Env = x.Env,
            KeepOpen = x.KeepOpen,
            CaptureLogs = x.CaptureLogs,
            Mode = string.IsNullOrWhiteSpace(x.Mode) ? "job" : x.Mode,
        };

        if (x.Local)
        {
            var def = Launcher.BuildAdHoc(msg, out var perr);
            if (def == null) { Console.WriteLine("错误: " + perr); return 2; }
            if (def.CaptureLogs)
                Console.WriteLine("⚠ 本地直启不捕获日志（--capture-logs 依赖 qchost 的 JOB 模式）");
            var proc = Launcher.Launch(def, out var lerr);
            History.Append(new HistoryEntry
            {
                Event = "start", Kind = "exec", Source = "cli-local", ExecId = def.Id,
                Title = def.Title, CommandLine = History.CommandLineOf(def),
                Workdir = Launcher.EffectiveWorkdir(def), Status = "local",
                Pid = proc?.Id ?? 0, Error = proc == null ? lerr ?? "" : "",
                RequesterPid = Environment.ProcessId,
            }, out _);
            if (proc == null) { Console.WriteLine("错误: " + lerr); return 1; }
            Console.WriteLine("⚠ 本地直启（不经过 qchost）：进程可能受调用方沙盒/权限限制，也不在托盘实例列表里。pid=" + proc.Id);
            Console.WriteLine("审计: quickcmd history --limit 5");
            return 0;
        }

        // 必须经 qchost：出沙盒、实例跟踪、审计都由它负责
        if (!Ipc.EnsureHost(500, 3000))
        {
            History.Append(new HistoryEntry
            {
                Event = "start", Kind = "exec", Source = "cli", Title = x.Title ?? line,
                CommandLine = line, Workdir = x.Workdir ?? "", Status = "host-unavailable",
                Error = "qchost 未启动且自动拉起失败", RequesterPid = Environment.ProcessId,
            }, out _);
            Console.WriteLine("错误: qchost 未运行且自动拉起失败。");
            Console.WriteLine("      免注册直跑必须经 qchost（常驻托盘、不受调用方沙盒限制）。");
            Console.WriteLine("      先启动 qcApp.exe（托盘），或临时用: quickcmd exec --local -- <命令>");
            return 3;
        }

        int r = Ipc.TryCall(msg, 3000, out var resp, out var failReason);
        if (r != 0)
        {
            History.Append(new HistoryEntry
            {
                Event = "start", Kind = "exec", Source = "cli", Title = x.Title ?? line,
                CommandLine = line, Workdir = x.Workdir ?? "", Status = "host-unavailable",
                Error = failReason ?? "qchost 无响应", RequesterPid = Environment.ProcessId,
            }, out _);
            Console.WriteLine("错误: " + (failReason ?? "qchost 无响应"));
            return 3;
        }
        if (resp == null || !resp.Ok)
        {
            var m = resp?.Message ?? "qchost 无响应";
            if (m.Contains("unknown op", StringComparison.OrdinalIgnoreCase))
                m += "（qchost 版本过旧；先 quickcmd host restart 再试）";
            Console.WriteLine("错误: " + m);
            return 1;
        }

        Console.WriteLine("OK: " + resp.Message);
        if (!string.IsNullOrEmpty(resp.Data))
        {
            try
            {
                var e = JsonSerializer.Deserialize<HistoryEntry>(resp.Data, Store.JsonOpts);
                if (e != null) Console.WriteLine("历史: " + e.Title);
            }
            catch { }
        }
        Console.WriteLine("审计: quickcmd history --limit 5");
        return 0;
    }

    private sealed class ExecArgs
    {
        public readonly List<string> Command = new();
        public readonly List<string> Env = new();
        public string Title;
        public string Workdir;
        public string Mode;
        public string Program;
        public string Args;
        public bool KeepOpen;
        public bool CaptureLogs;
        public bool Local;
    }

    // exec 的解析规则：选项只能出现在命令之前；遇到 "--" 或第一个非选项 token 后，
    // 其余全部原样作为命令行（命令自身的 --flag 不会被当成 quickcmd 选项）。
    private static ExecArgs ParseExec(string[] a)
    {
        var r = new ExecArgs();
        for (int i = 0; i < a.Length; i++)
        {
            var t = a[i];
            if (t == "--")
            {
                for (i++; i < a.Length; i++) r.Command.Add(a[i]);
                break;
            }
            if (t.StartsWith("--", StringComparison.Ordinal))
            {
                var key = t.Substring(2);
                string val = null;
                var eq = key.IndexOf('=');
                if (eq >= 0) { val = key.Substring(eq + 1); key = key.Substring(0, eq); }
                switch (key.ToLowerInvariant())
                {
                    case "workdir": r.Workdir = val ?? NextValue(a, ref i); break;
                    case "env": r.Env.Add(val ?? NextValue(a, ref i)); break;
                    case "title":
                    case "label": r.Title = val ?? NextValue(a, ref i); break;
                    case "mode": r.Mode = val ?? NextValue(a, ref i); break;
                    case "program": r.Program = val ?? NextValue(a, ref i); break;
                    case "args": r.Args = val ?? NextValue(a, ref i); break;
                    case "keep-open": r.KeepOpen = true; break;
                    case "capture-logs": r.CaptureLogs = true; break;
                    case "local": r.Local = true; break;
                    default: throw new ArgumentException("未知选项 --" + key + "（quickcmd help 查看 exec 选项）");
                }
                continue;
            }

            for (; i < a.Length; i++) r.Command.Add(a[i]);
            break;
        }
        return r;
    }

    private static string NextValue(string[] a, ref int i) =>
        i + 1 < a.Length ? a[++i] : throw new ArgumentException("选项缺值");

    // 审计历史：默认最近 20 条；--json 给机器读；--clear -y 清空。
    private static int CmdHistory(string[] a)
    {
        var o = Parse(a);
        if (o.Has("clear"))
        {
            if (!o.Has("yes"))
            {
                Console.WriteLine("清空审计历史不可撤销：确认请用 quickcmd history --clear -y");
                return 1;
            }
            if (!History.Clear(out var cerr)) { Console.WriteLine("错误: " + cerr); return 1; }
            Console.WriteLine("已清空: " + History.FilePath);
            return 0;
        }

        int limit = 20;
        if (o.Has("limit"))
        {
            if (!int.TryParse(o.Value("limit"), out limit) || limit < 0)
            {
                Console.WriteLine("错误: --limit 需要 >=0 的整数（0 = 全部）");
                return 2;
            }
        }

        var list = History.Read(limit, o.Value("grep"), out var bad);
        if (o.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(list, Store.JsonOpts));
            return 0;
        }
        if (bad > 0) Console.WriteLine("⚠ 跳过 " + bad + " 行无法解析的历史");
        if (list.Count == 0)
        {
            Console.WriteLine("（没有历史记录。用 quickcmd run / exec 执行后产生。文件: " + History.FilePath + "）");
            return 0;
        }

        Console.WriteLine("TIME           EVENT  TYPE  SOURCE       STATUS           COMMAND");
        foreach (var e in list)
        {
            var ev = e.Event == "finish" ? "完成" : "启动";
            var kind = e.Kind == "exec" ? "直跑" : "注册";
            var status = History.StatusText(e.Status)
                + (e.ExitCode != int.MinValue ? "(" + e.ExitCode + ")" : "");
            Console.WriteLine(
                e.Timestamp.ToString("MM-dd HH:mm:ss").PadRight(15)
                + ev.PadRight(7)
                + kind.PadRight(6)
                + Trunc(string.IsNullOrWhiteSpace(e.Source) ? "-" : e.Source, 10).PadRight(13)
                + status.PadRight(17)
                + Trunc(e.CommandLine, 60));
        }
        Console.WriteLine("（共 " + list.Count + " 条；文件: " + History.FilePath + "）");
        return 0;
    }

    // 输出日志：捕获型命令（--capture-logs）的 stdout/stderr。
    //   quickcmd logs                   列出最近日志
    //   quickcmd logs <key>             看某个命令/实例的最新日志（默认 60 行）
    //   quickcmd logs <key> --tail 200  多取几行
    //   quickcmd logs <key> --path      只打印文件路径（给编辑器/agent 用）
    //   quickcmd logs <key> --clear -y  删除该命令的日志（不带 key 清空整个目录）
    private static int CmdLogs(string[] a)
    {
        var o = Parse(a);
        var key = o.Positional.FirstOrDefault();

        if (o.Has("clear")) return ClearLogs(key, o.Has("yes"));

        if (string.IsNullOrWhiteSpace(key))
        {
            var files = LogStore.List(20);
            if (o.Has("json")) { Console.WriteLine(JsonSerializer.Serialize(files, Store.JsonOpts)); return 0; }
            if (files.Count == 0)
            {
                Console.WriteLine("（还没有日志。给命令加 --capture-logs 后运行即可捕获 stdout/stderr）");
                Console.WriteLine("目录: " + LogStore.Dir);
                return 0;
            }
            Console.WriteLine("TIME           SIZE      FILE");
            foreach (var f in files)
                Console.WriteLine(
                    f.Modified.ToString("MM-dd HH:mm:ss").PadRight(15)
                    + f.SizeText.PadRight(10)
                    + f.Name);
            Console.WriteLine("（共 " + files.Count + " 个；目录: " + LogStore.Dir + "）");
            Console.WriteLine("查看: quickcmd logs <short-id|实例ID> [--tail N]");
            return 0;
        }

        var file = LogStore.Resolve(key, out var why);
        if (file == null)
        {
            Console.WriteLine("错误: " + why);
            return 1;
        }
        if (o.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(file, Store.JsonOpts));
            return 0;
        }
        if (o.Has("open"))
        {
            try { Process.Start(new ProcessStartInfo(file.Path) { UseShellExecute = true }); } catch { }
        }
        Console.WriteLine(file.Path);

        int tail = 60;
        if (o.Has("tail") && (!int.TryParse(o.Value("tail"), out tail) || tail <= 0))
        {
            Console.WriteLine("错误: --tail 需要 >0 的整数");
            return 2;
        }
        if (o.Has("path")) return 0;

        var text = LogStore.Tail(file.Path, tail);
        if (text.Length == 0)
        {
            Console.WriteLine("（日志为空：命令可能还没输出，或还在缓冲）");
            return 0;
        }
        Console.WriteLine("----- 最近 " + tail + " 行 -----");
        Console.WriteLine(text);
        return 0;
    }

    private static int ClearLogs(string key, bool yes)
    {
        if (!yes)
        {
            Console.WriteLine("删除日志不可撤销：确认请加 -y");
            return 1;
        }
        var files = new List<LogFile>();
        if (string.IsNullOrWhiteSpace(key))
        {
            files = LogStore.List(0);
        }
        else
        {
            var list = Store.Load(out _);
            var c = Resolver.Resolve(key, list, out _);
            var prefix = c != null ? LogStore.Key(c) : key.Trim();
            files = LogStore.List(0)
                .Where(f => f.Name.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(f.Name, prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        var n = 0;
        foreach (var f in files) { try { File.Delete(f.Path); n++; } catch { } }
        Console.WriteLine("已删除 " + n + " 个日志文件（目录: " + LogStore.Dir + "）");
        return 0;
    }

    // 打开本地 Web 控制台：确保 qchost 在线，向它要 URL（默认 http://127.0.0.1:41777/，
    // 端口被占用时宿主会顺延并写回 settings.json）
    private static int CmdUi(string[] a)
    {
        var o = Parse(a);
        if (!Ipc.EnsureHost(500, 5000))
        {
            Console.WriteLine("错误: qchost 未运行且自动拉起失败");
            return 1;
        }
        int r = Ipc.TryCall(new IpcMessage { Op = "ui" }, 1500, out var resp, out var failReason);
        if (r != 0 || resp == null || !resp.Ok)
        {
            Console.WriteLine("错误: " + (resp?.Message ?? failReason ?? "qchost 无响应"));
            return 1;
        }
        Console.WriteLine(resp.Message);
        if (!o.Has("no-open"))
        {
            try { Process.Start(new ProcessStartInfo(resp.Message) { UseShellExecute = true }); } catch { }
        }
        return 0;
    }

    // 常驻宿主管理（升级/排障用）：status | stop | restart
    private static int CmdHost(string[] a)
    {
        var sub = (a.FirstOrDefault() ?? "status").ToLowerInvariant();
        switch (sub)
        {
            case "status":
            {
                int r = Ipc.TryCall(new IpcMessage { Op = "ping" }, 800, out var resp, out _);
                if (r == 0 && resp != null && resp.Ok)
                {
                    Console.WriteLine("qchost: 在线 (" + resp.Message + ")");
                    return 0;
                }
                Console.WriteLine("qchost: 未运行");
                return 1;
            }
            case "stop":
            case "restart":
            {
                int r = Ipc.TryCall(new IpcMessage { Op = "shutdown" }, 1500, out _, out _);
                if (r == 0)
                {
                    Console.WriteLine("qchost 已退出");
                    Thread.Sleep(300);
                }
                else Console.WriteLine("qchost 未运行");

                if (sub == "restart")
                {
                    if (!Ipc.EnsureHost(500, 5000))
                    {
                        Console.WriteLine("错误: qchost 重启失败（新进程未就绪）");
                        return 1;
                    }
                    Console.WriteLine("qchost 已重启");
                }
                return 0;
            }
            default:
                Console.WriteLine("用法: quickcmd host [status|stop|restart]");
                return 2;
        }
    }

    // 通过共享 IPC 协议向 qchost 发一条请求。返回：0=拿到响应 1=连接中断/无响应（已打印） 2=qchost 不在（未连上）
    private static int TryQchostCall(IpcMessage msg, int connectTimeoutMs, out QchostResponse resp)
    {
        int r = Ipc.TryCall(msg, connectTimeoutMs, out resp, out var failReason);
        if (r == 1) Console.WriteLine("错误: " + failReason);
        return r;
    }

    // 返回：0=成功 1=失败（已打印） 2=qchost 不在。ps/kill 不自动拉起 qchost：实例本来就活在 qchost 里
    private static int SendRunToQchost(CommandDef c, bool useJob, int connectTimeoutMs)
    {
        int r = TryQchostCall(new IpcMessage
        {
            Op = "run",
            Key = c.Id,
            Mode = useJob ? "job" : "pid",
            Source = "cli",
            RequesterPid = Environment.ProcessId,
        }, connectTimeoutMs, out var gr);
        if (r != 0) return r;
        Console.WriteLine((gr != null && gr.Ok ? "OK: " : "错误: ") + (gr?.Message ?? "(无响应)"));
        return gr != null && gr.Ok ? 0 : 1;
    }

    private static int CmdPs(string[] a)
    {
        int r = TryQchostCall(new IpcMessage { Op = "ps" }, 800, out var gr);
        if (r == 2) { Console.WriteLine("（qchost 未运行，没有被跟踪的实例）"); return 0; }
        if (r == 1) return 1;
        if (gr == null || !gr.Ok) { Console.WriteLine("错误: " + (gr?.Message ?? "qchost 无响应")); return 1; }

        var list = string.IsNullOrEmpty(gr.Data)
            ? new List<InstanceInfo>()
            : JsonSerializer.Deserialize<List<InstanceInfo>>(gr.Data, Store.JsonOpts);
        if (list == null || list.Count == 0) { Console.WriteLine("（没有运行中的实例）"); return 0; }

        Console.WriteLine("ID              MODE   启动        运行时长    SHORT      命令");
        foreach (var inst in list.OrderBy(x => x.Started))
        {
            var span = DateTime.Now - inst.Started;
            var dur = span.TotalMinutes < 1 ? (int)span.TotalSeconds + " 秒" : (long)span.TotalMinutes + " 分";
            Console.WriteLine(
                (inst.Id + (inst.RootExited ? "*" : "")).PadRight(16)
                + ("[" + inst.Mode + "]").PadRight(7)
                + inst.Started.ToString("HH:mm:ss").PadRight(12)
                + dur.PadRight(12)
                + (inst.ShortId ?? "").PadRight(10)
                + (string.IsNullOrWhiteSpace(inst.Proj) ? "" : inst.Proj + " > ") + inst.Title
                + (string.IsNullOrWhiteSpace(inst.Url) ? "" : "  ·  " + inst.Url));
            PrintProcTree(inst);
        }
        Console.WriteLine("（共 " + list.Count + " 个实例。结束: quickcmd kill <ID>——[JOB] 击杀整棵作业树；* = root 已退出，存活靠作业）");
        return 0;
    }

    // 进程树（JOB 成员 / pid 子孙），按 ppid 组林；root pid 所在节点标注
    private static void PrintProcTree(InstanceInfo inst)
    {
        var tree = inst.Tree ?? new List<ProcNode>();
        if (tree.Count == 0) { Console.WriteLine("    （当前无可见的活跃进程）"); return; }

        int root = int.TryParse(inst.Id, out var r) ? r : -1;
        var set = new HashSet<int>(tree.Select(n => n.Pid));
        var kids = tree.GroupBy(n => n.Ppid)
            .ToDictionary(g => g.Key, g => g.Where(n => set.Contains(n.Pid) && n.Pid != n.Ppid).OrderBy(n => n.Pid).ToList());
        var roots = tree.Where(n => !set.Contains(n.Ppid))
            .OrderBy(n => n.Pid == root ? 0 : 1).ThenBy(n => n.Pid).ToList();

        foreach (var rn in roots) Walk(rn, "    ", 0, true);

        void Walk(ProcNode n, string indent, int depth, bool last)
        {
            var mark = n.Pid == root ? (inst.Mode == "JOB" ? "   <- root pid" : "   <- 实例ID") : "";
            Console.WriteLine(indent + (depth == 0 ? "" : last ? "└─ " : "├─ ") + n.Pid + "  " + n.Name + mark);
            var ch = kids.TryGetValue(n.Pid, out var l) ? l : new List<ProcNode>();
            var childIndent = indent + (depth == 0 ? "  " : last ? "   " : "│  ");
            for (int i = 0; i < ch.Count; i++) Walk(ch[i], childIndent, depth + 1, i == ch.Count - 1);
        }
    }

    private static int CmdKill(string[] a)
    {
        var o = Parse(a);
        var t = o.Positional.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(t))
        {
            Console.WriteLine("用法: quickcmd kill <实例ID>   ID 取自 quickcmd ps（job-<数字> 或 pid）");
            return 2;
        }
        int r = TryQchostCall(new IpcMessage { Op = "kill", Key = t }, 800, out var gr);
        if (r == 2) { Console.WriteLine("错误: qchost 未运行（实例只存活于 qchost 进程内，无实例可结束）"); return 1; }
        if (r == 1) return 1;
        Console.WriteLine((gr != null && gr.Ok ? "OK: " : "错误: ") + (gr?.Message ?? "(无响应)"));
        return gr != null && gr.Ok ? 0 : 1;
    }

    // 显示配套 agent skill 文档（嵌入在 quickcmd.shared.dll，资源名 skill.md）
    private static int CmdSkill()
    {
        var text = SkillResource.LoadText();
        if (text == null) { Console.WriteLine("错误: 此可执行文件内未嵌入 skill.md"); return 1; }
        Console.Write(text);
        return 0;
    }

    // 安装 skill 文档到 ~/.agents/skills/traycmd/SKILL.md；-y 覆盖已存在
    private static int CmdInstallSkill(string[] a)
    {
        var o = Parse(a);
        var unknown = o.Map.Keys.Where(k => k != "yes").ToList();
        if (unknown.Count > 0)
        {
            Console.WriteLine("错误: 未知选项 --" + string.Join(" --", unknown));
            return 2;
        }

        bool overwrite = o.Has("yes");
        Console.WriteLine("目标: " + SkillResource.TargetFile);
        if (!overwrite && SkillResource.Exists())
        {
            Console.WriteLine("（文件已存在。用 -y 覆盖，或先到目录确认版本）");
            return 3;
        }
        if (SkillResource.Install(overwrite, out var err))
        {
            Console.WriteLine((overwrite && SkillResource.Exists() ? "已覆盖: " : "已安装: ") + SkillResource.TargetFile);
            return 0;
        }
        Console.WriteLine("错误: " + err);
        return 1;
    }

    // 拉起 qchost（与 quickcmd 同目录优先，其次交给系统搜索）
    private static bool StartQchostProcess() => Ipc.StartHost();

    // ----- 输出辅助 -----

    private static void PrintRecord(CommandDef c)
    {
        Console.WriteLine("  short    : " + c.ShortId);
        Console.WriteLine("  id       : " + c.Id);
        Console.WriteLine("  title    : " + c.Title);
        Console.WriteLine("  type     : " + (c.IsWeb ? "web（网站/常驻服务）" : "app（普通程序）"));
        if (!string.IsNullOrWhiteSpace(c.Url)) Console.WriteLine("  url      : " + c.Url);
        if (c.AutoStart) Console.WriteLine("  auto     : qchost 启动时自动拉起");
        Console.WriteLine("  proj     : " + (string.IsNullOrEmpty(c.Proj) ? "(空)" : c.Proj));
        Console.WriteLine("  workdir  : " + (string.IsNullOrEmpty(c.Workdir) ? "(默认=用户目录)" : c.Workdir));
        Console.WriteLine("  run      : " + c.KindText + (string.IsNullOrEmpty(c.Args) ? "" : "  " + c.Args));
        Console.WriteLine("  interp   : " + (string.IsNullOrWhiteSpace(c.Interpreter)
            ? (string.IsNullOrWhiteSpace(c.ShellScript) ? "(不适用)" : "(默认: 按扩展名 → 系统 PATH)")
            : c.Interpreter));
        Console.WriteLine("  keep     : " + (c.KeepOpen ? "窗口保持" : "退出即关"));
        if (c.CaptureLogs) Console.WriteLine("  logs     : 捕获输出 → " + LogStore.Dir + "（无控制台窗口）");
        Console.WriteLine("  env      : " + (c.Env.Count == 0 ? "(无)" : string.Join("  ", c.Env)));
        Console.WriteLine("  max      : " + c.MaxInstances);
        Console.WriteLine("  updated  : " + c.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    private static void WarnDuplicateTitle(CommandDef c, List<CommandDef> list)
    {
        var same = list.Where(x => x != c && string.Equals(x.Title.Trim(), c.Title.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (same.Count > 0)
            Console.WriteLine("⚠ 警告: 已存在 " + same.Count + " 条同名 title（"
                + string.Join(", ", same.Select(x => x.ShortId + " " + x.Title))
                + "）。允许重名，定位时请用 short id。");
    }

    private static string Trunc(string s, int n)
    {
        s = s ?? "";
        return s.Length <= n ? s : s.Substring(0, Math.Max(1, n - 1)) + "…";
    }

    private static void Help()
    {
        Console.WriteLine("""
quickcmd — 系统托盘命令启动器（CLI）
qchost（quick command host）= 常驻托盘的宿主进程 qchost.exe：负责托盘菜单、实例跟踪、IPC 服务端。
数据文件: %APPDATA%\quickcmd\commands.jsonl（注册项）
审计文件: %APPDATA%\quickcmd\history.jsonl（执行历史，qchost 自动写入）

用法:
  quickcmd                                  无参数 = 显示本帮助
  quickcmd add --title <名> (--program <程序> | --shell-script <脚本>) [选项]
  quickcmd list [--proj <项目>] [--json]
  quickcmd get <short-id|id|title> [--json]
  quickcmd update <short-id|id|title> [选项]
  quickcmd delete <short-id|id|title> [-y]
  quickcmd run <short-id|id|title>         以 [PID] 模式运行（按 root pid 进程树跟踪）
  quickcmd run-job <short-id|id|title>     以 [JOB] 模式运行（作业对象整树跟踪，推荐）
  quickcmd exec [选项] -- <命令行>          免注册直跑（经 qchost 出沙盒，自动记入审计历史）
  quickcmd serve [--dir <目录>] --port <端口> [--spa]
                                           内置静态文件服务（前台运行，仅绑定 127.0.0.1）
  quickcmd url <short-id|id|title>         打印 web 命令的访问地址
  quickcmd logs [<key>] [--tail N]         查看输出日志（清单 / 某命令或实例的最新日志）
  quickcmd ui [--no-open]                  打开本地 Web 控制台（默认浏览器；--no-open 只打印 URL）
  quickcmd history [--limit N] [--grep 文本] [--json] [--clear -y]
                                           查看/导出/清空执行审计历史（默认最近 20 条）
  quickcmd host [status|stop|restart]      管理常驻宿主 qchost（升级/排障用）
  quickcmd ps                              列出 qchost 正在跟踪的实例（ID=job-<数字> 或 root pid）
  quickcmd kill <实例ID>                    结束实例：ID 取自 ps（[JOB] 杀整棵作业树+逃逸子孙，传树内任意 pid 也可定位）
  quickcmd skill                           显示配套 agent skill 文档（嵌入在本程序内）
  quickcmd install-skill [-y]              安装 skill 到 ~\.agents\skills\traycmd\（已存在时需 -y 覆盖）
  quickcmd path

add / update 选项:
  --title <名>             菜单显示名（允许重名，创建后提示是否撞名）
  --proj <项目名>          项目分组名
  --workdir <目录>         执行目录（支持 %VAR% 占位符；留空 = 用户目录）
  --env <KEY=VAL>          环境变量，可重复多次；VAL 支持 %VAR% 引用系统环境变量
  --program <程序>         可执行程序：exe 路径或 PATH 中的名称（npm 等裸命令自动经 cmd 解析）
  --shell-script <脚本>    脚本文件，按扩展名选解释器:
                           .py/.pyw → python(pythonw)   .bat/.cmd → cmd   .ps1 → pwsh(未装则回退 powershell)
  --args <参数>            program / shell-script 的附加参数
  --interpreter <路径...>  解释器查找顺序覆盖（';' 分隔文件/目录，按顺序，最后落系统 PATH），
                           用于解释器多版本场景，如 --interpreter "D:\py312;D:\py311"
  --max-instances <N>      并发实例上限，默认 1（已到上限再点击会置前现有实例）
  --type web               标记为网站/常驻服务（默认 app）；托盘“网站”菜单与后续面板显示 URL 和状态
  --url <地址>             web 类型的访问地址（须 http:// 或 https://），如 http://127.0.0.1:41001/
  --auto-start             web 类型：qchost 启动时自动拉起（默认不自动）
  --capture-logs           捕获 stdout/stderr 到日志文件（%APPDATA%\quickcmd\logs，不显示控制台窗口）
                           适合 serve / dev server 这类常驻服务；交互式命令不要开
  --close-on-exit          命令结束后关闭窗口（默认保持窗口: python 用 -i、ps1 用 -NoExit、bat 用 /k）

exec 选项（写在 -- 之前）:
  --workdir <目录>         执行目录（支持 %VAR% 占位符；空 = 用户目录）
  --env <KEY=VAL>          环境变量，可重复
  --title/--label <名>     历史里显示的名字（默认取命令行前 60 字）
  --mode job|pid           跟踪模式，默认 job（作业对象整树）
  --program <程序>         直接启动程序（与 -- 之后的命令行二选一）
  --args <参数>            --program 的附加参数
  --keep-open              窗口保持（cmd /k；默认 /c 跑完即退）
  --capture-logs           捕获 stdout/stderr 到日志文件（之后用 quickcmd logs 查看）
  --local                  显式降级为当前进程直启：可能受调用方沙盒限制，且不进托盘实例
  说明: exec 默认必须经 qchost（常驻托盘，出沙盒）；qchost 不在时命令失败（退出码 3），
        不会静默在沙盒内直接执行。-- 之后的整行原样交给 cmd。

示例:
  quickcmd add --title "Dev Server" --proj myapp --workdir D:\code\myapp --program "npm" --args "run dev"
  quickcmd add --title "数据同步" --workdir D:\jobs --shell-script D:\jobs\sync.py --interpreter "D:\py312;D:\py311"
  quickcmd add --title "记事本" --program notepad.exe --max-instances 2
  quickcmd add --type web --title "站点预览" --url http://127.0.0.1:41001/ --workdir D:\\out ^
    --program quickcmd --args "serve --port 41001 --spa" --capture-logs
  quickcmd serve --port 41001 --spa          # 直接前台起一个静态服务
  quickcmd url 1a2b3c4d
  quickcmd logs 1a2b3c4d --tail 100          # 看该命令最近一次运行的输出
  quickcmd logs                              # 列出最近的日志
  quickcmd update 1a2b3c4d --max-instances 3
  quickcmd run 1a2b3c4d          # [PID] 模式
  quickcmd run-job 1a2b3c4d      # [JOB] 模式
  quickcmd exec --workdir D:\\code\\app -- npm run build   # 免注册跑一次
  quickcmd exec -- echo ok > %USERPROFILE%\\probe.txt      # 原样交给 cmd
  quickcmd history --limit 10 --grep npm
  quickcmd delete 1a2b3c4d -y

说明:
  - short id 是 GUID 前 8 位；get/update/delete/run/run-job 都可用 short-id、完整 id 或 title 定位
  - --type web 只是标记（托盘/面板据此显示 URL 与运行状态），不改变执行方式；
    静态托管用内置 serve：--program quickcmd 会在 add 时解析成 quickcmd.exe 绝对路径，
    站点根目录就是 --workdir（web 类型要求它已存在）
  - run / run-job 把请求交给常驻 qchost（实例可跟踪管理，托盘菜单中以 [PID]/[JOB] 标注模式）；
     qchost 未运行时自动拉起 qchost.exe 再执行；找不到/拉不起 qchost 才退回直接启动（不跟踪）
   - 手动启动 qchost：直接运行 qchost.exe（无参数，单实例互斥）
   - [JOB] 模式用 Windows 作业对象跟踪整棵进程树：launcher 自退后孙进程仍算存活，
     作业成员之外沿 ppid 归属链扩出的逃逸子孙（如 bat 里 start 商店记事本）也被跟踪并补杀；
     实例 ID = job-<N>（qchost 按作业创建顺序分配的自增序号，非 pid），Kill 击杀整树，判定最准；
     [PID] 模式仅按 root pid 及其子孙树跟踪（root 提前退出会误判结束）
  - ps / kill 只在 qchost 运行时有意义（实例存活于 qchost 进程内），qchost 不在时 ps 提示为空、kill 报错
""");
    }
}

