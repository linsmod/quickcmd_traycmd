// ============================================================================
// quickcmd-app — TrayContext.cs: 托盘 UI 客户端
//   托盘图标 + 动态菜单 + 命令面板 + 全局热键。
//   本身不启动/跟踪任何进程：所有动作经 Ipc 发给 qchost（headless 服务端）。
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

public sealed class TrayContext : ApplicationContext
{
    private readonly Form _helper;                    // 隐藏窗口：用于前台权限 / 菜单点外关闭
    private readonly NotifyIcon _ni;
    private readonly ContextMenuStrip _menu;
    private CommandPaletteForm _paletteForm;
    private HotKeyKit.GlobalHotKey _hotkey;
    private readonly Dictionary<string, Icon> _iconCache;
    private bool _hostUp;

    public TrayContext()
    {
        Store.EnsureFile();
        _iconCache = new Dictionary<string, Icon>();

        _helper = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-3000, -3000),
            Opacity = 0,
        };
        _ = _helper.Handle; // 强制创建句柄

        // 全局触发键：长按 Win ≥500ms 弹出命令面板（HotKeyKit 低级键盘钩子；
        // 短按 Win / Win+组合键不受影响）
        _hotkey = new HotKeyKit.GlobalHotKey
        {
            CallbackContext = SynchronizationContext.Current,
        };
        _hotkey.WinKeyLongPress += ShowCommandPalette;
        _hotkey.EnableWinLongPress(500);

        _menu = new ContextMenuStrip();
        _menu.Opening += (s, e) => RebuildMenu(); // 每次打开重建：天然感知 CLI / app 的增删改与实例变化

        _ni = new NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "quickcmd",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        _ni.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left && e.Clicks == 1)
            {
                Native.SetForegroundWindow(_helper.Handle); // 保证点击菜单外部时菜单能关闭
                _menu.Show(Cursor.Position);
            }
        };
        _ni.MouseDoubleClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _menu.Close(); // 双击的第一次单击可能已弹出菜单
                ShowCommandPalette();
            }
        };

        _hostUp = EnsureHostRunning();
        RebuildMenu(); // 启动时预填菜单：空菜单首次右键时 Opening 会被 WinForms 置 Cancel=true，导致菜单要点两次才弹出
    }

    // ----- IPC 辅助 -----

    // 返回：0=在线 1=中断/无响应 2=qchost 不在
    private static int Call(IpcMessage msg, int timeoutMs, out QchostResponse resp) =>
        Ipc.TryCall(msg, timeoutMs, out resp, out _);

    // qchost 不在就拉起并等待就绪（菜单里“重新连接”用）
    private static bool EnsureHostRunning() => Ipc.EnsureHost();

    private List<InstanceInfo> Snapshot()
    {
        if (Call(new IpcMessage { Op = "ps" }, 1500, out var resp) != 0 || resp == null || !resp.Ok)
        {
            _hostUp = false;
            return new List<InstanceInfo>();
        }
        _hostUp = true;
        if (string.IsNullOrEmpty(resp.Data)) return new List<InstanceInfo>();
        return JsonSerializer.Deserialize<List<InstanceInfo>>(resp.Data, Store.JsonOpts) ?? new List<InstanceInfo>();
    }

    // ----- 动作（全部发给 qchost 执行）-----

    // 托盘/面板点击运行：默认 JOB 模式（整树跟踪）
    private void RunCommand(CommandDef c)
    {
        int r = Call(new IpcMessage { Op = "run", Key = c.Id, Mode = "job" }, 3000, out _);
        if (r == 2) _hostUp = false;
    }

    private void KillInstance(string id)
    {
        if (Call(new IpcMessage { Op = "kill", Key = id }, 3000, out _) != 0) _hostUp = false;
    }

    public void ShowCommandPalette()
    {
        if (_paletteForm != null && !_paletteForm.IsDisposed)
        {
            _paletteForm.Close();
            _paletteForm = null;
        }

        var commands = Store.Load(out _);
        _paletteForm = new CommandPaletteForm(commands, RunCommand, this);
        _paletteForm.Show();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hotkey?.Dispose();
            _ni?.Dispose();
            _menu?.Dispose();
            _helper?.Dispose();

            // 清理图标缓存
            foreach (var icon in _iconCache.Values)
                icon?.Dispose();
            _iconCache.Clear();
        }
        base.Dispose(disposing);
    }

    // ----- 菜单 -----

    private void RebuildMenu()
    {
        var list = Store.Load(out var bad);
        var snap = _hostUp ? Snapshot() : new List<InstanceInfo>();

        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripMenuItem("quickcmd"
            + (_hostUp ? " · " + snap.Count + " 个实例" : "")) { Enabled = false });
        if (!_hostUp)
        {
            var retry = new ToolStripMenuItem("⚠ qchost 未连接（点击重连）");
            retry.Click += (s, e) =>
            {
                _hostUp = true; // AppUI 启动前已确保 qchost 在线
            };
            _menu.Items.Add(retry);
        }
        if (bad > 0)
            _menu.Items.Add(new ToolStripMenuItem("⚠ 数据文件有 " + bad + " 行无法解析") { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());

        // 第一级 = 运行中的实例（运行命令走命令面板：双击托盘 / 长按 Win）。
        // 菜单不再平铺命令列表，避免 命令→实例→操作 三层嵌套。
        if (_hostUp)
        {
            if (snap.Count == 0)
                _menu.Items.Add(new ToolStripMenuItem("暂无实例或已逃逸") { Enabled = false });
            else
                foreach (var i in snap.OrderBy(x => x.Started))
                    _menu.Items.Add(BuildInstanceItem(i, list));
        }

        _menu.Items.Add(new ToolStripSeparator());
        var dataItem = new ToolStripMenuItem("打开数据文件");
        dataItem.Click += (s, e) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + Store.FilePath + "\"")
                {
                    UseShellExecute = true,
                });
            }
            catch { }
        };
        _menu.Items.Add(dataItem);

        var skillItem = new ToolStripMenuItem(SkillResource.Available() ? "安装 Agent Skill" : "安装 Agent Skill（不可用）");
        skillItem.Enabled = SkillResource.Available();
        skillItem.Click += (s, e) => InstallSkillFromMenu();
        _menu.Items.Add(skillItem);

        var startupItem = new ToolStripMenuItem("开机启动")
        {
            CheckOnClick = true,
            Checked = StartupManager.IsEnabled()
        };
        startupItem.CheckedChanged += (s, e) => StartupManager.SetEnabled(startupItem.Checked);
        _menu.Items.Add(startupItem);

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (s, e) => ExitApp();
        _menu.Items.Add(exitItem);
    }

    // 托盘"安装 Agent Skill"：把嵌入资源写到 ~/.agents/skills/traycmd/SKILL.md
    // 已存在时弹框确认覆盖（等价 CLI 的 -y）
    private void InstallSkillFromMenu()
    {
        bool overwrite = true;
        if (SkillResource.Exists())
        {
            var r = MessageBox.Show(
                "已存在：\n" + SkillResource.TargetFile +
                "\n\n要覆盖吗？（取消 = 保留现有文件）",
                "安装 Agent Skill", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return;
            overwrite = r == DialogResult.Yes;
        }
        if (!overwrite)
        {
            MessageBox.Show("已保留现有文件，未改动。", "安装 Agent Skill",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var ok = SkillResource.Install(true, out var err);
        MessageBox.Show(ok ? "已安装：\n" + SkillResource.TargetFile : "安装失败：" + err,
            "安装 Agent Skill", MessageBoxButtons.OK,
            ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    // 实例项（第一级）：前缀直接是实例 ID —— JOB = "[job-N]"，PID = "[pid]"；
    // 后跟**当前活跃 pid**（root 启动 pid 可能已退）· 时长 · root已退标记；
    // 展开 = 结束任务 / 打开文件所在位置 / 打开工作目录
    private ToolStripMenuItem BuildInstanceItem(InstanceInfo i, List<CommandDef> defs)
    {
        var cmd = defs.FirstOrDefault(d => string.Equals(d.ShortId, i.ShortId, StringComparison.OrdinalIgnoreCase));
        int rootPid = int.TryParse(i.Id, out var rp) ? rp : -1;

        var livePids = (i.Tree ?? new List<ProcNode>()).Select(n => n.Pid).Distinct()
            .OrderBy(p => p == rootPid ? 0 : 1).ThenBy(p => p).ToList();
        var pidList = "pid " + string.Join(",", livePids.Take(4)) + (livePids.Count > 4 ? "…" : "");

        var span = DateTime.Now - i.Started;
        var dur = span.TotalMinutes < 1 ? "刚刚" : span.TotalHours < 1 ? (long)span.TotalMinutes + " 分" : (long)span.TotalHours + " 小时";
        var projPrefix = string.IsNullOrWhiteSpace(i.Proj) ? "" : i.Proj.Trim() + " > ";
        var isJob = i.Mode == "JOB";
        var label = (isJob ? "[" + i.Id + "] " : "[pid] ") + projPrefix + i.Title
            + " · " + (livePids.Count > 0 ? pidList : isJob ? "无活跃pid" : "pid " + i.Id + " 已退");
        if (i.RootExited) label += " · root已退";
        var item = new ToolStripMenuItem(label + " · " + dur);

        if (cmd != null)
        {
            if (!_iconCache.TryGetValue(cmd.Id, out var icon))
            {
                icon = IconExtractor.GetIcon(cmd);
                if (icon != null) _iconCache[cmd.Id] = icon;
            }
            if (icon != null) item.Image = icon.ToBitmap();
        }

        var kill = new ToolStripMenuItem("结束任务" + (i.Mode == "JOB" ? "（整棵作业树）" : "（进程树）"));
        kill.Click += (s, e) => KillInstance(i.Id);
        item.DropDownItems.Add(kill);

        var fileItem = new ToolStripMenuItem("打开文件所在位置");
        var filePath = ResolveTargetFile(i, cmd);
        if (filePath == null) fileItem.Enabled = false;
        else fileItem.Click += (s, e) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + filePath + "\"") { UseShellExecute = true });
            }
            catch { }
        };
        item.DropDownItems.Add(fileItem);

        var dirItem = new ToolStripMenuItem("打开工作目录");
        if (cmd == null) dirItem.Enabled = false;
        else dirItem.Click += (s, e) =>
        {
            var dir = Launcher.EffectiveWorkdir(cmd);
            try
            {
                if (Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            }
            catch { }
        };
        item.DropDownItems.Add(dirItem);

        return item;
    }

    // 实例的目标文件（任务管理器"打开文件所在位置"语义）：优先运行中进程的主模块路径，
    // root/树内 pid 逐个试（商店记事本的 root 就是 WindowsApps 下真实 exe；经 shell
    // 代理逃逸的孙进程也在树里）；全都拿不到再退回注册的脚本文件 / 绝对路径程序
    private static string ResolveTargetFile(InstanceInfo i, CommandDef cmd)
    {
        int root = int.TryParse(i.Id, out var r) ? r : -1;
        var pids = (i.Tree ?? new List<ProcNode>()).Select(n => n.Pid).Distinct()
            .OrderBy(p => p == root ? 0 : 1).ThenBy(p => p).ToList();
        if (root > 0 && !pids.Contains(root)) pids.Insert(0, root);

        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                var path = p.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
            }
            catch { }
        }

        if (cmd != null)
        {
            if (!string.IsNullOrWhiteSpace(cmd.ShellScript))
            {
                var script = Launcher.Expand(cmd.ShellScript.Trim());
                if (File.Exists(script)) return script;
            }
            if (!string.IsNullOrWhiteSpace(cmd.Program))
            {
                var prog = Launcher.Expand(cmd.Program.Trim());
                if (Path.IsPathRooted(prog) && File.Exists(prog)) return prog;
            }
        }
        return null;
    }

    // ----- 退出 -----

    // 退出 UI，并让 qchost 一并退出（已启动的命令/作业进程不受影响）
    private void ExitApp()
    {
        Call(new IpcMessage { Op = "shutdown" }, 1500, out _);
        _ni.Visible = false;
        _ni.Dispose();
        _menu.Dispose();
        ExitThread();
    }

    private static Icon MakeIcon()
    {
        try
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                using (var bg = new SolidBrush(Color.FromArgb(0, 120, 212)))
                    g.FillRectangle(bg, 0, 0, 32, 32);
                using (var f = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var fg = new SolidBrush(Color.White))
                {
                    var size = g.MeasureString("TC", f);
                    g.DrawString("TC", f, fg, (32 - size.Width) / 2f, (32 - size.Height) / 2f);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
        catch { return SystemIcons.Application; }
    }
}
