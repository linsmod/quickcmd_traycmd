# quickcmd — 系统托盘命令启动器

一个仓库，两个角色。项目术语 **qchost**（quick command host）= 常驻系统托盘的宿主进程 `quick_run.exe`，负责托盘菜单与实例管理；用它代替"GUI"一词，避免与用户注册命令跑出来的**目标 GUI 程序**混淆。

| 角色 | 启动方式 | 职责 |
|---|---|---|
| qchost（托盘宿主） | 直接运行 `quick_run.exe`（无参数，单实例互斥） | 右键菜单点已注册命令运行；展开命令可看运行中的实例（置前 / 结束）；IPC 服务端 |
| CLI | `quickcmd.exe <verb> ...`（带参数） | 对菜单里的命令做增删改查（CRUD）+ run/ps/kill，供脚本 / AI agent 调用；`run`/`run-job` 在 qchost 未运行时自动拉起它 |

数据落盘：`%APPDATA%\quickcmd\commands.jsonl`（每行一条命令定义，UTF-8 JSONL）。

## 源码结构

`quickcmd.exe` 由 `Program.cs`（`Main` 直接进 CLI）+ `src/` 下按职责拆分的文件编译（均 `namespace QuickCmd`）；qchost（`quick_run.exe`）是同解决方案下的 WinExe 壳，引用本项目并调用 `TrayContext`：

| 文件 | 职责 |
|---|---|
| `hotkey/` | **独立类库 `HotKeyKit`**：全局快捷键检测（message-only 窗口 + 监听线程，零 WinForms 依赖），`Register(mods, key, callback)` 注册组合键 + `EnableWinLongPress()/WinKeyLongPress` 长按 Win 检测（短按/组合键不受影响） |
| `src/Models.cs` | `CommandDef` / IPC 数据模型 |
| `src/Store.cs` | JSONL 读写（原子替换、旧字段迁移） |
| `src/Resolver.cs` | 按 id / short-id / title 定位命令 |
| `src/IconExtractor.cs` | 命令项图标提取 |
| `src/Job.cs` | **Windows 作业对象封装**（挂起启动 + 指派 + 整树存活/击杀） |
| `src/Launcher.cs` | 命令定义 → 真实进程（解释器解析、cmd 兜底、作业跟踪） |
| `src/Manager.cs` | 运行实例、`WindowFinder`、`Manager`（存活/置前/结束） |
| `src/Native.cs` | Win32 P/Invoke |
| `src/StartupManager.cs` | 开机启动项 |
| `src/CommandPalette.cs` | 命令面板（长按 Win 呼出） |
| `src/TrayContext.cs` | qchost 托盘宿主（`TrayContext`：托盘图标 + 动态菜单 + IPC 服务端） |
| `src/CliApp.cs` | CLI CRUD |

## 构建

需要 .NET SDK（本机已装 9/10）：

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o dist
# 产物: dist\quickcmd.exe（约 190 KB，运行需已装 .NET 8+ 桌面运行时）
```

> 分发到没有 .NET 的机器时加 `--self-contained true`（体积约 150 MB，免装运行时）。

## 菜单结构

```
qchost 托盘图标（左键或右键）
└─ 菜单
   ├─ ○ myapp > Dev Server  ← 点击 = 运行（JOB 模式，弹真实终端/exe 窗口，能看到输出）
   │   └─ 展开（仅当有运行中实例；无实例的命令不给子菜单）
   │       └─ [JOB] pid 12345 · 21:30:11 · 5 分钟   ← 点击 = 把该窗口置前
   │           ├─ Proj: myapp · [JOB] pid 12345
   │           ├─ Open work directory
   │           ├─ Bring window to front
   │           └─ Kill whole process tree (pid 12345)   ← [PID] 模式为 "Kill process"
   ├─ （平铺渲染：命令标签为 "proj > title"，同项目相邻，不再套 proj 子菜单）
   ├─ ─────────────
   ├─ 打开数据文件
   └─ 退出
