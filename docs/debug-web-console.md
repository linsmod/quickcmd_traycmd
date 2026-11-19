# Web 控制台调试与预览

最后核对：2026-11-19

## 1. 快速预览（不重启宿主）

页面内嵌在 `quickcmdhost/WebConsole.cs`，改完源码后如果直接重启宿主验证，会中断正在运行的任务。用预览工具只重载页面：

```powershell
node tools/preview-web-console.js
# 默认 http://127.0.0.1:43999/ ，上游读 %APPDATA%\quickcmd\settings.json 的 uiPort
```

- 工具每次请求都重新读取 `WebConsole.cs`，改完源码刷新页面即可，不需要重启 qchost，也不需要重新编译（只改了 HTML 时）。
- `GET /api/state`、`/api/log` 代理到真实宿主（默认 41777）。
- `POST /api/*` 返回 501：预览页里的启动/停止/固定会失败，这是预期行为；写操作请在真实控制台验证。
- 上游不在时会返回 502，页面显示「控制台未连接」。
- 工具只绑定 `127.0.0.1`，不会对外暴露。

已验证（2026-11-19，宿主 41777 在线）：`/` 200、`/api/state` 200、`/api/log` 200、`POST /api/run` 501。

## 2. 真实控制台

```powershell
quickcmd ui            # 打印并打开真实控制台
quickcmd ui --no-open  # 只打印 URL
quickcmd host status   # 宿主是否在线
```

端口来自 `%APPDATA%\quickcmd\settings.json` 的 `uiPort`，当前为 41777。

## 3. 只读检查接口

```powershell
$s = Invoke-WebRequest -UseBasicParsing http://127.0.0.1:41777/api/state | Select-Object -Expand Content | ConvertFrom-Json
$s.host
$s.commands | Select-Object shortId,title,proj,state,instanceId
$s.instances | Select-Object id,shortId,title,started,logPath
$s.history | Select-Object -First 10 timestamp,event,shortId,status,exitCode,durationMs,instanceId
```

日志：

```powershell
Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:41777/api/log?key=<short-id|实例ID|日志文件名>&tail=50"
```

## 4. Edge headless 验证布局

Edge 在这里比 Chrome 稳定：窗口尺寸会真正生效（Chrome headless 若复用了已有实例/配置，`--window-size` 可能被忽略，回落到 800×600 左右）。**必须用独立的 `--user-data-dir`**，并且不要结束用户正在使用的浏览器进程。

```powershell
$edge = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
$out  = "$env:TEMP\qc-console.png"
$prof = "$env:TEMP\qc-edge-" + [Guid]::NewGuid().ToString("N")
& $edge --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 `
  "--window-size=1600,900" --user-data-dir=$prof `
  --virtual-time-budget=5000 --screenshot=$out `
  "http://127.0.0.1:43999/#%E5%85%A8%E9%83%A8/7aec2859"
```

- `--dump-dom` 代替 `--screenshot` 可检查实际渲染出的 DOM（`data-w`、`data-rail`、`data-sheet`、表行）。
- `--virtual-time-budget` 让 3 秒轮询和日志请求有机会完成；单次约 3–6 秒。
- 断点自测至少覆盖：1600（wide + 项目栏）、1400（wide 无项目栏）、1200（wide + compact）、1150（mid）、800/600（narrow）。
- 需要验证点击（属性、历史、筛选、返回）时，可在临时预览页的 `</body>` 前注入一段一次性脚本：

```html
<script>
setTimeout(function () { document.querySelector('[data-open-sheet="props"]').click(); }, 1200);
</script>
```

再用 `--dump-dom` 检查结果。临时脚本不要提交。

## 5. 常见问题

| 现象 | 原因 / 处理 |
|---|---|
| 预览页显示「控制台未连接」 | qchost 未运行或端口不对；`quickcmd host status`，或给工具加 `--upstream <port>` |
| 预览页启动/停止报错 | 预览不代理写操作（没有宿主 token）；到真实控制台操作 |
| 日志 404 /「日志还没有内容」 | 命令未开 `--capture-logs`，或日志已被清理 |
| 无日志按钮 | 历史记录里的日志文件已不存在，`/api/state` 已把 `logPath` 置空 |
| 布局断点不符合预期 | 断点按「右半区宽度」算：`rightW = 窗口 - 项目栏(≥1500 才有 216) - Launcher(clamp 300/29%/462)` |
| 改了 HTML 但页面没变 | 预览工具每次读源码，刷新即可；真实控制台需要重新编译并重启宿主 |
| 停不掉/覆盖不了 dist | qchost/qcApp 正在占用 dist 里的文件，先停宿主并退出托盘，见 `workflows/wf-build-and-run.md` |

## 6. 构建

```powershell
dotnet build quickcmdhost/quickcmd-host.csproj -v q --nologo   # 实测约 1.5–3 秒
```

编译只验证 C#；内嵌 JS 的语法可用下面的方式先抽出来检查：

```powershell
$src = Get-Content quickcmdhost\WebConsole.cs -Raw
$m = [regex]::Match($src, '(?s)<script>(.*?)</script>')
[IO.File]::WriteAllText("$env:TEMP\qc-console.js", $m.Groups[1].Value)
node --check "$env:TEMP\qc-console.js"
```
