---
name: traycmd
description: Run one-shot commands outside the caller's sandbox, register one-click commands in the Windows system tray (CLI quickcmd.exe, host processes qchost/qcApp), host local static sites/previews with a stable http://127.0.0.1 URL, and read captured stdout/stderr logs. `quickcmd exec -- <cmd>` runs a command through the tray host without registering it and records it in an audit history; `quickcmd add` registers a command to rerun later from the palette (double-click tray icon / long-press Win) or CLI; `quickcmd add --type web ... --program quickcmd --args "serve --port N"` registers a local site, `quickcmd url <short-id>` returns its URL; `--capture-logs` + `quickcmd logs <short-id|job-N>` shows a service's output without a console window. Use whenever the user says 加到托盘 / 注册到托盘 / 注册个命令 / 以后一键运行 / 以后我自己点 / 托管网站 / 起个预览地址 / 看日志 / 服务起不来, or when a command must escape the agent sandbox (file/network restrictions) and only needs to run once, or when you start a dev server/watch task/build the user may want to relaunch alone.
---

# traycmd — 托盘一键命令 / 免注册直跑

## 域映射（对用户回复只用右列，内部名不出现在对话里）

| 执行域术语 | 用户感知的说法 |
|---|---|
| `quickcmd.exe`（CLI） | "托盘命令"（注册/删除/执行的入口） |
| qchost / qcApp 常驻进程 | "托盘" |
| 注册的条目 title | 用户自己起的名字 = 菜单项 |
| `exec` + `history.jsonl` | "免注册直接执行 / 执行记录" |
| `--type web` + 托盘"网站"分区 | "本机网站 / 预览地址" |
| `--capture-logs` + `quickcmd logs` | "运行日志 / 报错输出" |
| short id | 不用向用户解释，自己留档用于后续定位 |

- CLI：`C:\Users\linswin\Documents\quickcmd\traycmd\dist\quickcmd.exe`（下文简写 `quickcmd`）。**选项与用法以 `quickcmd help` 实时输出为准**，不要凭记忆编造 flag。
- 数据文件 `%APPDATA%\quickcmd\commands.jsonl`、审计文件 `%APPDATA%\quickcmd\history.jsonl` 永不手改，一律走 CLI。

## 决策规则

- **一次性/临时命令：不要 add，直接用 `quickcmd exec`**（见下）。它经 qchost 执行（不受调用方沙盒限制）并自动记审计；只有“以后还要点/要出现在托盘菜单”才 add。
- 必须注册时：必填 `--title` +（`--program` 可执行程序 | `--shell-script` .py/.bat/.ps1 脚本，二选一）。`--proj` 项目名、`--workdir` 执行目录、`--args` 传参（值可以 `-` 开头）。
- 启动注册实例用 `run-job`（整棵进程树跟踪，杀进程更准）；仅用户明确要求时用 `run`（PID 模式）。
- 用户要"把某个目录/页面挂出来给个地址"时，用 **web 命令 + 内置 `serve`**（见下），不要自己去起临时 http.server 然后不管。
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

## 托管网站（web 命令）

用户说"把这个目录 / 页面挂出来、起个预览地址"时，用内置静态服务，不要另起临时服务器：

```powershell
# 站点根目录 = --workdir（web 类型要求目录已存在）
quickcmd add --type web --title "站点预览" --proj demo `
  --url http://127.0.0.1:41001/ --workdir D:\out\site --auto-start --capture-logs `
  --program quickcmd --args "serve --port 41001 --spa"

