# Web 控制台架构（现状）

最后核对：2026-11-19

## 1. 入口与运行方式

- 实现文件：`quickcmdhost/WebConsole.cs`（HTTP 服务 + 内嵌单页 HTML/CSS/JS）。
- 启动：`quickcmdhost/Program.cs` 的 `Main` 在 IPC 服务之后调用 `WebConsole.Start()`，失败不影响宿主。
- 绑定：仅 `127.0.0.1`。
- 端口：首选 `%APPDATA%\quickcmd\settings.json` 的 `uiPort`（当前 41777）；被占用时从首选起顺延最多 20 个端口，并回写设置。
- 打开方式：`quickcmd ui`（打印并打开 URL），或浏览器访问 `http://127.0.0.1:<port>/`。
- 页面 token：宿主每次启动随机生成，响应时替换 `__QC_TOKEN__` 与 `__QC_PORT__` 后返回。

## 2. HTTP 端点

| 方法 | 路径 | 说明 | 鉴权 |
|---|---|---|---|
| GET | `/`、`/index.html` | 单页控制台 | 仅 Host 校验 |
| GET | `/api/state` | 命令 + 实例 + 历史 + 宿主信息快照 | 仅 Host 校验 |
| GET | `/api/log?key=&tail=` | 日志尾部；`key` 接受命令 short-id、实例 ID、日志文件名 | 仅 Host 校验 |
| GET | `/favicon.ico` | 204 | - |
| POST | `/api/run` | 启动注册命令（body `{"key":"short-id\|id\|title"}`） | `X-QC-Token` + Origin |
| POST | `/api/kill` | 结束实例（body `{"key":"实例ID"}`） | 同上 |
| POST | `/api/front` | 置前实例 | 同上 |
| POST | `/api/pin` | 首页固定（body `{"key":"...","pinned":true\|false}`） | 同上 |

- Host 校验用于防 DNS rebinding：只接受 `127.0.0.1:<port>` / `localhost:<port>`。
- Origin 校验：无 Origin（curl/本地脚本）放行，否则必须是同一 loopback 地址。
- `/api/run|kill|front` 与 IPC 走同一套 `IpcServer.HandleMessage`，`Source=webui`。
- 日志读取用 `LogStore.Resolve(key)` + `LogStore.Tail(path, tail)`；`tail` 默认 300，钳制 1–5000；返回首行带文件名、大小、修改时间。

## 3. `/api/state` 结构

```json
{
  "host": { "version": "...", "port": 41777, "uptimeSeconds": 0, "badLines": 0, "pinned": ["97957db5"] },
  "commands": [{ "id", "shortId", "title", "proj", "type": "web|app", "url", "autoStart", "captureLogs",
                 "log", "state": "running|stopped", "instanceId", "pid", "started", "kindText",
                 "commandLine", "program", "args", "shellScript", "interpreter", "env", "workdir",
                 "effectiveWorkdir", "maxInstances", "keepOpen", "createdAt", "updatedAt" }],
  "instances": [{ "id", "mode": "JOB|PID", "shortId", "title", "proj", "type", "url", "logPath",
                  "pid", "started", "rootExited", "tree": [{ "pid", "ppid", "name" }] }],
  "history":   [{ "timestamp", "event": "start|finish", "kind": "run|exec", "source", "execId",
                  "title", "shortId", "commandLine", "workdir", "mode", "instanceId", "pid",
                  "status", "exitCode", "durationMs", "logPath" }]
}
```

- `history` 由 `History.Read(60, null)` 读取后反转，**最新在前**，最多 60 条。
- 日志文件已不存在的历史记录，`logPath` 会被置空，页面不显示日志按钮。
- `instances` 是 qchost 当前跟踪的实例；`commands[].state=running` 由 short-id 匹配实例得出。

## 4. 页面结构（栈模型）

三层栈，靠 `body[data-route]`、`body[data-sheet]`、`body[data-w]`、`body[data-rail]`、`body[data-compact]` 控制：

