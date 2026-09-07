// ============================================================================
// quickcmd.shared — Ipc.cs: 客户端 ↔ qchost 的命名管道协议与拉起逻辑
// ============================================================================

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace QuickCmd;

// ---------------------------------------------------------------------------
// IPC 常量与客户端：所有进程（CLI / quickcmd-app / qchost 服务端）共用同一
// 管道名与消息格式；拉起宿主时也只认这一个 exe 名。
// ---------------------------------------------------------------------------
internal static class Ipc
{
    public const string PipeName = "quickcmd.ipc";
    public const string HostExeName = "qchost.exe";

    // 向 qchost 发一条 IPC 请求（无副作用、不打印）。
    // 返回：0=拿到响应 1=已连接但中断/无响应（failReason 给出原因） 2=qchost 不在（未连上）
    public static int TryCall(IpcMessage msg, int connectTimeoutMs, out QchostResponse resp, out string failReason)
    {
        resp = null;
        failReason = null;
        bool connected = false;
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(connectTimeoutMs);
            connected = true;

            var req = JsonSerializer.Serialize(msg, Store.JsonOpts);
            var bytes = Encoding.UTF8.GetBytes(req + "\n");
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();

            using var r = new StreamReader(pipe, Encoding.UTF8);
            var line = r.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) { failReason = "qchost 无响应"; return 1; }
            resp = JsonSerializer.Deserialize<QchostResponse>(line, Store.JsonOpts);
            return 0;
        }
        catch
        {
            if (connected) { failReason = "qchost 连接中断"; return 1; }
            return 2;
        }
    }

    // 确保 qchost 在线：ping 不通就拉起并快速轮询等待就绪（pipe 在 qchost 启动早期即建立）。
    // 正常情况毫秒级完成；maxWaitMs 内仍不在线返回 false。
    public static bool EnsureHost(int connectTimeoutMs = 500, int maxWaitMs = 3000)
    {
        if (TryCall(new IpcMessage { Op = "ping" }, connectTimeoutMs, out _, out _) == 0) return true;
        if (!StartHost()) return false;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxWaitMs)
        {
            Thread.Sleep(50);
            if (TryCall(new IpcMessage { Op = "ping" }, connectTimeoutMs, out _, out _) == 0) return true;
        }
        return false;
    }

    // 拉起 qchost（与当前进程同目录优先，其次交给系统搜索）。返回 false = 找不到 exe
    public static bool StartHost()
    {
        try
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath);
            var exe = dir != null ? Path.Combine(dir, HostExeName) : null;
            var psi = exe != null && File.Exists(exe)
                ? new ProcessStartInfo(exe) { WorkingDirectory = dir, UseShellExecute = true }
                : new ProcessStartInfo(HostExeName) { UseShellExecute = true };
            Process.Start(psi);
            return true;
        }
        catch { return false; }
    }
}
