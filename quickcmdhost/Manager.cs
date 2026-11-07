// ============================================================================
// quickcmd — Manager.cs: 实例（一次运行）、窗口定位与实例管理器
// ============================================================================

using System.Diagnostics;
using System.Text;

namespace QuickCmd;

// ---------------------------------------------------------------------------
// 实例（一次运行）与窗口定位
// ---------------------------------------------------------------------------
internal sealed class Instance
{
    public CommandDef Cmd;
    public int Pid;           // 启动时的 root pid（内部用：进程对象/树匹配/置前通知）
    public string Id;         // 对外实例 ID：JOB = "job-<数字>"，PID = root pid 字符串
    public Process Proc;      // root 进程；可能为 null（root 在跟踪接管前就退出了）
    public Job Job;           // 作业对象；null = 退化模式（只能按 pid 树跟踪）
    public IntPtr Hwnd = IntPtr.Zero;
    public DateTime Started = DateTime.Now;
    public bool Dead;
    public bool Killed;       // 用户主动结束 → finish 事件记 killed（否则 exited）
    public int ExitCode = int.MinValue; // root 退出码（拿不到保持 MinValue）
    public int RequesterPid;  // 发起这次执行的 CLI/托盘进程（审计 finish 也带上）
    public string ModeName;   // 创建时的跟踪模式快照（kill 会先释放 Job，事后取会误判成 PID）

    public void ReleaseJob()
    {
        Job?.Dispose();
        Job = null;
    }
}

