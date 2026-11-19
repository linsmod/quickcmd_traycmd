# 执行历史（审计）与免注册直跑

> 2026-11-08 设计记录。实现：`quickcmd exec` + `history.jsonl`。

## 1. 要解决的问题

`quickcmd add` 适合“以后还要点”的常驻命令，但一次性的排查/构建命令每次都注册一条，
既污染菜单也难清理。同时需要一个**可审计**的执行记录：谁在什么时候、通过 CLI 还是托盘、
在哪个目录、以什么模式执行了什么，成功没有。

因此新增两条能力：

1. `quickcmd exec -- <命令行>`：**免注册直跑**，不写 `commands.jsonl`；
2. `history.jsonl`：所有执行（注册项 + 直跑）的 **append-only 审计日志**。

## 2. 为什么必须经 qchost

qchost（常驻托盘宿主，`qchost.exe`）是唯一在**调用方沙盒之外**运行的进程
（由托盘应用 / 用户登录启动）。命令只有交给它 `CreateProcess` 才具备“绕过调用方
文件/网络限制”的能力，同时才能进入作业对象跟踪（`quickcmd ps` / 托盘实例列表）。

所以 `exec` 的默认路径是：

```
quickcmd.exe (CLI) --命名管道--> qchost.exe --作业对象--> cmd.exe /d /s /c "<命令行>"
```

qchost 不在时 **不静默降级**：CLI 返回退出码 3 并提示启动托盘；只有显式
`exec --local` 才在当前进程直启（会记 `source=cli-local`，并提示可能受沙盒限制）。

## 3. CLI

```powershell
# 一次性命令（不注册；默认 JOB 模式，跑完即退）
quickcmd exec --workdir D:\code\app -- npm run build
quickcmd exec --label "查端口" -- netstat -ano ^| findstr 8080
quickcmd exec --program cmd.exe --args "/c exit 3" --title direct

# 查看审计（最近 20 条；--json 给程序读；--grep 过滤命令行/标题/状态）
quickcmd history --limit 10
quickcmd history --grep npm --limit 5 --json

# 清空（不可撤销，需 -y）
quickcmd history --clear -y

# 宿主升级/排障
quickcmd host status
quickcmd host restart
```

`--` 之后的整行原样交给 `cmd /c`；`cmd.exe` 自身的 `--flag` 不会被 quickcmd 吞掉。
选项只能写在 `--` 之前（`--workdir/--env/--title/--mode/--program/--args/--keep-open/--local`）。

## 4. 审计数据

文件：`%APPDATA%\quickcmd\history.jsonl`（一行一个 JSON 事件，永不就地修改）。
**唯一写入者设计**：执行事件由 qchost 写（CLI `exec`/`run` 都经它），避免多进程交错；
追加用命名互斥体 `Local\quickcmd-history` 串行化；读取（CLI/托盘）同时进行不受影响。

一次执行两条记录，用 `execId` 关联：

| 字段 | 含义 |
|---|---|
| `timestamp` | 本地时间（人工对表用） |
| `event` | `start` / `finish` |
| `kind` | `run`（注册项）/ `exec`（免注册直跑） |
| `source` | `cli` / `palette`（命令面板）/ `webui`（本地控制台）/ `qchost`（finish）/ `qchost-auto`（宿主自动拉起）/ `cli-local`（显式本地兜底） |
| `execId` | 关联一次执行：注册项 = 命令 Id；直跑 = `adhoc-<hex>` |
| `title` / `shortId` | 注册项标题 / short id（直跑为 adhoc 前缀） |
| `commandLine` | 实际执行的命令行（含解释器/包装） |
| `workdir` / `mode` / `instanceId` / `pid` | 执行位置与实例信息 |
| `status` | `started` `exited` `killed` `failed` `max` `host-unavailable` `local` |
| `exitCode` | root 退出码（未知 = `int.MinValue`）|
| `durationMs` | 从启动到结束（finish 事件）|
| `requesterPid` | 发起执行的 CLI/托盘进程 |
| `logPath` | 捕获型执行（`--capture-logs`）的输出日志；空 = 未捕获 |

实现要点：

- JOB 模式保留 root 进程句柄，作业完成后 `GetExitCodeProcess` 取退出码（root 早退也拿得到）；
- `kill` 走单独收尾：先取退出码、记 `finish/killed`，再释放作业句柄（避免被 `Prune` 跳过）；
- 长时间窗口（`--keep-open`）在窗口关闭时记 finish；qchost 崩溃/重启会丢失未完成的 finish（已知边界）。

## 5. 已知边界

- 审计只覆盖“执行”，不覆盖 add/update/delete（CRUD 由 `commands.jsonl` 自身审计）；
- 不抓 stdout/stderr（避免阻塞/二进制问题）；需要输出用命令自身重定向到文件；
- `--local` 直启只有 start 记录，没有 finish（CLI 退出后无人跟踪）；
- 历史文件只增不减，靠 `history --clear -y` 或托盘“清空历史…”显式清理；
- 命令行走 `cmd /c`，Windows 的引号嵌套规则原样生效（复杂命令建议写脚本）。
