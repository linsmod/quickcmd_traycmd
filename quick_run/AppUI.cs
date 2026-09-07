using System.Diagnostics;
using QuickCmd;

namespace quick_run
{
    // quickcmd-app（qcApp.exe）：常驻托盘的 UI 客户端入口。
    // 单实例互斥后跑 TrayContext（托盘 + 菜单 + 命令面板）；进程执行/跟踪由 qchost 承担。
    internal class AppUI
    {
        public static int Run()
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\quickcmd.app", out created))
            {
                if (!created) return 0;

                // 启动即拉起 qchost；失败则静默退出（退出码 1），不弹任何窗口
                if (!Ipc.EnsureHost())
                {
                    Trace.WriteLine("quickcmd: 无法启动 " + Ipc.HostExeName + "（需与本程序同目录或在 PATH 中）");
                    return 1;
                }

                using (var ctx = new TrayContext()) Application.Run(ctx);
                return 0;
            }
        }
    }
}
