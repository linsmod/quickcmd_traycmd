# quickcmd — 系统托盘命令启动器

一个仓库，两个角色。项目术语 **qchost**（quick command host）= 常驻系统托盘的宿主进程 `quick_run.exe`，负责托盘菜单与实例管理；用它代替"GUI"一词，避免与用户注册命令跑出来的**目标 GUI 程序**混淆。

| 角色 | 启动方式 | 职责 |
|---|---|---|
| qchost（托盘宿主） | 直接运行 `quick_run.exe`（无参数，单实例互斥） | 托盘菜单第一级直接列出运行中的实例（结束 / 打开文件位置 / 打开工作目录）；命令面板（双击托盘 / 长按 Win）搜索并运行命令；IPC 服务端 |
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
| `Serve.cs`（仓库 `traycmd/Serve.cs`） | 内置静态文件服务（`quickcmd serve`：Range/ETag/index/SPA 回退/路径逃逸防护） |
| `LogStore.cs`（仓库 `quickcmd.shared/LogStore.cs`） | 输出日志：可继承文件句柄、中继线程、尾部读取（UTF-8→GBK 回退）、自动清理 |
| `WebConsole.cs`（仓库 `quickcmdhost/WebConsole.cs`） | 本地 Web 控制台：单页 UI + `/api/state`/`run`/`kill`，loopback + Host/Origin/Token 校验 |
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
   ├─ [JOB] myapp > Dev Server · job-2 · pid 12345,12346 · 5 分   ← 第一级 = 运行中的实例
   │   ├─ 结束任务（整棵作业树）                              ← [PID] 模式为 "（进程树）"
   │   ├─ 打开文件所在位置        ← 优先取运行进程主模块路径（含商店别名/逃逸子孙），回退注册脚本/绝对路径
   │   ├─ 查看日志               ← 仅 --capture-logs 命令，且本次生成了日志
   │   └─ 打开工作目录
   ├─ 暂无实例或已逃逸            ← 没有存活实例时的占位
   ├─ ─────────────
   ├─ 网站                        ← web 类型命令（● 运行中 / ○ 已停止）
   │   ├─ ● demo > 站点预览 · http://127.0.0.1:41001/
   │   │   ├─ 在浏览器打开 / 复制 URL
   │   │   └─ 停止
   │   └─ ○ 文档预览
   │       ├─ 启动
   │       └─ 复制 URL
   ├─ ─────────────
   ├─ 打开数据文件
   ├─ 打开日志目录              ← %APPDATA%\quickcmd\logs
   ├─ 打开控制台（浏览器）        ← 命令/实例/历史 列表 + 启动/停止
   ├─ 安装 Agent Skill          ← 嵌入的 SKILL.md 写到 ~\.agents\skills\traycmd\
   ├─ 复制 Skill 文件            ← 把 SKILL.md 内容放剪贴板（未安装则用内嵌版）
   └─ 退出
```

菜单只放实例，不再平铺命令列表（避免 命令→实例→操作 三层嵌套）：运行命令走命令面板（双击托盘图标 / 长按 Win），或 CLI `quickcmd run-job`。

实例 ID 为字符串：`[JOB]` = `job-<N>`（qchost 按作业创建顺序分配的自增序号，见下），`[PID]` = root pid。标签里的 pid 列表是**当前查出来的活跃 pid**：JOB 成员 + 沿 ppid 归属链扩出的**逃逸子孙**（如 bat 里 `start` 商店记事本：真实进程经 shell 代理拉起不进作业，但 ppid 仍指着已退的 cmd，照样被跟踪/击杀）。root 已退时标 `root已退`。

同一命令可多次运行（受 `--max-instances` 限制，默认 1）。已到上限时 qchost 会把现有实例置前并提示。

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
quickcmd exec [选项] -- <命令行>          免注册直跑（经 qchost 出沙盒，自动记入审计历史）
quickcmd serve [--dir <目录>] --port <端口> [--spa]
                                          内置静态文件服务（前台运行，仅绑定 127.0.0.1）
quickcmd url <short-id|id|title>          打印 web 命令的访问地址
quickcmd logs [<key>] [--tail N] [--path] [--open] [--json]
                                          查看输出日志：不带 key = 最近日志清单；
                                          key = short-id / 实例 ID（job-N、pid）/ 日志文件名
quickcmd ui [--no-open]                  打开本地 Web 控制台（默认浏览器；--no-open 只打印 URL）
quickcmd ps                               列出 qchost 跟踪中的实例（ID=job-<数字> 或 pid；[JOB] 展示整棵进程树）
quickcmd kill <实例ID>                     结束实例（job-<数字> / root pid / 树内任意子 pid 均可；[JOB] 杀作业树+逃逸子孙）
quickcmd path                             打印数据文件路径
quickcmd skill                            显示配套 agent skill 文档（SKILL.md 构建时以嵌入资源随 exe 分发）
quickcmd help
```

