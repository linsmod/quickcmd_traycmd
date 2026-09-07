// ============================================================================
// quickcmd — Models.cs: 数据模型（JSONL 记录）与 IPC 消息
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
// 数据模型（JSONL 中的一行）
// ---------------------------------------------------------------------------
internal sealed class CommandDef
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N"); // 32 位 hex；ShortId 取前 8 位
    public string Title { get; set; } = "";
    public string Proj { get; set; } = "";
    public string Workdir { get; set; } = "";                      // 执行目录；支持 %VAR%；空=用户目录
    public List<string> Env { get; set; } = new();                 // "KEY=VAL"；VAL 支持 %VAR% 占位符
    public string Program { get; set; } = "";                      // 可执行程序：exe 路径或 PATH 中的名称；裸命令（如 npm）自动经 cmd 兜底解析
    public string ShellScript { get; set; } = "";                  // 脚本文件：按扩展名选解释器 .py/.pyw→python(pythonw) .bat/.cmd→cmd .ps1→pwsh
    public string Args { get; set; } = "";                         // 附加参数（program / shell_script 共用）
    public string Interpreter { get; set; } = "";                  // 解释器查找顺序覆盖：';' 分隔的文件/目录，按顺序，最后落系统 PATH
    public int MaxInstances { get; set; } = 1;                     // 并发实例上限
    public bool KeepOpen { get; set; } = true;                     // 命令结束后是否保持窗口
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    // 旧版本字段（command_text/command_file/shell）：读入后立即迁移，保存时清除
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; }

    public string ShortId => Id.Length >= 8 ? Id.Substring(0, 8) : Id;

    public string KindText =>
        !string.IsNullOrWhiteSpace(ShellScript) ? "script: " + ShellScript
        : !string.IsNullOrWhiteSpace(Program) ? "program: " + Program
        : "(空)";
}

internal sealed class IpcMessage
{
    public string Op { get; set; }   // ping | run | kill | ps | reload
    public string Key { get; set; }  // run: short-id/id/title；kill: 实例 ID（pid）
    public string Mode { get; set; } // run 跟踪模式: "job" | "pid"（空 = job）
}

// qchost（常驻托盘宿主）对 IPC 请求的应答
internal sealed class QchostResponse
{
    public bool Ok { get; set; }
    public string Message { get; set; }
    public string Data { get; set; } // 可选 JSON 负载（ps 的实例快照）
}

// ps 快照：一条运行实例。Id = 实例 ID（root pid；JOB 模式下即该作业的 ID，
// kill 用它定位，杀掉的是整棵作业树而不止这个 pid）
internal sealed class InstanceInfo
{
    public int Id { get; set; }
    public string Mode { get; set; }      // JOB | PID
    public string ShortId { get; set; }   // 所属命令定义 short id
    public string Title { get; set; }
    public string Proj { get; set; }
    public DateTime Started { get; set; }
    public bool RootExited { get; set; }  // JOB 模式 root 已退、树靠作业续命
    public List<ProcNode> Tree { get; set; } = new();
}

internal sealed class ProcNode
{
    public int Pid { get; set; }
    public int Ppid { get; set; }
    public string Name { get; set; }
}

