# 工作流：预览并验证 Web 控制台 UI

## 适用场景

- 修改 `quickcmdhost/WebConsole.cs` 里的内嵌 HTML/CSS/JS（页面结构、样式、交互）。
- 不想重启 qchost（会中断用户正在运行的任务）。
- 需要在多个视口宽度下核对布局，或验证点击/筛选/返回等交互。

不适用：修改 C# 服务端逻辑、新增 HTTP 字段或端点。那时需要重新编译并重启宿主，转 `wf-build-and-run.md`。

## 前置条件

- Windows + PowerShell。
- `qchost` 在线（`quickcmd host status`），默认 Web 端口 41777；端口读 `%APPDATA%\quickcmd\settings.json` 的 `uiPort`。
- Node.js（预览工具，无第三方依赖）。
- Edge 已安装：`C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe`。

## 相关文件

| 路径 | 作用 |
|---|---|
| `quickcmdhost/WebConsole.cs` | 内嵌页面与 HTTP 服务 |
| `tools/preview-web-console.js` | 提取内嵌页面并提供预览/只读代理 |
| `docs/arch-web-console.md` | 端点、数据映射、断点 |
| `docs/debug-web-console.md` | 调试命令与常见问题 |

## 步骤

### 1. 编译与语法检查

```powershell
dotnet build quickcmdhost/quickcmd-host.csproj -v q --nologo     # 实测约 1.5–3 秒（增量）
```

只改了 HTML 时也必须过一遍编译（raw string 在 C# 里）。再把 `<script>` 抽出来检查 JS：

```powershell
$src = Get-Content quickcmdhost\WebConsole.cs -Raw
$m = [regex]::Match($src, '(?s)<script>(.*?)</script>')
[IO.File]::WriteAllText("$env:TEMP\qc-console.js", $m.Groups[1].Value)
node --check "$env:TEMP\qc-console.js"
```

### 2. 起预览

```powershell
node tools/preview-web-console.js --port 43999
```

- 浏览器打开 `http://127.0.0.1:43999/`。
- 工具每次请求重读源码，改完保存后刷新页面即可。
- 只代理 GET `/api/state`、`/api/log`；写操作返回 501。
- 上游端口不对时用 `--upstream 41777`；只调样式时可用 `--no-upstream`。

### 3. 多断点验证

用独立 profile 启 Edge headless，截图或 dump DOM：

```powershell
$edge = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
$prof = "$env:TEMP\qc-edge-" + [Guid]::NewGuid().ToString("N")
& $edge --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 `
  "--window-size=1600,900" --user-data-dir=$prof --virtual-time-budget=5000 `
  --screenshot="$env:TEMP\qc-1600.png" "http://127.0.0.1:43999/#%E5%85%A8%E9%83%A8/<short-id>"
```

检查 DOM 时把 `--screenshot=...` 换成 `--dump-dom`，重点看：

- `body` 上的 `data-w`（wide/mid/narrow）、`data-rail`、`data-compact`、`data-route`、`data-sheet`；
- `#launcher` 的 tile 数量、项目栏计数、记录表行数、`#pane` 是否渲染。

建议覆盖宽度：1600、1400、1200、1150、1000、800、600。

### 4. 交互 smoke

headless 不能点击，可临时在预览页 `</body>` 前注入一次性脚本（只用于测试，不提交）：

```html
<script>
setTimeout(function () { document.querySelector('[data-open-sheet="props"]').click(); }, 1200);
</script>
```

再 `--dump-dom` 检查属性面板、历史浮层、筛选结果、选中行、返回后的 `data-route`。曾用过的动作：`props`、`hist`、`failfilter`、`select2`、`replay`、`back`、`search`、`type`、`esc`、`nextscreen`。

### 5. 收尾

- 删除临时注入脚本、临时 profile 目录、截图。
- 若验证中发现设计问题，修在源码里再重复步骤 1–4。
- 页面改动同时更新 `docs/arch-web-console.md` 与 `docs/fix-web-console-ui.md`。

## 经验与坑

- **不要结束用户正在使用的浏览器进程**。用 `--headless=new` + 每次新的 `--user-data-dir`；headless 跑完自行退出。
- Chrome headless 在复用已有实例/配置时可能忽略 `--window-size`，回落到约 800×600；本次验证改用 Edge 后正常。两种浏览器都要独立 profile。
- `--virtual-time-budget` 给 3 秒轮询和日志请求留时间；本次单次 3–6 秒。
- 预览没有宿主 token，启动/停止/固定的真实结果只能到 `http://127.0.0.1:41777/` 验证。
- 页面每 3 秒比较 `/api/state` 的业务数据签名；改选中态要用稳定 key，不要用行下标。
- 日志刷新只对运行中的记录轮询；已结束的日志缓存一次即可。

## 退出条件

- 源码编译通过、JS 语法检查通过、目标断点与交互 smoke 通过。
- 需要发布时转 `wf-build-and-run.md`。

## 改进建议

- 如果后续交互验证频繁，可给 `tools/preview-web-console.js` 增加只读的 `--probe` 注入能力（例如 `--act props`）；目前为避免工具复杂化，注入脚本保持一次性。
