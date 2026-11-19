# 工具索引

最后核对：2026-11-19

| 工具 / 脚本 | 位置 | 用途 | 备注 |
|---|---|---|---|
| Web 控制台预览 | [preview-web-console.js](preview-web-console.js) | 从 `WebConsole.cs` 提取内嵌页面，只读代理真实宿主的 `/api/state`、`/api/log`，不重启宿主 | 已用 41777 宿主验证；写操作返回 501 |
| 发布脚本 | [../traycmd/make_dist.ps1](../traycmd/make_dist.ps1) | 清理 bin、构建解决方案、把各 exe 项目产物平铺到 `traycmd\dist` | 会删除并重建 dist；运行中的 qchost/qcApp 会锁文件 |
| 测试夹具 | [../traycmd/testdata](../traycmd/testdata) | JOB 模式测试：`hi.bat`、`probe-jobid.ps1`、`spawn-and-exit.bat`（启动 notepad 后 root 退出，验证作业树跟踪） | 仅测试用 |
| CLI | `traycmd\dist\quickcmd.exe` | `add/list/update/delete`、`run/run-job/exec`、`ps/kill`、`logs`、`history`、`host status\|stop\|restart`、`ui [--no-open]`、`serve` | 宿主不在时 `run` 会自动拉起 |
| DSH 会话回滚重启 | `dsh_rewind_restart.ps1`（工作区文件，当前未入库） | 重启 DSH 并把会话回滚到上次压缩前（用户脚本） | 与 quickcmd 功能无关，勿在常规验证中运行 |

## 相关文档

- Web 控制台架构：`docs/arch-web-console.md`
- 预览/调试：`docs/debug-web-console.md`
- 发布流程：`workflows/wf-build-and-run.md`