字段：`--title`、`--proj`、`--workdir`（支持 `%VAR%`）、`--env KEY=VAL`（可重复，VAL 支持 `%VAR%` 引用系统环境变量）、`--program` / `--shell-script`（二选一，+ `--args`）、`--interpreter`（解释器查找顺序覆盖）、`--max-instances`（默认 1）、`--close-on-exit`（默认保持窗口）、`--type web`（网站/常驻服务）、`--url <地址>`（web 类型的访问地址）、`--auto-start`（qchost 启动时自动拉起）、`--capture-logs`（捕获 stdout/stderr 到日志文件）。

- `--program`：exe 路径或 PATH 中的名称；`npm` 这类裸命令（实际是 .cmd）自动经 cmd 解析兜底。
- `--shell-script`：按扩展名分派解释器——`.py/.pyw`→python(pythonw)、`.bat/.cmd`→cmd、`.ps1`→pwsh（未装则回退 powershell）；其他扩展名拒绝。
- `--interpreter "D:\py312;D:\py311"`：分号分隔的文件/目录，按顺序查找解释器，全部无效报错；不指定则落系统 PATH。解决 python 等多版本问题。
- `--type web`：只是标记（展示 URL、运行状态，不改执行方式）；`--url` 供托盘"网站"菜单 / 命令面板打开；`--auto-start` 让 qchost 启动时自动拉起。静态托管用内置 `serve`（见下），站点根目录就是 `--workdir`（web 类型要求该目录已存在）。
- `--capture-logs`：把 stdout/stderr 落到 `%APPDATA%\quickcmd\logs\<short-id>-<时间>.log`，并隐藏控制台窗口（`CREATE_NO_WINDOW`）。适合 serve / dev server 这类不需要键盘交互的常驻服务；交互式命令不要开（stdin 接 NUL）。

### 托管静态网站

`quickcmd serve` 是内置静态文件服务（TcpListener 绑 `127.0.0.1`，无外部依赖；支持 GET/HEAD、Range、ETag、index.html、`--spa` 前端路由回退，拒绝路径逃逸）。默认托管当前目录，`--dir` 可覆盖：

```powershell
# 注册：--program quickcmd 在 add 时解析成 quickcmd.exe 绝对路径（qchost 里 ProcessPath 指向 qchost.exe）
.\dist\quickcmd.exe add --type web --title "站点预览" --proj demo `
  --url http://127.0.0.1:41001/ --workdir D:\out\site --auto-start --capture-logs `
  --program quickcmd --args "serve --port 41001 --spa"