1. Launcher（默认）：项目栏 + 图标网格 + 「全部」屏顶部搜索框 + 底部屏指示器。
2. 命令页：命令头（返回、图标、标题、状态；操作：打开/置前/启动/停止/属性/固定）、执行记录表、底部提示条。
3. 日志/属性面板：日志或属性两个 tab；宽屏常驻右栏，中屏覆盖右半，窄屏整屏覆盖。

DOM 关键节点：

- `#work` 布局容器（尺寸观察目标）
- `#left` > `#side`（项目） + `#launcher-wrap`（`#lsearch`/`#lq` + `#launcher` + `#dots`）
- `#right` > `#rightempty`（未选中占位） + `#app` + `#pane`
- `#hist` 执行历史浮层

## 5. 数据映射与交互状态

- 页面状态：`project`（屏名：`全部` / 项目名 / `未分组`）、`launcherQuery`、`route`（`home|app`）、`currentId`（命令 short-id）、`sheet`（`null|log|props`）、`selKey`、`runFilter`（`all|live|ok|fail`）。
- 执行记录 `runsOf(c)`：
  - 现运行实例 → 运行中行（含进程数、`data-since` 每秒刷新时长）；
  - `history` 中 `event=finish` 且 short-id 匹配的记录 → 已结束行（启动时间 = 完成时间 − 时长）。
- 行 key：`(live?"L:":"H:") + 实例ID + "@" + 启动时间`。实例 ID 跨宿主重启会重复，因此不能只用 ID；key 变了就回退到第一条（通常是刚结束的那条）。
- 失败判定：退出码非 0，或状态为 `failed|max|host-unavailable`。
- 日志：`/api/log` 取尾部 300 行；运行中每 3 秒轮询（页面数据轮询周期）且缓存新鲜度 1500ms；已结束的日志只取一次。面板接近底部时自动跟到底，向上滚动时不打断。
- 深链：`#<项目>/<short-id>`，项目与 short-id 分别 URL 编码；刷新可回到同一命令页。
- 快捷键：`Esc` 逐层回退（浮层 → 面板 → 命令页 → 首页）；首页 `←/→`、`PageUp/PageDown` 切项目屏；`Ctrl+K` 回首页并聚焦搜索。

## 6. 响应式断点

`applyWidth()` 以「右半区还剩多少」分档，而不是整窗宽度：

- `rail = w >= 1500`：显示项目栏（216px）；否则隐藏，用底部屏指示器切换项目。
- Launcher 宽度：`clamp(300px, 29% 视口, 462px)`。
- `rightW = w - 项目栏 - Launcher`：
  - `>= 820` → `wide`：记录与日志并排（日志栏固定 340px）；
  - `>= 600` → `mid`：日志覆盖右半；
  - 其他 → `narrow`：只显示栈顶，整屏覆盖。
- `appW = rightW - (wide ? 340 : 0) < 560` → `compact=1`，记录表把「时长」并入「启动」列。
- 从窄屏拉宽且当前在命令页时，自动打开日志面板；从未执行过的命令不自动开空面板。

## 7. 安全边界

- 服务只绑定 loopback；写操作需要宿主启动时生成的随机 token，token 只注入真实页面。
- 预览工具（`tools/preview-web-console.js`）不代理写操作，因此预览页的启动/停止/固定会失败，这是预期行为。

## 8. 当前缺失 / 已知限制

- 日志面板宽度固定 340px，不可拖拽；长行折行严重。见 `issues/INDEX.md` `[ui] log-pane-width`。
- 执行历史只取最近 60 条，无分页；更早记录只能 `quickcmd history` 查。
- 日志固定尾部 300 行，无全文下载或更大的查看窗口。
- 预览工具不能验证写操作（token 限制）。
- `traycmd/dist` 已于 2026-11-19 12:43 重新发布并重启 qcApp/qchost；后续发布流程见 `workflows/wf-build-and-run.md`。
