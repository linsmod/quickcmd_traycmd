// ============================================================================
// quickcmd — Models.cs: 数据模型（JSONL 记录）与 IPC 消息
// ============================================================================

using System.Text.Json;
using System.Text.Json.Serialization;

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
    public string Type { get; set; } = "";                         // 空/"app" = 普通程序；"web" = 网站/常驻服务（托盘/面板显示 URL 与状态）
    public string Url { get; set; } = "";                          // web 类型的访问地址（展示/打开用，如 http://127.0.0.1:41001/）
    public bool AutoStart { get; set; }                            // web 类型：qchost 启动时自动拉起（默认不自动启动）
    public bool CaptureLogs { get; set; }                          // 捕获 stdout/stderr 到 %APPDATA%\quickcmd\logs（服务型命令用；不显示控制台窗口）
    public int MaxInstances { get; set; } = 1;                     // 并发实例上限
    public bool KeepOpen { get; set; } = true;                     // 命令结束后是否保持窗口
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    // 旧版本字段（command_text/command_file/shell）：读入后立即迁移，保存时清除
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; }

    public string ShortId => Id.Length >= 8 ? Id.Substring(0, 8) : Id;

    public bool IsWeb => string.Equals(Type, "web", StringComparison.OrdinalIgnoreCase);

    public string KindText =>
        !string.IsNullOrWhiteSpace(ShellScript) ? "script: " + ShellScript
        : !string.IsNullOrWhiteSpace(Program) ? "program: " + Program
        : "(空)";
}

internal sealed class IpcMessage
{
    public string Op { get; set; }   // ping | run | exec | kill | ps | reload | shutdown
    public string Key { get; set; }  // run: short-id/id/title；kill/front: 实例 ID（job-<数字> 或 pid，也接受树内任意 pid）
    public string Mode { get; set; } // run/exec 跟踪模式: "job" | "pid"（空 = job）

    // exec（免注册直跑）的载荷；run 只用 Key。
    // Source/RequesterPid 只用于审计（history.jsonl），不参与执行。
    public string Source { get; set; }        // cli | palette | tray
    public int RequesterPid { get; set; }
    public string Title { get; set; }         // exec 的历史标签（可选）
    public string CommandLine { get; set; }   // exec: 交给 cmd /c 的整行命令
    public string Program { get; set; }       // exec: 直接指定可执行程序（与 CommandLine 二选一）
    public string Args { get; set; }          // exec: --program 的附加参数
    public string Workdir { get; set; }       // exec: 执行目录（空 = 用户目录）
    public List<string> Env { get; set; }     // exec: "KEY=VAL" 列表
    public bool KeepOpen { get; set; }        // exec: cmd /k 保持窗口（默认 /c 跑完即退）
    public bool CaptureLogs { get; set; }     // exec: 捕获 stdout/stderr 到日志文件（quickcmd logs 查看）
}

// qchost（常驻托盘宿主）对 IPC 请求的应答
internal sealed class QchostResponse
{
    public bool Ok { get; set; }
    public string Message { get; set; }
    public string Data { get; set; } // 可选 JSON 负载（ps 的实例快照）
}

// ps 快照：一条运行实例。Id = 实例 ID（字符串：JOB = "job-<N>"（qchost 按作业创建
// 顺序分配的自增序号，随 Job 对象走），PID = root pid），kill/front 用它定位；
// JOB 模式杀整棵作业树，root pid 提前退出也不影响（存活以 Tree 里的作业成员/逃逸子孙为准）
internal sealed class InstanceInfo
{
    public string Id { get; set; }
    public string Mode { get; set; }      // JOB | PID
    public string ShortId { get; set; }   // 所属命令定义 short id
    public string Title { get; set; }
    public string Proj { get; set; }
    public string Type { get; set; }      // 命令类型（web = 网站/常驻服务）
    public string Url { get; set; }       // web 类型的访问地址（托盘/面板"打开"用）
    public string LogPath { get; set; }   // 输出日志文件（仅 CaptureLogs 命令；空 = 未捕获）
    public int Pid { get; set; }          // root 启动 pid（JOB 模式下可能已退出）
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

// ---------------------------------------------------------------------------
// 执行历史（审计）：%APPDATA%\quickcmd\history.jsonl，一行一个事件。
// 只有 qchost 写执行事件（单一写入者），CLI / 托盘只读；本地兜底直启由 CLI 记 local。
// 一次执行两条记录：Event=start（已启动）与 Event=finish（退出/被杀），用 ExecId 关联。
// ---------------------------------------------------------------------------
internal sealed class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Timestamp { get; set; } = DateTime.Now;   // 本地时间，便于人工对表
    public string Event { get; set; } = "start";              // start | finish
    public string Kind { get; set; } = "run";                 // run（注册项）| exec（免注册直跑）
    public string Source { get; set; } = "";                  // cli | palette | tray | qchost | cli-local
    public string ExecId { get; set; } = "";                  // 关联一次执行（adhoc-… 或命令 Id）
    public string Title { get; set; } = "";
    public string ShortId { get; set; } = "";
    public string CommandLine { get; set; } = "";             // 实际执行的命令行（程序 + 参数）
    public string Workdir { get; set; } = "";
    public string Mode { get; set; } = "";                    // JOB | PID
    public string InstanceId { get; set; } = "";
    public int Pid { get; set; }
    public string Status { get; set; } = "";                  // started|exited|killed|failed|max|host-unavailable|local
    public int ExitCode { get; set; } = int.MinValue;         // 未知 = int.MinValue
    public long DurationMs { get; set; } = -1;
    public string Error { get; set; } = "";
    public int RequesterPid { get; set; }                     // 发起请求的进程（CLI / 托盘）
    public string LogPath { get; set; } = "";                 // 捕获型执行（CaptureLogs）的输出日志文件
}

