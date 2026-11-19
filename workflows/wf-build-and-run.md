# 工作流：编译、发布并重启 qchost / 托盘

## 适用场景

- 改了 C# 源码，需要让正在运行的 qchost 用上新逻辑。
- 需要刷新 `traycmd/dist` 发布目录。
- 排障：确认当前运行的是哪个版本、端口和宿主状态。

不适用：只改 `WebConsole.cs` 内嵌 HTML。那种情况先走 `wf-preview-and-verify-web-ui.md`，确认后再按本流程发布。

## 环境与布局

- 仓库：`C:\Users\linswin\Documents\quickcmd`，分支 `main`。
- SDK：.NET 9（本次核对 `dotnet --version` = 9.0.308）。
- 运行中的进程（2026-11-19 核对）：
  - `traycmd\dist\qchost.exe`：常驻宿主，IPC + Web 控制台（41777）。
  - `traycmd\dist\qcApp.exe`：托盘/命令面板。
  - `quickcmd.exe`：CLI，按次启动。
- 设置：`%APPDATA%\quickcmd\settings.json`（`uiPort`、`pinned`）。
- 发布目录：`traycmd\dist`（被 `.gitignore` 的 `**/dist/` 忽略，不入库）。
- 发布脚本：`traycmd\make_dist.ps1`（默认 Release）。

## 步骤

### 1. 先确认运行来源

```powershell
Get-Process qchost,qcApp,quickcmd -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,Path
quickcmd host status
```

如果进程 Path 不是当前仓库的 `traycmd\dist`，先问清楚用户在用哪个安装版，不要直接覆盖。

### 2. 单项目编译（快速验证）

```powershell
dotnet build quickcmdhost/quickcmd-host.csproj -v q --nologo
```

本仓库是 WPF/WinExe 混合解决方案；只改单个项目时优先单项目编译，避免无关项目把失败混进来。

### 3. 生成 dist（会删除并重建）

> `make_dist.ps1` 会先删除各项目的 `bin`，再删除整个 `traycmd\dist`，最后平铺复制。**发布前 dist 里的旧文件会消失**；如果构建失败，dist 为空。

因此顺序必须是：

1. 让用户保存手头工作。
2. 停宿主：`quickcmd host stop`。
3. 退出托盘 `qcApp`（用户操作；托盘会重新拉起宿主，退出后再发布）。
4. 确认没有进程占用 dist：

```powershell
Get-Process qchost,qcApp,quickcmd -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,Path
```

5. 发布（脚本在**当前目录**找 `*.sln`，解决方案位于 `traycmd\quickcmd.sln`，所以要先进 `traycmd`）：

```powershell
cd traycmd
powershell -NoProfile -ExecutionPolicy Bypass -File .\make_dist.ps1 -Configuration Release
cd ..
```

发布前必须得到用户同意：这会停止正在运行的任务跟踪，并重建发布目录。按 `AGENTS.md` 的危险操作规则处理。

### 4. 启动与验证

```powershell
traycmd\dist\qcApp.exe                 # 或用户平时的方式启动托盘
quickcmd host status                   # 在线
quickcmd ui --no-open                  # 打印真实控制台 URL
Invoke-WebRequest -UseBasicParsing http://127.0.0.1:41777/api/state | Select-Object StatusCode
```

- Web 控制台：`http://127.0.0.1:41777/`（端口以 settings 为准）。
- 新页面是否生效：打开后看是否为新版三栏/栈式布局；旧版是顶部「首页/命令/实例/历史」导航。

### 5. 回滚

- 本仓库没有保留旧 dist 的自动备份。发布前如需回滚，先把 `traycmd\dist` 复制到任务临时目录。
- 回滚 = 停进程 → 覆盖回旧 dist → 启动。不要用 `git clean` 动用户文件。

## 经验与坑

- **文件锁**：qchost/qcApp 运行时锁定 dist 里的 exe/dll；直接跑 `make_dist.ps1` 会出现删除/复制失败或留下混合版本。必须先停进程。
- **自动启动**：宿主启动后会按命令的 `--auto-start` 自动拉起 web 服务（`Program.AutoStartWeb`），验证时不要误判为“上次实例还在”。
- **端口顺延**：首选端口被占用时宿主会 +1 顺延并回写 settings；文档里的 41777 以当前 settings 为准。
- **tasks 会掉跟踪**：宿主重启后旧 JOB 实例的跟踪/停止能力取决于进程本身是否存活；重启前告知用户。
- 只改内嵌页面时不必走 dist，预览工具即可；发布是最后一步。

## 退出条件

- `host status` 在线、`/api/state` 返回 200、真实控制台显示新版本。
- 发布过程中断或失败：记录当前 dist 状态，按“回滚”恢复，未完成项登记到 `issues/INDEX.md`。
