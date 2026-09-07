// ============================================================================
// quickcmd — 系统托盘命令启动器
//
// 项目术语 —— qchost（quick command host）：常驻托盘的宿主进程 qchost.exe
//   （独立项目 quickcmd-host），负责托盘菜单、实例跟踪、IPC。
//   与它区分：用户注册的命令跑出来的窗口是"目标程序窗口"，不是 qchost。
//
// 本项目 = quickcmd CLI（quickcmd.exe）：对菜单里的命令做增删改查（CRUD）+
//   run/ps/kill，供其他 agent / 脚本注册命令；公用代码在 quickcmd.shared。
//
// 数据落盘：%APPDATA%\quickcmd\commands.jsonl（每行一条命令定义，UTF-8）
// ============================================================================

using System.Text;

namespace QuickCmd;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        return CliApp.Run(args);
    }
}
