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

    private void BringFront(int pid)
    {
        if (Call(new IpcMessage { Op = "front", Key = pid.ToString() }, 3000, out _) != 0) _hostUp = false;
    }

    private void KillInstance(int pid)
    {
        if (Call(new IpcMessage { Op = "kill", Key = pid.ToString() }, 3000, out _) != 0) _hostUp = false;
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
        var byCmd = new Dictionary<string, List<InstanceInfo>>();
        foreach (var i in snap)
        {
            var key = i.ShortId ?? "";
            if (!byCmd.TryGetValue(key, out var l)) byCmd[key] = l = new List<InstanceInfo>();
            l.Add(i);
        }

        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripMenuItem("quickcmd · " + list.Count + " 条命令") { Enabled = false });
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

        if (list.Count == 0)
        {
            _menu.Items.Add(new ToolStripMenuItem("（暂无命令，用 quickcmd add 注册）") { Enabled = false });
        }
        else
        {
            // 平铺渲染（与命令面板一致）：命令项标签本身为 "proj > title"，同项目按 proj 排序自然相邻
            var ordered = list
                .OrderBy(c => (c.Proj ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase);
            foreach (var c in ordered) _menu.Items.Add(BuildCommandItem(c, byCmd));
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

    // 命令项：点击 = 运行（IPC→qchost，JOB 模式）；有运行中实例时展开直接显示实例
    private ToolStripMenuItem BuildCommandItem(CommandDef c, Dictionary<string, List<InstanceInfo>> byCmd)
    {
        var insts = byCmd.TryGetValue(c.ShortId, out var l) ? l : new List<InstanceInfo>();
        var projPrefix = string.IsNullOrWhiteSpace(c.Proj) ? "" : c.Proj.Trim() + " > ";
        var label = (insts.Count > 0 ? "● " : "○ ") + projPrefix + c.Title
                    + (insts.Count > 0 ? "  [" + insts.Count + "/" + c.MaxInstances + "]" : "");
        var item = new ToolStripMenuItem(label);
        item.Click += (s, e) => RunCommand(c);

        // 提取并设置图标（使用缓存）
        if (!_iconCache.TryGetValue(c.Id, out var icon))
        {
            icon = IconExtractor.GetIcon(c);
            if (icon != null)
                _iconCache[c.Id] = icon;
        }

        if (icon != null)
        {
            item.Image = icon.ToBitmap();
        }

        if (insts.Count > 0)
        {
            foreach (var i in insts) item.DropDownItems.Add(BuildInstanceItem(i, c));
            if (insts.Count > 1)
            {
                var killAll = new ToolStripMenuItem("结束全部实例");
                killAll.Click += (s, e) =>
                {
                    foreach (var i in insts) KillInstance(i.Id);
                };
                item.DropDownItems.Add(killAll);
            }
        }
        return item;
    }

    // 实例项：点击 = 置前；展开 = Proj name / Open workdir / Bring front / Kill
    private ToolStripMenuItem BuildInstanceItem(InstanceInfo i, CommandDef cmd)
    {
        var mins = (long)(DateTime.Now - i.Started).TotalMinutes;
        var dur = mins < 1 ? "刚刚" : mins + " 分钟";
        var item = new ToolStripMenuItem("[" + i.Mode + "] pid " + i.Id + " · " + i.Started.ToString("HH:mm:ss") + " · " + dur
            + (i.RootExited ? " · root 已退" : ""));
        item.Click += (s, e) => BringFront(i.Id);

        var info = new ToolStripMenuItem(
            "Proj: " + (string.IsNullOrWhiteSpace(cmd.Proj) ? "(未设置)" : cmd.Proj)
            + " · [" + i.Mode + "] pid " + i.Id)
        { Enabled = false };
        item.DropDownItems.Add(info);

        var open = new ToolStripMenuItem("Open work directory");
        open.Click += (s, e) =>
        {
            var dir = Launcher.EffectiveWorkdir(cmd);
            try
            {
                if (Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            }
            catch { }
        };
        item.DropDownItems.Add(open);

        var front = new ToolStripMenuItem("Bring window to front");
        front.Click += (s, e) => BringFront(i.Id);
        item.DropDownItems.Add(front);

        var kill = new ToolStripMenuItem(
            (i.Mode == "JOB" ? "Kill whole process tree" : "Kill process") + " (pid " + i.Id + ")");
        kill.Click += (s, e) => KillInstance(i.Id);
        item.DropDownItems.Add(kill);

        return item;
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
