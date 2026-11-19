// ============================================================================
// quickcmd — CommandPalette.cs: 命令面板（VS Code 风格搜索框）
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

// ---------------------------------------------------------------------------
// 命令面板（VS Code 风格的搜索框）
// ---------------------------------------------------------------------------
internal sealed class CommandPaletteForm : Form
{
    private readonly TextBox _searchBox;
    private readonly ListBox _commandList;
    private readonly List<CommandDef> _allCommands;
    private readonly Action<CommandDef> _onCommandSelected;
    private readonly TrayContext _trayContext;
    private readonly Dictionary<string, Icon> _paletteIconCache;

    public CommandPaletteForm(List<CommandDef> commands, Action<CommandDef> onCommandSelected, TrayContext trayContext)
    {
        _allCommands = commands;
        _onCommandSelected = onCommandSelected;
        _trayContext = trayContext;
        _paletteIconCache = new Dictionary<string, Icon>();

        // Form 设置
        this.Text = "Command Palette   —   Enter 运行 · Ctrl+Enter 打开网站";
        this.FormBorderStyle = FormBorderStyle.FixedSingle;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.Size = new Size(600, 450);
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.ShowInTaskbar = false;
        this.TopMost = true;
        this.BackColor = Color.FromArgb(30, 30, 30);
        this.ForeColor = Color.White;

        // 搜索框
        _searchBox = new TextBox
        {
            Location = new Point(10, 10),
            Size = new Size(560, 30),
            Font = new Font("Segoe UI", 12f),
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Text = ""
        };
        _searchBox.TextChanged += (s, e) => FilterCommands();
        _searchBox.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Down)
            {
                MoveSelection(+1);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Up)
            {
                MoveSelection(-1);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                if (e.Control) OpenSelectedUrl();
                else SubmitSelected();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                this.Close();
                e.Handled = true;
            }
        };
        this.Controls.Add(_searchBox);

        // 命令列表
        _commandList = new ListBox
        {
            Location = new Point(10, 50),
            Size = new Size(560, 350),
            Font = new Font("Segoe UI", 10f),
            BackColor = Color.FromArgb(37, 37, 37),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 45,
            IntegralHeight = false
        };
        _commandList.DrawItem += DrawCommandItem;
        _commandList.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                if (e.Control) OpenSelectedUrl();
                else SubmitSelected();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                this.Close();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Down)
            {
                MoveSelection(+1); // 移动高亮后焦点回到输入框
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Up)
            {
                MoveSelection(-1);
                e.Handled = true;
            }
        };
        _commandList.DoubleClick += (s, e) =>
        {
            if (_commandList.SelectedItem is CommandDef cmd)
            {
                _onCommandSelected(cmd);
                this.Close();
            }
        };
        this.Controls.Add(_commandList);

        // 初始加载所有命令
        FilterCommands();

        // Deactivate 时关闭（点击外部）
        this.Deactivate += (s, e) => this.Close();
        this.Shown += CommandPaletteForm_Shown;
    }

    private void CommandPaletteForm_Shown(object sender, EventArgs e)
    {
        this.Activate();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _searchBox.Focus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var icon in _paletteIconCache.Values)
                icon?.Dispose();
            _paletteIconCache.Clear();
        }
        base.Dispose(disposing);
    }

    // 移动列表高亮，焦点始终回到输入框（可继续键入过滤）
    private void MoveSelection(int delta)
    {
        if (_commandList.Items.Count == 0) return;
        var i = _commandList.SelectedIndex;
        i = i < 0
            ? (delta > 0 ? 0 : _commandList.Items.Count - 1)
            : Math.Clamp(i + delta, 0, _commandList.Items.Count - 1);
        _commandList.SelectedIndex = i;
        EnsureItemVisible(i);
        _searchBox.Focus();
        _searchBox.SelectionStart = _searchBox.Text.Length;
    }

    // WinForms ListBox 没有 EnsureVisible，用 TopIndex 手动滚动到可见区
    private void EnsureItemVisible(int index)
    {
        int visibleCount = Math.Max(1, _commandList.Height / _commandList.ItemHeight);
        if (index < _commandList.TopIndex)
            _commandList.TopIndex = index;
        else if (index > _commandList.TopIndex + visibleCount - 1)
            _commandList.TopIndex = index - visibleCount + 1;
    }

    private void SubmitSelected()
    {
        if (_commandList.SelectedItem is CommandDef cmd)
        {
            _onCommandSelected(cmd);
            this.Close();
        }
    }

    // Ctrl+Enter：直接打开 web 命令的 URL（不启动、不改状态）
    private void OpenSelectedUrl()
    {
        if (_commandList.SelectedItem is CommandDef cmd && !string.IsNullOrWhiteSpace(cmd.Url))
        {
            try { Process.Start(new ProcessStartInfo(cmd.Url) { UseShellExecute = true }); } catch { }
        }
    }

    private void FilterCommands()
    {
        var query = _searchBox.Text.ToLowerInvariant();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allCommands
            : _allCommands.Where(c =>
                c.Title.ToLowerInvariant().Contains(query) ||
                (c.Proj?.ToLowerInvariant().Contains(query) ?? false) ||
                c.KindText.ToLowerInvariant().Contains(query))
            .ToList();

        _commandList.Items.Clear();
        foreach (var cmd in filtered)
            _commandList.Items.Add(cmd);
    }

    private void DrawCommandItem(object sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _commandList.Items.Count) return;

        e.DrawBackground();
        e.DrawFocusRectangle();

        var cmd = _commandList.Items[e.Index] as CommandDef;
        if (cmd == null) return;

        var brush = new SolidBrush(e.ForeColor);
        var titleBrush = new SolidBrush(Color.White);
        var grayBrush = new SolidBrush(Color.Gray);

        // 绘制图标
        int iconOffset = 0;
        if (!_paletteIconCache.TryGetValue(cmd.Id, out var icon))
        {
            icon = IconExtractor.GetIcon(cmd);
            if (icon != null)
                _paletteIconCache[cmd.Id] = icon;
        }

        if (icon != null)
        {
            e.Graphics.DrawIcon(icon, e.Bounds.X + 5, e.Bounds.Y + 5);
            iconOffset = 35; // 图标宽度 + 间距
        }

        // 绘制项目名>命令标题
        var projPrefix = string.IsNullOrWhiteSpace(cmd.Proj) ? "" : cmd.Proj.Trim() + " > ";
        var title = projPrefix + cmd.Title;
        var titleRect = new Rectangle(e.Bounds.X + 5 + iconOffset, e.Bounds.Y + 5, e.Bounds.Width - 10 - iconOffset, 20);
        e.Graphics.DrawString(title, new Font("Segoe UI", 10f, FontStyle.Bold), titleBrush, titleRect);

        // 绘制命令类型
        var subtitle = (cmd.IsWeb ? "[网站] " : "") + cmd.KindText
            + (string.IsNullOrWhiteSpace(cmd.Url) ? "" : "   ·   " + cmd.Url);
        var subtitleRect = new Rectangle(e.Bounds.X + 5 + iconOffset, e.Bounds.Y + 27, e.Bounds.Width - 10 - iconOffset, 15);
        e.Graphics.DrawString(subtitle, new Font("Segoe UI", 8f), grayBrush, subtitleRect);

        brush.Dispose();
        titleBrush.Dispose();
        grayBrush.Dispose();
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            this.Close();
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }
}