quickcmd run-job <short-id>     # 启动（已在跑时只会置前，不会重启）
quickcmd url <short-id>         # → http://127.0.0.1:41001/
quickcmd ps                     # 实例行尾带 · URL
quickcmd logs <short-id>        # serve 的输出/报错（403/404/端口占用等）
quickcmd kill <实例ID>           # 停止
```

- `--program quickcmd` 必须在 add 时写裸名：CLI 会解析成本机 quickcmd.exe 的绝对路径；qchost 里 `Environment.ProcessPath` 指向 qchost.exe，写别的相对路径会起错程序。
- 端口自己选 41000–41999 中未被占用的。`serve` 端口被占用会**报错退出**（退出码 1），不会自动换端口——URL 必须稳定。启动后 `ps` 看不到实例或日志报占用，就换一个端口重新 add/update。
- `--spa` = 前端路由回退：无扩展名的未命中路径返回 index.html；缺失的 .js/.css 仍 404。
- 只绑定 `127.0.0.1`，没有公网地址。汇报时说"本机预览地址"，不要承诺外网可访问。
- 重启：先 `kill` 再 `run-job`；`run-job` 触达单实例上限时只会把现有实例置前。
- 用户侧入口：托盘"网站"分区（● 运行中 / ○ 已停止，可打开/复制 URL、停止/启动、查看日志）；命令面板里 web 项显示 URL，`Ctrl+Enter` 直接打开。
- 直接前台调试（不注册）可用 `quickcmd serve --port 41001 --spa`，默认托管当前目录，`--dir` 覆盖。

## 输出日志（常驻服务排查）

`--capture-logs` 让 stdout/stderr 进日志文件、同时隐藏控制台窗口。适合 serve / dev server / 后台 API；交互式命令不要开。

```powershell
quickcmd logs                          # 最近 20 个日志（时间/大小/文件名）
quickcmd logs <short-id>               # 该命令最新一次输出（默认 60 行）
quickcmd logs job-1 --tail 200         # 某次执行（实例 ID）
quickcmd logs <short-id> --path        # 只要路径（自己读全文/交给编辑器）
quickcmd logs <short-id> --clear -y    # 删除日志
```

- 目录：`%APPDATA%\quickcmd\logs\<short-id>-<时间>.log`；exec 直跑是 `exec-<8位>-…`。
- 排障顺序：`ps` 看实例在不在 → `logs <实例ID>` 看最后几十行 → 端口/路径/权限错误通常都在里面。
- `logs` 支持 UTF-8 与 GBK 混合输出；超过 512 KB 只取尾部。
- 依赖 qchost 的 JOB 模式：qchost 不在时会降级直启并**不捕获**（输出里会提示）。要日志就先确保托盘宿主在跑。
- `exec --capture-logs -- <命令>` 也能捕获一次直跑的输出，之后 `quickcmd logs <实例ID>` 取。

## 本地控制台（给用户看/点）

```powershell
quickcmd ui --no-open   # 只打印 URL，例 http://127.0.0.1:41777/
```

- 由 qchost 提供，只绑 127.0.0.1；默认端口 41777，被占用时自动顺延（以输出 URL 为准）。
- 页面可看 首页 / 命令 / 实例 / 历史，并做 **启动 / 停止**；带日志的条目有"日志"按钮（`GET /api/log?key=...`）。增删改仍走 CLI。
- 卡片视图的「固定到首页」把命令放大图标放进首页（存在 `settings.json`，跨浏览器保留）；首页点图标即启动，运行中的网站点图标直接开 URL。
- 搜索：`Ctrl+K` 或直接敲字弹出浮层，`Esc` 取消；`Enter` 在命令上=启动、在历史条目上=打开日志。浮层用打开时的快照，不会被轮询重绘。
- 最近：`Ctrl+R` 同一浮层，上半是最近关闭（可 Enter 重跑），下半是正在运行（Enter 打开 URL 或置前）。
- 用户问"现在有哪些命令 / 在跑什么 / 怎么看执行记录"时，优先给控制台 URL，不要让用户记 CLI 参数。
- 写给控制台的执行在历史里 `source=webui`，与 CLI 记录同格式。

## 执行历史（审计）

```powershell
quickcmd history                 # 最近 20 条：时间/事件/类型/来源/状态/命令行
quickcmd history --limit 50 --grep npm
quickcmd history --json          # 机器可读（一行一个 HistoryEntry）
quickcmd history --clear -y      # 清空（不可撤销，必须 -y）
```

- 每次执行两条记录（`event=start/finish`），用 `execId` 关联；注册项 `kind=run`，直跑 `kind=exec`。
- 记录：时间、来源（cli/palette/webui/qchost-auto）、命令行、workdir、跟踪模式、实例 ID/pid、状态（started/exited/killed/failed/max/host-unavailable/local）、退出码、耗时、requesterPid。
- 托盘菜单也有“历史记录”子菜单（最近 8 条 + 打开/清空历史文件）。
- 审计只覆盖执行，不覆盖 add/update/delete；不抓 stdout/stderr。

## 沟通义务（托盘不弹系统通知，你的汇报 = 用户唯一的反馈渠道）

- exec 成功：告知"已通过托盘执行（不受沙盒限制）"并说明已记入执行历史；需要证据时可给 `quickcmd history --limit 5` 的结果。
- exec 失败：区分“命令失败（exitCode 非 0）”与“宿主不在（退出码 3）”，后者提示启动托盘后重试；`--local` 降级必须如实说明"可能受沙盒限制、不进托盘实例列表"。
- 注册成功：告诉用户"托盘菜单里将显示「title」"，内部记下 short id。add 输出有 `⚠ 同名警告` 时，向用户说明重名并建议改名。
- 托管网站：必须报**实际 URL**（以 `quickcmd url` 输出为准）和停止方式（托盘"网站"→停止，或 `quickcmd kill <ID>`），并说明只有本机 127.0.0.1 能访问，不是公网部署。
- 启动成功（注册项）：说明以后用命令面板（双击托盘图标 / 长按 Win）搜「title」即可重跑；托盘菜单第一级列出运行中的实例（结束任务 / 打开文件所在位置 / 打开工作目录）。启动失败，或降级为直接启动（提示词含"不会被跟踪"）时，必须如实转告并给出补救（重开托盘应用后重试）。
- 删除/结束类操作：报一次结果即可，不展开内部细节。

## 边界

- 增删改查不依赖托盘在跑；`run` 时 qchost 没跑会自动拉起，无需你干预；`exec` 在 qchost 不在时**不会**自动静默本地执行（退出码 3）。
- 升级 qchost/quickcmd 后用 `quickcmd host restart` 让常驻宿主加载新版本；`host status` 查在线状态。
- web 托管只绑 127.0.0.1：不外网、不做端口转发；用户要公网访问时明确说当前不支持，别用临时 tunnel 冒充本功能。
- 不要在会话里手动启动/结束 qchost.exe / qcApp.exe 进程，除非用户点名要求（部署新版本时的重启除外，且要告知用户）。
- `ps` 列出的是"运行中的实例"，不是"已注册命令"；两者别混着向用户汇报。
