// ============================================================================
// quickcmd — Native.cs: Win32 互操作
// ============================================================================

using System.Runtime.InteropServices;
using System.Text;

namespace QuickCmd;

// ---------------------------------------------------------------------------
// Win32 互操作
// ---------------------------------------------------------------------------
internal static class Native
{
    [DllImport("kernel32.dll")] public static extern bool FreeConsole();

    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr hObject);

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    // ---- 进程创建（挂起）与作业对象 ----
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFOW
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessW(
        string lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")] public static extern uint ResumeThread(IntPtr hThread);
    [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
    [DllImport("kernel32.dll")] public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool QueryInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength, out uint lpReturnLength);

    // 注：曾尝试 kernel32/kernelbase ordinal #793（"GetJobObjectInfo"）取作业 KMTID，
    // 实测对新建匿名作业返回全零结构，拿不到对象 ID——作业序号改由 Job.Create 自增分配。
    // 验证脚本：traycmd/testdata/probe-jobid.ps1

    public const uint CREATE_SUSPENDED = 0x00000004;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const int JobObjectBasicProcessIdListInformation = 3;
    // JOBOBJECTINFOCLASS 枚举从 1 开始：Accounting=1, BasicLimit=2, ProcessIdList=3, ...
    public const int JobObjectBasicAccountingInformation = 1;

    // 作业内当前活跃进程 pid 列表（变长结构，用原始 buffer 手工读取，见 Job.ActivePids）
    // 布局：offset 0 = NumberOfAssignedProcesses(DWORD), 4 = NumberOfProcessIdsInList(DWORD),
    //       8 = 第一个 ULONG_PTR pid（x64 上 8 字节对齐，与偏移 8 吻合）


    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    // 全系统进程快照条目（pid / 父 pid / exe 名），ps 组进程树用
    public readonly struct ProcEntry
    {
        public readonly int Pid; public readonly int Ppid; public readonly string Name;
        public ProcEntry(int pid, int ppid, string name) { Pid = pid; Ppid = ppid; Name = name; }
    }

    public static List<ProcEntry> AllProcesses()
    {
        var list = new List<ProcEntry>();
        var h = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (h == IntPtr.Zero || h == (IntPtr)(-1)) return list;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W)) };
            if (Process32FirstW(h, ref e))
                do list.Add(new ProcEntry(unchecked((int)e.th32ProcessID), unchecked((int)e.th32ParentProcessID), e.szExeFile));
                while (Process32NextW(h, ref e));
        }
        finally { CloseHandle(h); }
        return list;
    }

    // 返回 rootPid 及其全部子孙 pid（用于跨 Windows Terminal 归属窗口、整树杀进程）
    public static HashSet<int> DescendantsAndSelf(int rootPid) => DescendantsOf(new[] { rootPid });

    // 多种子版：rootPid + 作业成员等一起向下扩。子进程即使父进程已退出，ppid 归属
    // 仍留在进程快照里，因此能追到"launcher 自退后经 shell/商店别名代理拉起"的逃逸进程
    public static HashSet<int> DescendantsOf(IEnumerable<int> seeds)
    {
        var result = new HashSet<int>(seeds);
        var parentToChild = new Dictionary<int, List<int>>();
        var h = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (h == IntPtr.Zero || h == (IntPtr)(-1)) return result;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W)) };
            if (Process32FirstW(h, ref e))
            {
                do
                {
                    int pid = unchecked((int)e.th32ProcessID);
                    int ppid = unchecked((int)e.th32ParentProcessID);
                    if (!parentToChild.TryGetValue(ppid, out var l)) parentToChild[ppid] = l = new List<int>();
                    l.Add(pid);
                } while (Process32NextW(h, ref e));
            }
        }
        finally { CloseHandle(h); }

        var queue = new Queue<int>(result);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (parentToChild.TryGetValue(cur, out var kids))
                foreach (var k in kids) if (result.Add(k)) queue.Enqueue(k);
        }
        return result;
    }
}

