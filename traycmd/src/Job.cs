// ============================================================================
// quickcmd — Job.cs: Windows 作业对象（Job Object）封装：整棵进程树的跟踪与终止
// ============================================================================

using System.Runtime.InteropServices;
using System.Text;

namespace QuickCmd;

// ---------------------------------------------------------------------------
// 作业成员可继承：作业内进程再创建的子进程自动属于同一作业。因此
// “launcher 拉起子进程后自己退出”的场景，孙进程仍在作业内——
//   IsCompleted    最后一个成员进程退出后 job handle 变 signaled（Win8+），
//                  以此判断“整树是否还在”，root 提前退出也能正确跟踪；
//   TerminateAll   整树击杀；
//   ActivePids     作业内当前活跃 pid 列表（窗口定位兜底用，比进程树启发式准）。
// 不设 KILL_ON_JOB_CLOSE：quickcmd 自身退出时不应连带杀掉用户启动的命令。
// ---------------------------------------------------------------------------
internal sealed class Job : IDisposable
{
    public IntPtr Handle { get; private set; }

    private Job(IntPtr h) => Handle = h;

    // 创建匿名作业；失败返回 null（调用方退化回旧的 pid 树跟踪）
    public static Job Create()
    {
        try
        {
            var h = Native.CreateJobObjectW(IntPtr.Zero, null);
            return h == IntPtr.Zero ? null : new Job(h);
        }
        catch { return null; }
    }

    // 挂起创建进程 → 指派进作业 → 恢复运行。
    // 必须在恢复前指派：否则 launcher 可能在入作业前就 fork 出界外子进程。
    // 返回 false 时进程未被启动（或已被终止），win32Error 为原因；
    // 指派失败的典型原因：quickcmd 自身已在别的作业里，新进程出生即属那个作业。
    public bool TryLaunch(string fileName, string args, string workdir,
        Dictionary<string, string> envOverrides, out int pid, out uint win32Error)
    {
        pid = 0; win32Error = 0;
        if (Handle == IntPtr.Zero) { win32Error = 6 /*ERROR_INVALID_HANDLE*/; return false; }

        var cmdLine = QuoteIfNeeded(fileName) + (string.IsNullOrEmpty(args) ? "" : " " + args);
        var si = new Native.STARTUPINFOW { cb = Marshal.SizeOf<Native.STARTUPINFOW>() };
        IntPtr envBlock = BuildEnvBlock(envOverrides);
        uint flags = Native.CREATE_SUSPENDED;
        if (envBlock != IntPtr.Zero) flags |= Native.CREATE_UNICODE_ENVIRONMENT;
        try
        {
            if (!Native.CreateProcessW(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false,
                    flags, envBlock, workdir, ref si, out var pi))
            { win32Error = (uint)Marshal.GetLastWin32Error(); return false; }

            if (!Native.AssignProcessToJobObject(Handle, pi.hProcess))
            {
                win32Error = (uint)Marshal.GetLastWin32Error();
                Native.TerminateProcess(pi.hProcess, 0); // 还未恢复过，终止挂起进程
                Native.CloseHandle(pi.hThread);
                Native.CloseHandle(pi.hProcess);
                return false;
            }

            Native.ResumeThread(pi.hThread);
            Native.CloseHandle(pi.hThread);
            Native.CloseHandle(pi.hProcess); // 进程在作业内的成员关系与此句柄无关
            pid = pi.dwProcessId;
            return true;
        }
        finally { if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock); }
    }

    // 作业内最后一个进程已退出。
    // 首选 accounting 的 ActiveProcesses（实测：父进程自身在别的作业里时，
    // job handle 的 signaled 状态可能不可靠，会一直停在 non-signaled）；
    // accounting 查询失败时退回 handle wait；两者都判定不了则保守认为仍存活。
    public bool IsCompleted
    {
        get
        {
            if (Handle == IntPtr.Zero) return true;
            try
            {
                if (TryActiveProcessCount(out int active)) return active == 0;

                const uint WAIT_OBJECT_0 = 0;
                return Native.WaitForSingleObject(Handle, 0) == WAIT_OBJECT_0;
            }
            catch { return false; }
        }
    }

    private bool TryActiveProcessCount(out int active)
    {
        // JOBOBJECT_BASIC_ACCOUNTING_INFORMATION：
        // 4×LARGE_INTEGER(32B) + TotalPageFaultCount(4) + TotalProcesses(4) + ActiveProcesses(4 @40) + ...
        // ★实测：该 info class 要求 buffer 长度“恰好等于” sizeof(=48)，
        //   传 56/64 反而返回 ERROR_BAD_LENGTH(24)（pid 列表类则接受 ≥ 大小）
        active = 0;
        const int size = 48;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!Native.QueryInformationJobObject(Handle, Native.JobObjectBasicAccountingInformation, buf, size, out _))
                return false;
            active = Marshal.ReadInt32(buf, 40);
            return true;
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // 作业内当前活跃进程的 pid 集合
    public HashSet<int> ActivePids()
    {
        var set = new HashSet<int>();
        if (Handle == IntPtr.Zero) return set;
        const int capacity = 512;
        int size = 8 + IntPtr.Size * capacity;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!Native.QueryInformationJobObject(Handle, Native.JobObjectBasicProcessIdListInformation,
                    buf, (uint)size, out _)) return set;
            int count = Marshal.ReadInt32(buf, 4); // NumberOfProcessIdsInList
            if (count > capacity) count = capacity;
            for (int i = 0; i < count; i++)
                set.Add(unchecked((int)(long)Marshal.ReadIntPtr(buf, 8 + i * IntPtr.Size)));
        }
        catch { }
        finally { Marshal.FreeHGlobal(buf); }
        return set;
    }

    // 结束作业内全部进程（含“launcher 已退、孙进程还活着”的整树）
    public void TerminateAll()
    {
        try { if (Handle != IntPtr.Zero) Native.TerminateJobObject(Handle, 0); } catch { }
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            try { Native.CloseHandle(Handle); } catch { }
            Handle = IntPtr.Zero;
        }
    }

    private static string QuoteIfNeeded(string s) =>
        s != null && s.IndexOf(' ') >= 0 ? "\"" + s + "\"" : s;

    // CreateProcessW 的 Unicode 环境块：当前进程环境 + 覆盖项，
    // 大小写不敏感升序，每项 KEY=VALUE\0，末尾再补一个 \0
    private static IntPtr BuildEnvBlock(Dictionary<string, string> overrides)
    {
        if (overrides == null || overrides.Count == 0) return IntPtr.Zero; // 直接继承父环境
        var merged = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            merged[(string)e.Key] = e.Value?.ToString() ?? "";
        foreach (var kv in overrides) merged[kv.Key] = kv.Value;

        var sb = new StringBuilder();
        foreach (var kv in merged) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        sb.Append('\0');
        var bytes = Encoding.Unicode.GetBytes(sb.ToString());
        var block = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, block, bytes.Length);
        return block;
    }
}
