// ============================================================================
// quickcmd-host — IpcServer.cs: 命名管道 IPC 服务端（纯 headless）
//   支持 op: ping | run | ps | kill | front | shutdown
// ============================================================================

using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace QuickCmd;

internal static class IpcServer
{
    // 后台线程循环：每次接一条请求 → 处理 → 写回一行响应
    public static void ServeLoop()
    {
        while (true)
        {
            NamedPipeServerStream server = null;
            try
            {
                server = new NamedPipeServerStream(Ipc.PipeName, PipeDirection.InOut, 1);
                server.WaitForConnection();

                string line;
                using (var r = new StreamReader(server, new UTF8Encoding(false), false, 1024, true))
                    line = r.ReadLine() ?? "";

                string resp = Handle(line, out bool shutdown);
                using (var w = new StreamWriter(server, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                    w.WriteLine(resp);

                if (shutdown) { try { server.Disconnect(); } catch { } break; }
            }
            catch { }
            finally { try { if (server != null) server.Dispose(); } catch { } }
        }
        Program.RequestExit();
    }

    private static string Handle(string line, out bool shutdown)
    {
        shutdown = false;
        try
        {
            var msg = JsonSerializer.Deserialize<IpcMessage>(line ?? "", Store.JsonOpts);
            if (msg == null) return Err("empty message");
            switch ((msg.Op ?? "").ToLowerInvariant())
            {
                case "ping":
                    return Ok("qchost");

                case "run":
                    var list = Store.Load(out _);
                    var c = Resolver.Resolve(msg.Key, list, out var err);
                    if (c == null) return Err(err);
                    bool useJob = !string.Equals(msg.Mode, "pid", StringComparison.OrdinalIgnoreCase);
                    if (Manager.Start(c, useJob, out var serr, out var spid, out var smode))
                        return Ok("[" + smode + "] 已启动 pid=" + spid);
                    if (serr == "max")
                        return Ok("已达实例上限，已置前现有实例");
                    return Err(serr);

                case "ps":
                    var snap = Manager.Snapshot();
                    return JsonSerializer.Serialize(new QchostResponse
                    {
                        Ok = true,
                        Message = snap.Count + " 个实例",
                        Data = JsonSerializer.Serialize(snap, Store.JsonOpts),
                    }, Store.JsonOpts);

                case "kill":
                    if (!int.TryParse((msg.Key ?? "").Trim(), out var kpid))
                        return Err("kill 目标必须是 ps 里的实例 ID（pid），实际: " + msg.Key);
                    var target = Manager.FindInstance(kpid);
                    if (target == null) return Err("未找到实例 pid " + kpid + "（不在 qchost 跟踪的实例中）");
                    var kmode = Manager.ModeOf(target);
                    var kid = target.Pid;
                    var ktitle = target.Cmd.Title ?? "";
                    Manager.Kill(target);
                    var via = kpid != kid ? "pid " + kpid + " 属于实例 " + kid + "，" : "";
                    return Ok("[" + kmode + "] " + via + "已结束「" + ktitle + "」" +
                              (kmode == "JOB" ? "（整棵作业树）" : "（pid 进程树）"));

                case "front":
                    if (!int.TryParse((msg.Key ?? "").Trim(), out var fpid))
                        return Err("front 目标必须是实例 ID（pid）");
                    var inst = Manager.FindInstance(fpid);
                    if (inst == null) return Err("未找到实例 pid " + fpid);
                    Manager.BringFront(inst);
                    return Ok("[" + Manager.ModeOf(inst) + "] 已置前「" + (inst.Cmd.Title ?? "") + "」 pid=" + inst.Pid);

                case "shutdown":
                    shutdown = true;
                    return Ok("bye");

                default:
                    return Err("unknown op: " + msg.Op);
            }
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private static string Ok(string m) =>
        JsonSerializer.Serialize(new QchostResponse { Ok = true, Message = m }, Store.JsonOpts);

    private static string Err(string m) =>
        JsonSerializer.Serialize(new QchostResponse { Ok = false, Message = m }, Store.JsonOpts);
}
