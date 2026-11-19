// ============================================================================
// quickcmd-host — Program.cs: qchost（quick command host）入口
//   纯 headless 服务端：单实例互斥 + IPC 服务端 + 实例跟踪修剪。
//   所有界面（托盘/菜单/命令面板）在 quickcmd-app（qcApp.exe）。
//   互斥体名中的 "quickrun.gui" 是存量协议名不改，避免与旧版宿主互不识别。
// ============================================================================

namespace QuickCmd;

internal static class Program
{
    private static readonly ManualResetEventSlim ExitSignal = new(false);
    private static Mutex _mutex;

    private static int Main()
    {
        bool created;
        _mutex = new Mutex(true, @"Local\quickrun.gui", out created);
        if (!created) return 0; // 已有 qchost 在跑

        Store.EnsureFile();

        var server = new Thread(IpcServer.ServeLoop) { IsBackground = true };
        server.Start();

        // 本地 Web 控制台（127.0.0.1，端口见 settings.json）；起不来不影响宿主
        WebConsole.Start();

        // web + --auto-start 的命令：宿主启动后自动拉起（失败只记审计，不影响宿主）
        ThreadPool.QueueUserWorkItem(_ => AutoStartWeb());

        using var pruneTimer = new Timer(_ =>
        {
            try { Manager.Prune(); } catch { }
        }, null, 2000, 2000);

        ExitSignal.Wait(); // 收到 IPC shutdown 后退出；已启动的命令/作业不受影响
        return 0;
    }

    public static void RequestExit() => ExitSignal.Set();

    // web + AutoStart：qchost 启动时把站点拉起来，并像 IPC 启动一样写一条 start 审计
    private static void AutoStartWeb()
    {
        try
        {
            var list = Store.Load(out _);
            foreach (var c in list.Where(x => x.IsWeb && x.AutoStart))
            {
                if (Manager.ForCommand(c.Id).Count > 0) continue;
                if (!Manager.Start(c, true, 0, out var err, out var pid, out var mode, out var id, out var log)) continue;

                History.Append(new HistoryEntry
                {
                    Event = "start",
                    Kind = "run",
                    Source = "qchost-auto",
                    ExecId = c.Id,
                    Title = c.Title,
                    ShortId = c.ShortId,
                    CommandLine = History.CommandLineOf(c),
                    Workdir = Launcher.EffectiveWorkdir(c),
                    Mode = mode,
                    InstanceId = id,
                    Pid = pid,
                    Status = "started",
                    LogPath = log ?? "",
                }, out _);
            }
        }
        catch { }
    }
}
