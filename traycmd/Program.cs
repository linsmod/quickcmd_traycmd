// ============================================================================
// quickcmd — 系统托盘命令启动器
//
// 项目术语 —— qchost（quick command host）：常驻托盘的宿主进程 quick_run.exe，
//   由本项目 src/ 的 TrayContext 等编译供其引用；负责托盘菜单、实例跟踪、IPC。
//   与它区分：用户注册的命令跑出来的窗口是"目标程序窗口"，不是 qchost。
//
// 一个仓库，两个角色：
//   qchost（quick_run.exe，无参数启动）：托盘常驻，点击菜单项运行命令，
//                      展开可看到运行中的实例（置前 / 打开目录 / 结束进程）。
//   quickcmd <verb> ... （CLI）：对菜单里的命令做增删改查（CRUD）+ run/ps/kill，
//                      供其他 agent / 脚本注册命令。
//
// 数据落盘：%APPDATA%\quickcmd\commands.jsonl（每行一条命令定义，UTF-8）
// ============================================================================

using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace QuickCmd;

internal static class Program
{
    public const string PipeName = "quickcmd.ipc";

    [STAThread]
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        return CliApp.Run(args);
    }
}