internal static class WindowFinder
{
    public static IntPtr FindFor(Instance inst)
    {
        // 1) 直接用 Process.MainWindowHandle（cmd / notepad 等绝大多数情况）
        try
        {
            if (inst.Proc != null && !inst.Proc.HasExited)
            {
                inst.Proc.Refresh();
                if (inst.Proc.MainWindowHandle != IntPtr.Zero) return inst.Proc.MainWindowHandle;
            }
        }
        catch { }

        // 2) 兜底：枚举作业内活跃 pid + 沿 ppid 归属链向下扩出的逃逸子孙（作业模式下，
        //    root 退出后依然准确；商店 App Execution Alias / shell 代理拉起的进程不进作业，
        //    但 ppid 仍记着作业内父进程），或 pid 子孙树（退化模式）
        HashSet<int> pids;
        try
        {
            if (inst.Job != null)
            {
                var seeds = inst.Job.ActivePids();
                seeds.Add(inst.Pid); // root 刚退出时 pid 仍可能持有窗口
                pids = Native.DescendantsOf(seeds);
            }
            else pids = Native.DescendantsAndSelf(inst.Pid);
        }
        catch { return IntPtr.Zero; }

        IntPtr found = IntPtr.Zero;
        try
        {
            Native.EnumWindows((h, l) =>
            {
                if (!Native.IsWindowVisible(h)) return true;
                Native.GetWindowThreadProcessId(h, out var wpid);
                if (wpid == 0 || !pids.Contains(unchecked((int)wpid))) return true;
                var sb = new StringBuilder(256);
                Native.GetWindowText(h, sb, 256);
                if (sb.Length == 0) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
        }
        catch { }
        return found;
    }
}

// ---------------------------------------------------------------------------
// 实例管理器（仅 qchost 进程使用）
// ---------------------------------------------------------------------------
internal static class Manager
{
    private static readonly object Gate = new();
    private static readonly List<Instance> Instances = new();

    // qchost 用来发气泡通知
    public static Action<string, string> Notify = (t, m) => { };

    public static List<Instance> ForCommand(string cmdId)
    {
        lock (Gate)
            return Instances.Where(i => i.Cmd.Id == cmdId && !i.Dead).ToList();
    }

    private static List<Instance> Live()
    {
        lock (Gate)
            return Instances.Where(i => !i.Dead).ToList();
    }

    // ps 快照：每个实例带真实进程树。JOB → 作业成员 pid（整棵树，root 提前退出
    // 也仍在树里）；PID → root pid 子孙树。
    public static List<InstanceInfo> Snapshot()
    {
        Prune();
        var live = Live();
        var result = new List<InstanceInfo>();
        if (live.Count == 0) return result;

        var byPid = new Dictionary<int, Native.ProcEntry>();
        foreach (var p in Native.AllProcesses()) byPid[p.Pid] = p;

        foreach (var i in live)
        {
            HashSet<int> members, set;
            try
            {
                members = i.Job != null ? i.Job.ActivePids() : new HashSet<int> { i.Pid };
                var seeds = new HashSet<int>(members);
                seeds.Add(i.Pid);
                set = Native.DescendantsOf(seeds); // 作业成员/子孙 + ppid 链上的逃逸进程
            }
            catch { members = set = new HashSet<int> { i.Pid }; }

            var tree = new List<ProcNode>();
            foreach (var pid in set)
                if (byPid.TryGetValue(pid, out var pe))
                    tree.Add(new ProcNode { Pid = pe.Pid, Ppid = pe.Ppid, Name = pe.Name });

            result.Add(new InstanceInfo
            {
                Id = i.Id,
                Mode = ModeOf(i),
                ShortId = i.Cmd.ShortId,
                Title = i.Cmd.Title,
                Proj = i.Cmd.Proj,
                Started = i.Started,
                RootExited = i.Job != null && !members.Contains(i.Pid),
                Tree = tree,
            });
        }
        return result;
    }

    // kill/front 定位：JOB 实例用 "job-<数字>" 实例 ID 精确匹配；数字 key 按 root pid
    // 精确匹配（兼容旧用法）；否则允许传树内任意子 pid——JOB 实例命中成员即定位到
    // 其作业（击杀整棵树），PID 实例命中子孙同理。
    public static Instance FindInstance(string key)
    {
        key = (key ?? "").Trim();
        if (key.Length == 0) return null;
        var live = Live();

        if (key.StartsWith("job-", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var i in live)
                if (string.Equals(i.Id, key, StringComparison.OrdinalIgnoreCase)) return i;
        }

        if (int.TryParse(key, out var pid))
        {
            foreach (var i in live) if (i.Pid == pid) return i;
            foreach (var i in live)
            {
                try
                {
                    if (i.Job != null) { var s = i.Job.ActivePids(); s.Add(i.Pid); if (Native.DescendantsOf(s).Contains(pid)) return i; }
                    else if (Native.DescendantsAndSelf(i.Pid).Contains(pid)) return i;
                }
                catch { }
            }
        }
        return null;
    }

    // 实例是否仍存活：
    //   作业模式 → 作业内还有活跃进程即存活（即便 root/launcher 已退出，孙进程仍算数）；
    //     作业空了但 ppid 归属链上还能追到子孙（商店别名/shell 代理拉起的逃逸进程，
    //     如 bat 里 start 记事本）也算存活；
    //   退化模式（无作业）→ 沿用旧的 root pid 存活判断。
    private static bool IsAlive(Instance i)
    {
        if (i.Job != null)
        {
            if (!i.Job.IsCompleted) return true;
            try
            {
                var seeds = i.Job.ActivePids(); // 通常已空
                seeds.Add(i.Pid);
                var full = Native.DescendantsOf(seeds);
                return full.Count > seeds.Count; // 有逃逸子孙挂着
            }
            catch { return false; }
        }

        bool exited = false;
        try { exited = i.Proc != null && i.Proc.HasExited; }
        catch { exited = false; }
        if (!exited && i.Proc == null)
        {
            try { Process.GetProcessById(i.Pid).Dispose(); }
            catch { exited = true; } // pid 已不存在
        }
        return !exited;
    }

    public static void Prune()
    {
        lock (Gate)
        {
            foreach (var i in Instances)
            {
                if (i.Dead) continue;
                if (!IsAlive(i))
                {
                    i.Dead = true;
                    CaptureExitCode(i);
                    AppendFinish(i, i.Killed ? "killed" : "exited");
                    i.ReleaseJob(); // 作业已空，关闭句柄
                }
            }
            Instances.RemoveAll(i => i.Dead);
        }
    }

    // root 退出码：作业模式用保留下来的进程句柄（root 早退也拿得到），
    // 退化模式退回 Process.ExitCode
    private static void CaptureExitCode(Instance i)
    {
        try
        {
            if (i.Job != null && i.Job.RootProcessHandle != IntPtr.Zero
                && Native.GetExitCodeProcess(i.Job.RootProcessHandle, out var code))
                i.ExitCode = unchecked((int)code);
            else if (i.Proc != null && i.Proc.HasExited)
                i.ExitCode = i.Proc.ExitCode;
        }
        catch { }
    }

    // 审计收尾：一次执行在历史里留下 start + finish 两条（ExecId 关联）
    private static void AppendFinish(Instance i, string status)
    {
        try
        {
            History.Append(new HistoryEntry
            {
                Event = "finish",
                Kind = i.Cmd != null && i.Cmd.Id.StartsWith("adhoc-", StringComparison.OrdinalIgnoreCase) ? "exec" : "run",
                Source = "qchost",
                ExecId = i.Cmd?.Id ?? "",
                Title = i.Cmd?.Title ?? "",
                ShortId = i.Cmd?.ShortId ?? "",
                CommandLine = History.CommandLineOf(i.Cmd),
                Workdir = i.Cmd != null ? Launcher.EffectiveWorkdir(i.Cmd) : "",
                Mode = ModeOf(i),
                InstanceId = i.Id,
                Pid = i.Pid,
                Status = status,
                ExitCode = i.ExitCode,
                DurationMs = (long)Math.Max(0, (DateTime.Now - i.Started).TotalMilliseconds),
                RequesterPid = i.RequesterPid,
            }, out _);
        }
        catch { }
    }

    // 实例跟踪模式：Job 句柄非空 = JOB（作业对象整树跟踪），否则 = PID（root pid 树跟踪）。
    // 用创建时快照：kill/Prune 会先释放 Job，事后按句柄判断会把已结束的 JOB 记成 PID。
    public static string ModeOf(Instance i) =>
        !string.IsNullOrEmpty(i.ModeName) ? i.ModeName : (i.Job != null ? "JOB" : "PID");

    // 返回 false 时 error=="max" 表示已达实例上限（已自动置前现有实例）；
    // 成功时 mode 为实际生效的跟踪模式："JOB"（作业整树）或 "PID"（root pid 树，
    // 含 useJob=true 但作业不可用而退化时）
    public static bool Start(CommandDef cmd, bool useJob, int requesterPid, out string error, out int pid, out string mode, out string id)
    {
        error = ""; pid = 0; mode = ""; id = "";

        var live = ForCommand(cmd.Id);
        if (live.Count >= Math.Max(1, cmd.MaxInstances))
        {
            BringFront(live[0]);
            Notify("quickcmd", "「" + cmd.Title + "」已达最大实例数 " + cmd.MaxInstances + "，已将现有窗口置前。");
            error = "max";
            return false;
        }

        var res = Launcher.LaunchTracked(cmd, useJob, out var err);
        if (res == null) { error = err; return false; }

        var inst = new Instance
        {
            Cmd = cmd, Pid = res.Pid, Proc = res.Proc, Job = res.Job,
            Id = res.Job != null ? res.Job.Id : res.Pid.ToString(),
            RequesterPid = requesterPid,
            ModeName = res.Job != null ? "JOB" : "PID",
        };
        lock (Gate) Instances.Add(inst);
        pid = inst.Pid;
        mode = ModeOf(inst);
        id = inst.Id;
        Notify("quickcmd", "已启动「" + cmd.Title + "」 [" + mode + "] " + inst.Id);

        // 后台寻找窗口句柄（窗口可能要过一会儿才出现）
        var t = new Thread(() =>
        {
            for (int i = 0; i < 12; i++)
            {
                // 作业模式下即使 root 秒退，孙进程可能稍后才弹窗，故按“整树存活”判断
                if (!IsAlive(inst)) return;
                var h = WindowFinder.FindFor(inst);
                if (h != IntPtr.Zero) { inst.Hwnd = h; return; }
                Thread.Sleep(500);
            }
        });
        t.IsBackground = true;
        t.Start();
        return true;
    }

    public static void BringFront(Instance inst)
    {
        var h = inst.Hwnd != IntPtr.Zero ? inst.Hwnd : WindowFinder.FindFor(inst);
        if (h == IntPtr.Zero)
        {
            Notify("quickcmd", "未找到「" + inst.Cmd.Title + "」的窗口（pid=" + inst.Pid + "）。");
            return;
        }
        inst.Hwnd = h;
        try
        {
            Native.ShowWindow(h, Native.IsIconic(h) ? 9 /*SW_RESTORE*/ : 5 /*SW_SHOW*/);
            if (!Native.SetForegroundWindow(h))
            {
                // 前台锁定绕过：模拟一次 Alt 击键
                Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);
                Native.SetForegroundWindow(h);
                Native.keybd_event(0x12, 0, 2 /*KEYEVENTF_KEYUP*/, UIntPtr.Zero);
            }
            Notify("quickcmd", "已将「" + inst.Cmd.Title + "」的窗口置前（pid=" + inst.Pid + "）。");
        }
        catch { }
    }

    public static void Kill(Instance inst)
    {
        try
        {
            inst.Killed = true;
            if (inst.Job != null)
            {
                // 整树击杀：覆盖“launcher 已退出、孙进程还活着”的情况
                var seeds = inst.Job.ActivePids();
                seeds.Add(inst.Pid);
                var escaped = Native.DescendantsOf(seeds); // 含作业成员与 ppid 链上的逃逸进程
                inst.Job.TerminateAll();
                CaptureExitCode(inst); // 句柄还在，先取退出码再释放
                foreach (var pid in escaped)
                {
                    if (seeds.Contains(pid)) continue; // 作业内的由 TerminateAll 处理
                    try { Process.GetProcessById(pid).Kill(true); } catch { }
                }
                inst.ReleaseJob();
            }
            else if (inst.Proc != null && !inst.Proc.HasExited) inst.Proc.Kill(true);
            else Process.GetProcessById(inst.Pid).Kill(true);
            Notify("quickcmd", "已结束「" + inst.Cmd.Title + "」 " + inst.Id);
        }
        catch (Exception ex) { Notify("quickcmd", "结束失败: " + ex.Message); }
        finally
        {
            // kill 路径不会走 Prune 的“刚死”分支（Dead 已置），这里主动补 finish 审计
            try { CaptureExitCode(inst); } catch { }
            inst.Dead = true;
            AppendFinish(inst, "killed");
            Prune();
        }
    }
}

