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

        using var pruneTimer = new Timer(_ =>
        {
            try { Manager.Prune(); } catch { }
        }, null, 2000, 2000);

        ExitSignal.Wait(); // 收到 IPC shutdown 后退出；已启动的命令/作业不受影响
        return 0;
    }

    public static void RequestExit() => ExitSignal.Set();
}
