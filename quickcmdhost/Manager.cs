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
    public int Pid;
    public Process Proc;      // root 进程；可能为 null（root 在跟踪接管前就退出了）
    public Job Job;           // 作业对象；null = 退化模式（只能按 pid 树跟踪）
    public IntPtr Hwnd = IntPtr.Zero;
    public DateTime Started = DateTime.Now;
    public bool Dead;

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

        // 2) 兜底：枚举作业内活跃 pid（作业模式，root 退出后依然准确；
        //    某些情况下窗口不属于被启动的 pid，如 Windows Terminal），
        //    或 pid 子孙树（退化模式）
        HashSet<int> pids;
        try
        {
            if (inst.Job != null)
            {
                pids = inst.Job.ActivePids();
                pids.Add(inst.Pid); // root 刚退出时 pid 仍可能持有窗口
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
            HashSet<int> set;
            try { set = i.Job != null ? i.Job.ActivePids() : Native.DescendantsAndSelf(i.Pid); }
            catch { set = new HashSet<int> { i.Pid }; }

            var tree = new List<ProcNode>();
            foreach (var pid in set)
                if (byPid.TryGetValue(pid, out var pe))
                    tree.Add(new ProcNode { Pid = pe.Pid, Ppid = pe.Ppid, Name = pe.Name });

            result.Add(new InstanceInfo
            {
                Id = i.Pid,
                Mode = ModeOf(i),
                ShortId = i.Cmd.ShortId,
                Title = i.Cmd.Title,
                Proj = i.Cmd.Proj,
                Started = i.Started,
                RootExited = i.Job != null && !set.Contains(i.Pid),
                Tree = tree,
            });
        }
        return result;
    }

    // kill 定位：优先实例 ID（root pid）精确匹配；否则允许传树内任意子 pid——
    // JOB 实例命中成员即定位到其作业（击杀整棵树），PID 实例命中子孙同理。
    public static Instance FindInstance(int pid)
    {
        var live = Live();
        foreach (var i in live) if (i.Pid == pid) return i;
        foreach (var i in live)
        {
            try
            {
                if (i.Job != null) { if (i.Job.ActivePids().Contains(pid)) return i; }
                else if (Native.DescendantsAndSelf(i.Pid).Contains(pid)) return i;
            }
            catch { }
        }
        return null;
    }

    // 实例是否仍存活：
    //   作业模式 → 作业内还有活跃进程即存活（即便 root/launcher 已退出，孙进程仍算数）；
    //   退化模式（无作业）→ 沿用旧的 root pid 存活判断。
    private static bool IsAlive(Instance i)
    {
        if (i.Job != null) return !i.Job.IsCompleted;

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
                    i.ReleaseJob(); // 作业已空，关闭句柄
                }
            }
            Instances.RemoveAll(i => i.Dead);
        }
    }

    // 实例跟踪模式：Job 句柄非空 = JOB（作业对象整树跟踪），否则 = PID（root pid 树跟踪）
    public static string ModeOf(Instance i) => i.Job != null ? "JOB" : "PID";

    // 返回 false 时 error=="max" 表示已达实例上限（已自动置前现有实例）；
    // 成功时 mode 为实际生效的跟踪模式："JOB"（作业整树）或 "PID"（root pid 树，
    // 含 useJob=true 但作业不可用而退化时）
    public static bool Start(CommandDef cmd, bool useJob, out string error, out int pid, out string mode)
    {
        error = ""; pid = 0; mode = "";

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

        var inst = new Instance { Cmd = cmd, Pid = res.Pid, Proc = res.Proc, Job = res.Job };
        lock (Gate) Instances.Add(inst);
        pid = inst.Pid;
        mode = ModeOf(inst);
        Notify("quickcmd", "已启动「" + cmd.Title + "」 [" + mode + "] pid=" + inst.Pid);

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
            if (inst.Job != null)
            {
                // 整树击杀：覆盖“launcher 已退出、孙进程还活着”的情况
                inst.Job.TerminateAll();
                inst.ReleaseJob();
            }
            else if (inst.Proc != null && !inst.Proc.HasExited) inst.Proc.Kill(true);
            else Process.GetProcessById(inst.Pid).Kill(true);
            Notify("quickcmd", "已结束「" + inst.Cmd.Title + "」 pid=" + inst.Pid);
        }
        catch (Exception ex) { Notify("quickcmd", "结束失败: " + ex.Message); }
        finally
        {
            inst.Dead = true;
            Prune();
        }
    }
}

