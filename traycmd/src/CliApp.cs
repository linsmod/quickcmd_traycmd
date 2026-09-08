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
    { "title", "proj", "workdir", "env", "program", "shell-script", "args", "interpreter", "max-instances" };

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
    };

    private static int CmdAdd(string[] a)
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
        var prog = o.Value("program");
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
            Console.WriteLine("⚠ 注意: 执行目录当前不存在（仍会保存）: " + Launcher.Expand(c.Workdir));

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
        Console.WriteLine("SHORT     TITLE                  PROJ           MAX  RUN");
        foreach (var c in list)
        {
            var run = Trunc(c.KindText + (string.IsNullOrEmpty(c.Args) ? "" : " " + c.Args), 48);
            Console.WriteLine(
                c.ShortId.PadRight(10)
                + Trunc(c.Title, 22).PadRight(24)
                + Trunc(string.IsNullOrEmpty(c.Proj) ? "-" : c.Proj, 14).PadRight(16)
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

    private static int CmdUpdate(string[] a)
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
        if (o.Has("program")) c.Program = (o.Value("program") ?? "").Trim();
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

        c.UpdatedAt = DateTime.Now;
        Store.SaveAll(list);
        Console.WriteLine("已更新:");
        PrintRecord(c);
        WarnDuplicateTitle(c, list);
        return 0;
    }

    private static int CmdDelete(string[] a)
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
        var proc = Launcher.Launch(c, out var err2);
        if (proc == null) { Console.WriteLine("错误: " + err2); return 1; }
        Console.WriteLine("已直接启动（该实例不会被跟踪，也不会出现在托盘菜单中）。pid=" + proc.Id);
        return 0;
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
        int r = TryQchostCall(new IpcMessage { Op = "run", Key = c.Id, Mode = useJob ? "job" : "pid" },
            connectTimeoutMs, out var gr);
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
                + (string.IsNullOrWhiteSpace(inst.Proj) ? "" : inst.Proj + " > ") + inst.Title);
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
        Console.WriteLine("  proj     : " + (string.IsNullOrEmpty(c.Proj) ? "(空)" : c.Proj));
        Console.WriteLine("  workdir  : " + (string.IsNullOrEmpty(c.Workdir) ? "(默认=用户目录)" : c.Workdir));
        Console.WriteLine("  run      : " + c.KindText + (string.IsNullOrEmpty(c.Args) ? "" : "  " + c.Args));
        Console.WriteLine("  interp   : " + (string.IsNullOrWhiteSpace(c.Interpreter)
            ? (string.IsNullOrWhiteSpace(c.ShellScript) ? "(不适用)" : "(默认: 按扩展名 → 系统 PATH)")
            : c.Interpreter));
        Console.WriteLine("  keep     : " + (c.KeepOpen ? "窗口保持" : "退出即关"));
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
数据文件: %APPDATA%\quickcmd\commands.jsonl

用法:
  quickcmd                                  无参数 = 显示本帮助
  quickcmd add --title <名> (--program <程序> | --shell-script <脚本>) [选项]
  quickcmd list [--proj <项目>] [--json]
  quickcmd get <short-id|id|title> [--json]
  quickcmd update <short-id|id|title> [选项]
  quickcmd delete <short-id|id|title> [-y]
  quickcmd run <short-id|id|title>         以 [PID] 模式运行（按 root pid 进程树跟踪）
  quickcmd run-job <short-id|id|title>     以 [JOB] 模式运行（作业对象整树跟踪，推荐）
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
  --close-on-exit          命令结束后关闭窗口（默认保持窗口: python 用 -i、ps1 用 -NoExit、bat 用 /k）

示例:
  quickcmd add --title "Dev Server" --proj myapp --workdir D:\code\myapp --program "npm" --args "run dev"
  quickcmd add --title "数据同步" --workdir D:\jobs --shell-script D:\jobs\sync.py --interpreter "D:\py312;D:\py311"
  quickcmd add --title "记事本" --program notepad.exe --max-instances 2
  quickcmd update 1a2b3c4d --max-instances 3
  quickcmd run 1a2b3c4d          # [PID] 模式
  quickcmd run-job 1a2b3c4d      # [JOB] 模式
  quickcmd delete 1a2b3c4d -y

说明:
  - short id 是 GUID 前 8 位；get/update/delete/run/run-job 都可用 short-id、完整 id 或 title 定位
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

