---
name: traycmd
description: Register one-click commands into the Windows system tray (CLI quickcmd.exe, host processes qchost/qcApp) so the user can rerun them later from the command palette (double-click tray icon / long-press Win) or via CLI; the tray menu's first level lists running instances (kill / open file location / open work dir). Use whenever the user says 加到托盘 / 注册到托盘 / 注册个命令 / 以后一键运行 / 以后我自己点, or when you start a dev server, watch task, build, or any frequently re-run command the user may want to relaunch alone.
---

# traycmd — 托盘一键命令

## 域映射（对用户回复只用右列，内部名不出现在对话里）

| 执行域术语 | 用户感知的说法 |
|---|---|
| `quickcmd.exe`（CLI） | "托盘命令"（注册/删除命令的动作） |
| qchost / qcApp 常驻进程 | "托盘" |
| 注册的条目 title | 用户自己起的名字 = 菜单项 |
| short id | 不用向用户解释，自己留档用于后续定位 |

- CLI：`C:\Users\linswin\Documents\repo_ls\traycmd\dist\quickcmd.exe`（下文简写 `quickcmd`）。**选项与用法以 `quickcmd help` 实时输出为准**，不要凭记忆编造 flag。
- 数据文件 `%APPDATA%\quickcmd\commands.jsonl` 永不手改，一律走 CLI。

## 决策规则

- 必填 `--title` +（`--program` 可执行程序 | `--shell-script` .py/.bat/.ps1 脚本，二选一）。`--proj` 填项目名、`--workdir` 填执行目录、`--args` 传参（值可以 `-` 开头）。
- 启动实例用 `run-job`（整棵进程树跟踪，杀进程更准）；仅用户明确要求时用 `run`（PID 模式）。
- 默认单实例：达到上限再触发是"置前已有窗口"，不是新开——用户想并跑多个时调 `--max-instances`。
- 后续定位一律用 add 输出的 8 位 short id；title 允许重名，见下。

## 沟通义务（托盘不弹系统通知，你的汇报 = 用户唯一的反馈渠道）

- 注册成功：告诉用户"托盘菜单里将显示「title」"，内部记下 short id。add 输出有 `⚠ 同名警告` 时，向用户说明重名并建议改名。
- 启动成功：说明以后用命令面板（双击托盘图标 / 长按 Win）搜「title」即可重跑；托盘菜单第一级列出运行中的实例（结束任务 / 打开文件所在位置 / 打开工作目录）。启动失败，或降级为直接启动（提示词含"不会被跟踪"）时，必须如实转告并给出补救（重开托盘应用后重试）。
- 删除/结束类操作：报一次结果即可，不展开内部细节。

## 边界

- 增删改查不依赖托盘在跑；`run` 时 qchost 没跑会自动拉起，无需你干预。
- 不要在会话里手动启动/结束 qchost.exe / qcApp.exe 进程，除非用户点名要求。
- `ps` 列出的是"运行中的实例"，不是"已注册命令"；两者别混着向用户汇报。