```

同一命令可多次运行（受 `--max-instances` 限制，默认 1）。已到上限再点击会置前现有实例。

实例标签前缀标明跟踪模式：`[JOB]` = 作业对象整树跟踪（托盘点击/run-job 的默认，判定与击杀最准）；`[PID]` = 仅按 root pid 进程树跟踪（`quickcmd run`）。

## CLI

```
quickcmd                                  无参数 = 显示帮助；qchost 宿主由 quick_run.exe 承担
quickcmd add --title <名> (--program <程序> | --shell-script <脚本>) [选项]
quickcmd list [--proj <项目>] [--json]
quickcmd get <short-id|id|title> [--json]
quickcmd update <short-id> [选项]
quickcmd delete <short-id> [-y]
quickcmd run <short-id>                   [PID] 模式运行：按 root pid 进程树跟踪
quickcmd run-job <short-id>               [JOB] 模式运行：作业对象整树跟踪（推荐）
quickcmd ps                               列出 qchost 跟踪中的实例（[JOB] 展示整棵进程树）
quickcmd kill <实例ID>                     结束实例（[JOB] 杀整棵作业树；传树内任意子 pid 也会定位到其 job）
quickcmd path                             打印数据文件路径
quickcmd help
```

字段：`--title`、`--proj`、`--workdir`（支持 `%VAR%`）、`--env KEY=VAL`（可重复，VAL 支持 `%VAR%` 引用系统环境变量）、`--program` / `--shell-script`（二选一，+ `--args`）、`--interpreter`（解释器查找顺序覆盖）、`--max-instances`（默认 1）、`--close-on-exit`（默认保持窗口）。

- `--program`：exe 路径或 PATH 中的名称；`npm` 这类裸命令（实际是 .cmd）自动经 cmd 解析兜底。
- `--shell-script`：按扩展名分派解释器——`.py/.pyw`→python(pythonw)、`.bat/.cmd`→cmd、`.ps1`→pwsh（未装则回退 powershell）；其他扩展名拒绝。
- `--interpreter "D:\py312;D:\py311"`：分号分隔的文件/目录，按顺序查找解释器，全部无效报错；不指定则落系统 PATH。解决 python 等多版本问题。

## 示例

```powershell
# 注册一个 dev server（终端保持打开看输出）
.\dist\quickcmd.exe add --title "Dev Server" --proj myapp --workdir D:\code\myapp `
  --env "NODE_ENV=dev" --program "npm" --args "run dev"

# 注册 python 脚本并锁定解释器版本
.\dist\quickcmd.exe add --title "数据同步" --workdir D:\jobs --shell-script D:\jobs\sync.py --interpreter "D:\py312"

# 注册一个 GUI 程序
.\dist\quickcmd.exe add --title "记事本" --program notepad.exe --max-instances 2

.\dist\quickcmd.exe list
.\dist\quickcmd.exe run 1a2b3c4d
.\dist\quickcmd.exe delete 1a2b3c4d -y
```

## Agent 集成（配套 skill）

已安装到 `~\.agents\skills\quickcmd\SKILL.md`（仓库副本在 `skills\quickcmd\`）。装了该 skill 的 agent（ZCode 等）在用户说"加到托盘 / 注册命令 / 以后一键运行"时会自动调用上述 CLI 完成注册。

## 设计说明 / 已知边界

- qchost 每次打开菜单都重读 JSONL，CLI 的增删改立即生效，无需重启。
- `quickcmd run` / `run-job` 通过命名管道（`quickcmd.ipc`）把请求（含模式）交给常驻 qchost，实例才可被"置前 / 杀进程"管理；qchost 未运行时自动拉起 `quick_run.exe` 再走 IPC，拉不起（找不到 exe / 12 秒无响应）才退回直接启动（不跟踪，两种模式行为相同）。
- **两种显式跟踪模式**，实例在托盘菜单与启动气泡中以 `[JOB]` / `[PID]` 标注：
  - `run-job` → **[JOB]**：Windows 作业对象（Job Object）整树跟踪。qchost 启动命令时以 `CREATE_SUSPENDED` 创建进程、指派进一个专属作业、再恢复运行。作业成员可继承，所以即便被启动的是"拉起子进程后自己就退出"的 launcher/包装器，其孙进程仍在同一作业内，不会被误判为已退出。`Kill whole process tree` 用 `TerminateJobObject` 结束整棵树；实例存活判定读作业的 `ActiveProcesses`（比"root pid 是否退出"准确）。
    - 不设 `KILL_ON_JOB_CLOSE`：quickcmd / qchost 自身退出时不会连带杀掉已启动的命令。
    - 兜底退化：若建作业/指派失败（例如 qchost 自身已在别的作业里），自动退化回 [PID] 方式（实例标注为 `[PID]`）。
  - `run` → **[PID]**：直接 `Process.Start`，仅按 root pid 及其子孙树跟踪存活与窗口（`WindowFinder` 枚举 pid 树）；root（launcher）提前退出会误判实例已结束。
  - qchost 托盘点击命令 / 命令面板运行 = 默认 [JOB] 模式。
- Windows 11 默认终端为 Windows Terminal 时，窗口归属与 pid 分离；有作业时按作业内活跃 pid 枚举窗口，退化时按"pid 子孙树 + 窗口枚举"兜底定位。
- **打包应用（MSIX）可能逃逸作业**：Win11 商店版 notepad 这类打包应用经 `cmd start`/ShellExecute 激活时由激活代理重新拉起，不在作业内（父 pid 显示不变但成员关系丢失），[JOB] 会如实判定实例已结束。用 `--program notepad.exe` 直接启动则正常。
- `ps` / `kill` 只在 qchost 运行时有意义（实例存活于 qchost 进程内，这两个命令不自动拉起 qchost）：`ps` 每条实例的 ID = root pid（[JOB] 即作业标识，带 `*` = root 已退、靠作业续命），下方缩进树为真实进程树；`kill <ID>` 对 [JOB] 用 `TerminateJobObject` 击杀整棵树，传树内任意成员 pid 也会定位到其作业实例。
- `--command-text` 已被 `--program` + `--args` 取代；裸命令（`npm` 等 .cmd）直接启动找不到时会自动回退经 `cmd /k` 解析。
- 默认保持窗口：python 用 `-i`（脚本跑完停在交互界面）、ps1 用 `-NoExit`、bat 用 `/k`；`--close-on-exit` 反之。
- 数据文件手工损坏的行会被跳过，并在菜单/`list` 中提示行数。
