---
name: quickcmd
description: quickcmd是一个用于管理常用命令的cli工具，可以让用户在配套的quick_run程序（启动后寄宿系统托盘）点击右键就能执行已注册程序/命令
---

# quickcmd — 托盘一键命令注册

quickcmd是一个用于管理常用命令的cli工具，配套宿主进程 **qchost**（quick command host，即 quick_run.exe，常驻系统托盘）——本 skill 一律用 qchost 指代这个托盘宿主，不要说"GUI"（以免和被运行出来的目标 GUI 程序混淆）。用户在 qchost 托盘右键菜单点已注册命令即可一键运行。
qchost 还有类似 vscode ctrl+shift+p 的命令面板，使用长按 win key 作为快捷键弹出支持搜索的命令列表窗口。

## 关键路径

- CLI: `C:\Users\linswin\Documents\repo_ls\quickcmd\dist\quickcmd.exe`（下文简写为 `quickcmd`）
- 数据文件: `%APPDATA%\quickcmd\commands.jsonl`（每行一条 JSON 命令定义，UTF-8）
- qchost 未运行时也能执行全部 CRUD；`quickcmd run` / `run-job` 依赖 qchost 跟踪实例（qchost 没跑会自动拉起 quick_run.exe）

## 核心规则

1. **注册用 `add`，结果用 `list` 确认**。add 会生成 GUID 并输出 8 位 short id，记录它或让用户记住 title。
2. **定位命令用 short id**（8 位）。title 允许重名——add 后注意输出中的 `⚠ 警告`，有重名时后续操作必须用 short id 而不是 title。
3. **必填**：`--title` 和（`--program` 或 `--shell-script`）二选一。
4. 一次注册一个明确用途的命令；title 用用户能看懂的名字（中文即可），`--proj` 填项目名，托盘菜单会按项目分组。
5. 带值选项的值即使以 `-` 开头也会被正确识别（如 `--args "--version"`）；但不要给开关选项传值。

## CLI 参考

```
quickcmd add    --title <名> (--program <程序> | --shell-script <脚本>) [选项]
quickcmd list   [--proj <项目>] [--json]
quickcmd get    <short-id|id|title> [--json]
quickcmd update <short-id> [选项]        # 只改给出的字段
quickcmd delete <short-id> [-y]
quickcmd run     <short-id>             # [PID] 模式：按 root pid 进程树跟踪实例
quickcmd run-job <short-id>             # [JOB] 模式：作业对象整树跟踪（推荐，杀进程/存活判定更准）
quickcmd ps                              # 列出 qchost 跟踪中的实例（[JOB] 展示整棵进程树，ID=根 pid）
quickcmd kill  <实例ID>                  # 结束实例：[JOB] 杀整棵作业树，传树内任意 pid 也会定位到其 job
quickcmd path                            # 打印数据文件路径
```

### add / update 选项

| 选项 | 说明 |
|---|---|
| `--proj <项目名>` | 项目分组名 |
| `--workdir <目录>` | 执行目录；支持 `%VAR%` 占位符；留空 = 用户目录 |
| `--program <程序>` | 可执行程序：exe 路径或 PATH 中的名称；`npm` 这类裸命令（实际是 .cmd）自动经 cmd 解析 |
| `--shell-script <脚本>` | 脚本文件，按扩展名分派：`.py/.pyw`→python(pythonw)、`.bat/.cmd`→cmd、`.ps1`→pwsh（未装则回退 powershell）。其他扩展名会被拒绝 |
| `--args <参数>` | program / shell-script 的附加参数（可含 `-` 开头的值） |
| `--interpreter <路径...>` | 解释器查找顺序覆盖：`;` 分隔的文件/目录，按顺序查找，全部无效则报错；不指定则落系统 PATH。用于解释器多版本，如 `--interpreter "D:\py312;D:\py311"` |
| `--env <KEY=VAL>` | 环境变量，**可重复多次**；VAL 支持 `%VAR%` 引用系统环境变量，如 `PATH=%PATH%;D:\tools` |
| `--max-instances <N>` | 并发实例上限，默认 1；已到上限再点击会置前现有实例而不是新开 |
| `--close-on-exit` | 命令结束后关闭窗口；**默认保持窗口打开**（python 用 `-i`、ps1 用 `-NoExit`、bat 用 `/k`），一次性脚本命令可加此选项 |

## 典型场景

用户说"把这个加到托盘，以后我自己点"：

```powershell
quickcmd add --title "Dev Server" --proj myapp --workdir D:\code\myapp --program "npm" --args "run dev"
# 记下输出的 short id（如 1a2b3c4d），告诉用户托盘菜单里叫 "Dev Server"
```

注册 python 脚本并锁定解释器版本：

```powershell
quickcmd add --title "数据同步" --proj myapp --workdir D:\jobs --shell-script D:\jobs\sync.py --interpreter "D:\py312" 
```

注册 GUI 程序（不一定是终端）：

```powershell
quickcmd add --title "记事本" --program notepad.exe --max-instances 2
```

你刚帮用户跑起了长时间任务，用户以后想自己重启：

```powershell
quickcmd add --title "前端构建" --proj myapp --workdir D:\code\myapp\web --program "npm" --args "run build" --close-on-exit
```

修改 / 下线：

```powershell
quickcmd update 1a2b3c4d --max-instances 3
quickcmd delete 1a2b3c4d -y
```

## 注意事项

- 注册成功后菜单**立即生效**（qchost 每次打开菜单时重读数据文件），无需重启 quickcmd。
- qchost 没运行时 `run` / `run-job` 会自动拉起 quick_run.exe 再交给它执行（实例进托盘菜单可管理）；仅当找不到/拉不起 qchost 才退回直接启动（不跟踪）。
- 实例跟踪模式在托盘菜单实例标签上以 `[JOB]` / `[PID]` 前缀标注；`run-job` 遇作业对象不可用会自动退化 `[PID]`。优先用 `run-job`，除非用户明确要 `[PID]`。
- 除非用户明确要求，不要手改 commands.jsonl——用 CLI，行格式损坏会被跳过并在菜单上提示。
- 不要在 agent 会话里启动或杀掉 qchost（quick_run.exe）进程，除非用户要求；`quickcmd run` / `run-job` 会在需要时自动拉起 qchost。