.\dist\quickcmd.exe run-job <short-id>   # 启动（或用托盘“网站”菜单 / 命令面板）
.\dist\quickcmd.exe url <short-id>       # 打印 URL
.\dist\quickcmd.exe ps                   # 实例行尾显示 · http://127.0.0.1:41001/
.\dist\quickcmd.exe logs <short-id>      # 看 serve 的输出（请求日志、报错）
```

托盘菜单的"网站"分区按 `● 运行中 / ○ 已停止` 列出所有 web 命令；运行中可"在浏览器打开 / 复制 URL / 停止"。命令面板里 web 项显示 URL，`Ctrl+Enter` 直接打开。`serve` 端口被占用时直接报错退出（不会自动换端口，避免 URL 漂移）。

### 输出日志

`--capture-logs` 的命令每次执行生成一个日志文件，`quickcmd logs` 用来查：

```powershell
quickcmd logs                     # 最近 20 个日志（时间/大小/文件名）
quickcmd logs <short-id>          # 该命令最新一次的日志（默认 60 行）
quickcmd logs job-1 --tail 200    # 某次执行（实例 ID，从 ps/history 拿）
quickcmd logs <short-id> --path   # 只要路径（交给编辑器 / agent）
quickcmd logs <short-id> --open   # 用系统默认程序打开
quickcmd logs <short-id> --clear -y   # 删除该命令的日志（不带 key = 清空整个目录）
```

实现要点：

- qchost（JOB 模式）启动时把**可继承的文件句柄**直接给子进程（`FILE_APPEND_DATA`），子进程自己往文件写 —— 没有管道、没有中继线程，服务不会因为管道缓冲区满而卡住；同时 `CREATE_NO_WINDOW` 隐藏控制台窗口（仍会有 conhost，但无可见窗口）。
- PID 模式（`run --mode pid`，qchost 内）用 `Process` 重定向 + 中继线程写入同一文件。
- 读取按尾部最多 512 KB 截取；UTF-8 严格解码失败时退回 GBK（cmd / 老工具输出），中英混排不会乱码。
- 日志目录超过 300 个文件或 14 天会自动清理（每次启动新日志时触发）。
- 本地兜底直启（qchost 不在）不捕获日志：CLI 进程退出会带走中继线程，会在输出里提示。

托盘："打开日志目录"（菜单底部）、实例项 / 网站项里的"查看日志"。控制台：命令行/实例行的"日志"按钮（`GET /api/log?key=...&tail=N`）。

### 本地 Web 控制台

qchost 启动时会在 `127.0.0.1` 上开一个只对 loopback 开放的控制台（默认 `http://127.0.0.1:41777/`，端口被占用时从 41777 起顺延并写回 `%APPDATA%\quickcmd\settings.json`）：

```powershell
quickcmd ui            # 打印 URL 并用浏览器打开
quickcmd ui --no-open  # 只打印 URL（脚本/agent 用）
```

页面四页：**首页**（固定命令的大图标入口，点击启动 / 运行中的网站点击直接打开 URL，悬停可取消固定或停止）、**命令**（类型、状态、URL、启动命令、工作目录、标记、更新时间 / 启动/停止/日志；卡片右下角「固定到首页」）、**实例**（ID、模式、类型、URL、root pid、启动时间、时长、进程数 / 停止/日志）、**历史**（时间/事件/类型/来源/状态/退出码/时长/实例/命令行/日志）。命令与实例支持**列表 / 卡片**两种视图（右上角切换，记在 localStorage）；首页与历史无此切换。固定项存在 `settings.json` 的 `pinned` 里，跨浏览器/重装保留。每 3 秒自动刷新（数据没变不重绘，滚动位置保留）。左侧「项目」侧栏按 `--proj` 分组，点一下只看该项目（各页签联动，计数与运行中标记实时更新）；地址栏形如 `#commands/CodeArts%20API`，刷新/分享可回到同一视图。

**搜索**：`Ctrl+K`（或直接在页面上敲字母）弹出浮层，输入即搜，结果按 命令 / 实例 / 历史 分组并高亮命中；`↑↓` 选择，`Enter` 执行（命令→启动或打开 URL，历史→打开日志），`Esc` 取消，点遮罩也关。浮层打开时对数据取**快照**，3 秒轮询不再刷它——滚动位置和选中项完全不动，关掉重开才更新。

**最近**：`Ctrl+R` 用同一个浮层，两组——**最近关闭**（已结束、按命令去重、排除仍运行的）在上，**最近运行**（当前实例）在下，中间只有一条分割线、不写组名。`Enter`：已关闭→重新启动；运行中→网站打开 URL、其它置前（`POST /api/front`）。列表超出高度时内部滚动，`↑↓` 会自动滚到选中项，浮层打开期间底层页面锁滚动。

