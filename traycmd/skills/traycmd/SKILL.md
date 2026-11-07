---
name: traycmd
description: Run one-shot commands outside the caller's sandbox and register one-click commands in the Windows system tray (CLI quickcmd.exe, host processes qchost/qcApp). `quickcmd exec -- <cmd>` runs a command through the tray host without registering it and records it in an audit history; `quickcmd add` registers a command to rerun later from the palette (double-click tray icon / long-press Win) or CLI. Use whenever the user says 加到托盘 / 注册到托盘 / 注册个命令 / 以后一键运行 / 以后我自己点, or when a command must escape the agent sandbox (file/network restrictions) and only needs to run once, or when you start a dev server/watch task/build the user may want to relaunch alone.
---

# traycmd — 托盘一键命令 / 免注册直跑

## 域映射（对用户回复只用右列，内部名不出现在对话里）

| 执行域术语 | 用户感知的说法 |
|---|---|
| `quickcmd.exe`（CLI） | "托盘命令"（注册/删除/执行的入口） |
| qchost / qcApp 常驻进程 | "托盘" |
| 注册的条目 title | 用户自己起的名字 = 菜单项 |
| `exec` + `history.jsonl` | "免注册直接执行 / 执行记录" |
| short id | 不用向用户解释，自己留档用于后续定位 |

- CLI：`C:\Users\linswin\Documents\quickcmd\traycmd\dist\quickcmd.exe`（下文简写 `quickcmd`）。**选项与用法以 `quickcmd help` 实时输出为准**，不要凭记忆编造 flag。
- 数据文件 `%APPDATA%\quickcmd\commands.jsonl`、审计文件 `%APPDATA%\quickcmd\history.jsonl` 永不手改，一律走 CLI。

## 决策规则

- **一次性/临时命令：不要 add，直接用 `quickcmd exec`**（见下）。它经 qchost 执行（不受调用方沙盒限制）并自动记审计；只有“以后还要点/要出现在托盘菜单”才 add。
- 必须注册时：必填 `--title` +（`--program` 可执行程序 | `--shell-script` .py/.bat/.ps1 脚本，二选一）。`--proj` 项目名、`--workdir` 执行目录、`--args` 传参（值可以 `-` 开头）。
- 启动注册实例用 `run-job`（整棵进程树跟踪，杀进程更准）；仅用户明确要求时用 `run`（PID 模式）。
- 默认单实例：达到上限再触发是"置前已有窗口"，不是新开——用户想并跑多个时调 `--max-instances`。
- 后续定位一律用 add 输出的 8 位 short id；title 允许重名，见下。
- `exec` 默认**必须经 qchost**；qchost 不在时直接失败（退出码 3）而不是静默本地执行。只有明确知道沙盒无碍时才加 `--local`。

## exec（免注册直跑）

```powershell
quickcmd exec --workdir D:\code\app -- npm run build
quickcmd exec --label "查端口" -- netstat -ano ^| findstr 8080
quickcmd exec --program cmd.exe --args "/c exit 3" --title direct
```

- `--` 之后的整行原样交给 `cmd /c`（默认跑完即退；`--keep-open` 用 `/k` 留窗口）。
- 常用选项：`--workdir`、`--env KEY=VAL`（可重复）、`--title/--label`（历史显示名）、`--mode job|pid`、`--program` + `--args`（直启程序，与命令行二选一）、`--local`。
- 成功输出 `[JOB] 已启动 job-N（免注册直跑）`；**退出码 3 = qchost 不在**，应提示用户启动托盘（qcApp）后重试。

## 执行历史（审计）

```powershell
quickcmd history                 # 最近 20 条：时间/事件/类型/来源/状态/命令行
quickcmd history --limit 50 --grep npm
quickcmd history --json          # 机器可读（一行一个 HistoryEntry）
quickcmd history --clear -y      # 清空（不可撤销，必须 -y）
```

- 每次执行两条记录（`event=start/finish`），用 `execId` 关联；注册项 `kind=run`，直跑 `kind=exec`。
- 记录：时间、来源（cli/palette/qchost）、命令行、workdir、跟踪模式、实例 ID/pid、状态（started/exited/killed/failed/max/host-unavailable/local）、退出码、耗时、requesterPid。
- 托盘菜单也有“历史记录”子菜单（最近 8 条 + 打开/清空历史文件）。
- 审计只覆盖执行，不覆盖 add/update/delete；不抓 stdout/stderr。

## 沟通义务（托盘不弹系统通知，你的汇报 = 用户唯一的反馈渠道）

- exec 成功：告知"已通过托盘执行（不受沙盒限制）"并说明已记入执行历史；需要证据时可给 `quickcmd history --limit 5` 的结果。
- exec 失败：区分“命令失败（exitCode 非 0）”与“宿主不在（退出码 3）”，后者提示启动托盘后重试；`--local` 降级必须如实说明"可能受沙盒限制、不进托盘实例列表"。
- 注册成功：告诉用户"托盘菜单里将显示「title」"，内部记下 short id。add 输出有 `⚠ 同名警告` 时，向用户说明重名并建议改名。
- 启动成功（注册项）：说明以后用命令面板（双击托盘图标 / 长按 Win）搜「title」即可重跑；托盘菜单第一级列出运行中的实例（结束任务 / 打开文件所在位置 / 打开工作目录）。启动失败，或降级为直接启动（提示词含"不会被跟踪"）时，必须如实转告并给出补救（重开托盘应用后重试）。
- 删除/结束类操作：报一次结果即可，不展开内部细节。

## 边界

- 增删改查不依赖托盘在跑；`run` 时 qchost 没跑会自动拉起，无需你干预；`exec` 在 qchost 不在时**不会**自动静默本地执行（退出码 3）。
- 升级 qchost/quickcmd 后用 `quickcmd host restart` 让常驻宿主加载新版本；`host status` 查在线状态。
- 不要在会话里手动启动/结束 qchost.exe / qcApp.exe 进程，除非用户点名要求（部署新版本时的重启除外，且要告知用户）。
- `ps` 列出的是"运行中的实例"，不是"已注册命令"；两者别混着向用户汇报。
