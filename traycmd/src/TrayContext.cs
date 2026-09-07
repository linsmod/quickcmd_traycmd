// ============================================================================
// quickcmd — TrayContext.cs: qchost（quick command host，托盘宿主进程，
//   即 quick_run.exe）：托盘图标 + 动态菜单 + IPC 服务端 + 实例管理入口
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
    private readonly Form _helper;                    // 隐藏窗口：用于前台权限 / UI 线程封送
    private readonly NotifyIcon _ni;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _pruneTimer;
    private CommandPaletteForm _paletteForm;
    private HotKeyKit.GlobalHotKey _hotkey;
    private readonly Dictionary<string, Icon> _iconCache;

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
        _menu.Opening += (s, e) => RebuildMenu(); // 每次打开菜单都重建，天然感知 CLI 的增删改

        _ni = new NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "quickcmd",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        Manager.Notify = (t, m) => _ni.ShowBalloonTip(2000, t, m, ToolTipIcon.Info);

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

        _pruneTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _pruneTimer.Tick += (s, e) => Manager.Prune();
        _pruneTimer.Start();

        RebuildMenu(); // 启动时预填菜单：空菜单首次右键时 Opening 会被 WinForms 置 Cancel=true，导致菜单要点两次才弹出

        StartIpcServer(); // CLI 的 run 请求走这里，实例可被跟踪
    }

    public void ShowCommandPalette()
    {
        if (_paletteForm != null && !_paletteForm.IsDisposed)
        {
            _paletteForm.Close();
            _paletteForm = null;
        }

        var commands = Store.Load(out _);
        _paletteForm = new CommandPaletteForm(commands, RunCommandFromPalette, this);
        _paletteForm.Show();
    }

    private void RunCommandFromPalette(CommandDef cmd)
    {
        RunCommand(cmd);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hotkey?.Dispose();
            _ni?.Dispose();
            _menu?.Dispose();
            _pruneTimer?.Stop();
            _pruneTimer?.Dispose();
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
        Manager.Prune();
        var list = Store.Load(out var bad);
        _menu.Items.Clear();

        // 清理图标缓存（移除已删除命令的图标）
        var currentIds = new HashSet<string>(list.Select(c => c.Id));
        var toRemove = _iconCache.Keys.Where(id => !currentIds.Contains(id)).ToList();
        foreach (var id in toRemove)
        {
            _iconCache[id]?.Dispose();
            _iconCache.Remove(id);
        }

        _menu.Items.Add(new ToolStripMenuItem("quickcmd · " + list.Count + " 条命令") { Enabled = false });
        if (bad > 0)
            _menu.Items.Add(new ToolStripMenuItem("⚠ 数据文件有 " + bad + " 行无法解析") { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());

        if (list.Count == 0)
        {
            _menu.Items.Add(new ToolStripMenuItem("（暂无命令，用 quickcmd add 注册）") { Enabled = false });
        }
        else
        {
            // 平铺渲染（与命令面板一致）：不再套 proj 子菜单，
            // 命令项标签本身就是 "proj > title"，同项目按 proj 排序自然相邻
            var ordered = list
                .OrderBy(c => (c.Proj ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase);
            foreach (var c in ordered) _menu.Items.Add(BuildCommandItem(c));
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
        startupItem.CheckedChanged += (s, e) =>
        {
            StartupManager.SetEnabled(startupItem.Checked, Manager.Notify);
        };
        _menu.Items.Add(startupItem);

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (s, e) => ExitTray();
        _menu.Items.Add(exitItem);
    }

    // 命令项：点击 = 运行（JOB 模式）；有运行中实例时展开直接显示实例，无实例不给子菜单
    private ToolStripMenuItem BuildCommandItem(CommandDef c)
    {
        var insts = Manager.ForCommand(c.Id);
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
            foreach (var i in insts) item.DropDownItems.Add(BuildInstanceItem(i));
            if (insts.Count > 1)
            {
                var killAll = new ToolStripMenuItem("结束全部实例");
                killAll.Click += (s, e) => { foreach (var i in Manager.ForCommand(c.Id)) Manager.Kill(i); };
                item.DropDownItems.Add(killAll);
            }
        }
        return item;
    }

    // 实例项：点击 = 置前；展开 = Proj name / Open workdir / Bring front / Kill
    private ToolStripMenuItem BuildInstanceItem(Instance i)
    {
        var mins = (long)(DateTime.Now - i.Started).TotalMinutes;
        var dur = mins < 1 ? "刚刚" : mins + " 分钟";
        var mode = Manager.ModeOf(i);
        var item = new ToolStripMenuItem("[" + mode + "] pid " + i.Pid + " · " + i.Started.ToString("HH:mm:ss") + " · " + dur);
        item.Click += (s, e) => Manager.BringFront(i);

        var info = new ToolStripMenuItem(
            "Proj: " + (string.IsNullOrWhiteSpace(i.Cmd.Proj) ? "(未设置)" : i.Cmd.Proj)
            + " · [" + mode + "] pid " + i.Pid)
        { Enabled = false };
        item.DropDownItems.Add(info);

        var open = new ToolStripMenuItem("Open work directory");
        open.Click += (s, e) =>
        {
            var dir = Launcher.EffectiveWorkdir(i.Cmd);
            try
            {
                if (Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
                else
                    Manager.Notify("quickcmd", "目录不存在: " + dir);
            }
            catch { }
        };
        item.DropDownItems.Add(open);

        var front = new ToolStripMenuItem("Bring window to front");
        front.Click += (s, e) => Manager.BringFront(i);
        item.DropDownItems.Add(front);

        var kill = new ToolStripMenuItem(
            (i.Job != null ? "Kill whole process tree" : "Kill process") + " (pid " + i.Pid + ")");
        kill.Click += (s, e) => Manager.Kill(i);
        item.DropDownItems.Add(kill);

        return item;
    }

    // 托盘/面板点击运行：默认 JOB 模式（整树跟踪）
    private void RunCommand(CommandDef c, bool useJob = true)
    {
        if (Manager.Start(c, useJob, out var err, out _, out _)) return;   // 成功，气泡已发
        if (err != "max")
            _ni.ShowBalloonTip(3000, "quickcmd 启动失败", err, ToolTipIcon.Error);
    }

    // ----- qchost 侧 IPC 服务端：接收 CLI 的 run / ps / kill / ping / reload -----

    private void StartIpcServer()
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(Program.PipeName, PipeDirection.InOut, 1);
                    server.WaitForConnection();

                    string line = "";
                    using (var r = new StreamReader(server, new UTF8Encoding(false), false, 1024, true))
                        line = r.ReadLine() ?? "";

                    string resp = "{}";
                    var ar = _helper.BeginInvoke((Action)(() => resp = HandleIpc(line)));
                    _helper.EndInvoke(ar);

                    using (var w = new StreamWriter(server, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                        w.WriteLine(resp);
                }
                catch { }
                finally { try { server?.Dispose(); } catch { } }
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    private string HandleIpc(string line)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<IpcMessage>(line ?? "", Store.JsonOpts);
            if (msg == null) return Err("empty message");
            switch ((msg.Op ?? "").ToLowerInvariant())
            {
                case "ping":
                    return JsonSerializer.Serialize(new QchostResponse { Ok = true, Message = "qchost" }, Store.JsonOpts);

                case "run":
                    var list = Store.Load(out _);
                    var c = Resolver.Resolve(msg.Key, list, out var err);
                    if (c == null) return Err(err);
                    bool useJob = !string.Equals(msg.Mode, "pid", StringComparison.OrdinalIgnoreCase);
                    if (Manager.Start(c, useJob, out var serr, out var spid, out var smode))
                        return JsonSerializer.Serialize(new QchostResponse { Ok = true, Message = "[" + smode + "] 已启动 pid=" + spid }, Store.JsonOpts);
                    if (serr == "max")
                        return JsonSerializer.Serialize(new QchostResponse { Ok = true, Message = "已达实例上限，已置前现有实例" }, Store.JsonOpts);
                    return Err(serr);

                case "ps":
                    var snap = Manager.Snapshot();
                    return JsonSerializer.Serialize(new QchostResponse
                    {
                        Ok = true,
                        Message = snap.Count + " 个实例",
                        Data = JsonSerializer.Serialize(snap, Store.JsonOpts),
                    }, Store.JsonOpts);

                case "kill":
                    if (!int.TryParse((msg.Key ?? "").Trim(), out var kpid))
                        return Err("kill 目标必须是 ps 里的实例 ID（pid），实际: " + msg.Key);
                    var target = Manager.FindInstance(kpid);
                    if (target == null) return Err("未找到实例 pid " + kpid + "（不在 qchost 跟踪的实例中）");
                    var kmode = Manager.ModeOf(target);
                    var kid = target.Pid;
                    var ktitle = target.Cmd.Title ?? "";
                    Manager.Kill(target);
                    var via = kpid != kid ? "pid " + kpid + " 属于实例 " + kid + "，" : "";
                    return JsonSerializer.Serialize(new QchostResponse
                    {
                        Ok = true,
                        Message = "[" + kmode + "] " + via + "已结束「" + ktitle + "」" +
                                  (kmode == "JOB" ? "（整棵作业树）" : "（pid 进程树）"),
                    }, Store.JsonOpts);

                default:
                    return Err("unknown op: " + msg.Op);
            }
        }
        catch (Exception ex) { return Err(ex.Message); }

        string Err(string m) =>
            JsonSerializer.Serialize(new QchostResponse { Ok = false, Message = m }, Store.JsonOpts);
    }

    // ----- 其他 -----

    private void ExitTray()
    {
        _pruneTimer.Stop();
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