API（`GET /api/state` 只读；写操作需要 `X-QC-Token`，token 随机生成并注入页面）：

```
GET  /api/state               # { host, commands[], instances[], history[] }
GET  /api/log?key=<short-id|实例ID>&tail=300   # text/plain：日志尾部
POST /api/run   {"key":"<id|short-id|title>"}
POST /api/kill  {"key":"<实例 ID 或树内任意 pid>"}
POST /api/front {"key":"<实例 ID 或树内任意 pid>"}   # 把实例窗口置前
POST /api/pin   {"key":"<id|short-id|title>","pinned":true|false}   # 首页固定项
```

安全约束：只绑 `127.0.0.1`；校验 `Host` 必须是 `127.0.0.1:<port>` / `localhost:<port>`（防 DNS rebinding）；`Origin` 非本页时拒绝；不返回 CORS 头。控制台与 CLI 共用同一套 IPC 处理逻辑，所以审计记录（`source=webui`）、实例跟踪、错误信息完全一致。

## 示例

```powershell
# 注册一个 dev server（终端保持打开看输出）
.\dist\quickcmd.exe add --title "Dev Server" --proj myapp --workdir D:\code\myapp `
  --env "NODE_ENV=dev" --program "npm" --args "run dev"

# 常驻服务：不弹窗口，输出进日志（quickcmd logs 查看）
.\dist\quickcmd.exe add --title "API 服务" --proj myapp --workdir D:\code\api `
  --program "node" --args "server.js" --capture-logs --close-on-exit
.\dist\quickcmd.exe logs <short-id> --tail 50

# 注册 python 脚本并锁定解释器版本
.\dist\quickcmd.exe add --title "数据同步" --workdir D:\jobs --shell-script D:\jobs\sync.py --interpreter "D:\py312"

# 注册一个 GUI 程序
.\dist\quickcmd.exe add --title "记事本" --program notepad.exe --max-instances 2

.\dist\quickcmd.exe list
.\dist\quickcmd.exe run 1a2b3c4d
.\dist\quickcmd.exe delete 1a2b3c4d -y
```

## Agent 集成（配套 skill）

已安装到 `~\.agents\skills\traycmd\SKILL.md`（仓库副本在 `skills\traycmd\`）。装了该 skill 的 agent（ZCode 等）在用户说"加到托盘 / 注册命令 / 以后一键运行"时会自动调用上述 CLI 完成注册。

SKILL.md 在构建时以嵌入资源（LogicalName `skill.md`）打进 `quickcmd.exe`，`quickcmd skill`（等价 `/skill`）可直接显示——exe 单独拷到任何机器都自带文档。

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
- `ps` / `kill` 只在 qchost 运行时有意义（实例存活于 qchost 进程内，这两个命令不自动拉起 qchost）：`ps` 每条实例的 ID 为字符串——[JOB] = `job-<N>`（qchost 按作业创建顺序分配的序号；内核不向用户态暴露作业对象的数字 ID，实测 ordinal #793 `GetJobObjectInfo` 对匿名作业返回全零，拿不到 KMTID，见 `testdata/probe-jobid.ps1`），[PID] = root pid；带 `*` = root 已退、靠作业续命。下方缩进树为真实进程树（JOB 成员 + ppid 链上的逃逸子孙）；`kill <ID>` 对 [JOB] 先 `TerminateJobObject` 杀作业成员，再逐个 `Kill(true)` 补杀逃逸子孙，传树内任意成员 pid 也会定位到其作业实例。
- `--command-text` 已被 `--program` + `--args` 取代；裸命令（`npm` 等 .cmd）直接启动找不到时会自动回退经 `cmd /k` 解析。
- 默认保持窗口：python 用 `-i`（脚本跑完停在交互界面）、ps1 用 `-NoExit`、bat 用 `/k`；`--close-on-exit` 反之。
- 数据文件手工损坏的行会被跳过，并在菜单/`list` 中提示行数。
