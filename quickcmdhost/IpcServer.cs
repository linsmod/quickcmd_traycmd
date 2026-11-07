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
                {
                    var list = Store.Load(out _);
                    var c = Resolver.Resolve(msg.Key, list, out var err);
                    if (c == null)
                    {
                        AppendHistory(new HistoryEntry
                        {
                            Event = "start", Kind = "run", Source = SourceOf(msg),
                            Title = msg.Key ?? "", Status = "failed", Error = err,
                            RequesterPid = msg.RequesterPid,
                        });
                        return Err(err);
                    }

                    bool useJob = !string.Equals(msg.Mode, "pid", StringComparison.OrdinalIgnoreCase);
                    var entry = NewEntry(msg, "run", c);
                    if (Manager.Start(c, useJob, msg.RequesterPid, out var serr, out var spid, out var smode, out var sid))
                    {
                        entry.Status = "started";
                        entry.Mode = smode;
                        entry.InstanceId = sid;
                        entry.Pid = spid;
                        AppendHistory(entry);
                        return Ok("[" + smode + "] 已启动 " + sid + (smode == "JOB" ? "" : " pid=" + spid), entry);
                    }
                    if (serr == "max")
                    {
                        entry.Status = "max";
                        AppendHistory(entry);
                        return Ok("已达实例上限，已置前现有实例");
                    }
                    entry.Status = "failed";
                    entry.Error = serr;
                    AppendHistory(entry);
                    return Err(serr);
                }

                case "exec":
                {
                    var c = Launcher.BuildAdHoc(msg, out var err);
                    if (c == null)
                    {
                        AppendHistory(new HistoryEntry
                        {
                            Event = "start", Kind = "exec", Source = SourceOf(msg),
                            Title = msg.Title ?? "", CommandLine = msg.CommandLine ?? "",
                            Status = "failed", Error = err, RequesterPid = msg.RequesterPid,
                        });
                        return Err(err);
                    }

                    bool useJob = !string.Equals(msg.Mode, "pid", StringComparison.OrdinalIgnoreCase);
                    var entry = NewEntry(msg, "exec", c);
                    if (Manager.Start(c, useJob, msg.RequesterPid, out var serr, out var spid, out var smode, out var sid))
                    {
                        entry.Status = "started";
                        entry.Mode = smode;
                        entry.InstanceId = sid;
                        entry.Pid = spid;
                        AppendHistory(entry);
                        return Ok("[" + smode + "] 已启动 " + sid + "（免注册直跑）", entry);
                    }
                    entry.Status = "failed";
                    entry.Error = serr;
                    AppendHistory(entry);
                    return Err(serr);
                }

                case "ps":
                    var snap = Manager.Snapshot();
                    return JsonSerializer.Serialize(new QchostResponse
                    {
                        Ok = true,
                        Message = snap.Count + " 个实例",
                        Data = JsonSerializer.Serialize(snap, Store.JsonOpts),
                    }, Store.JsonOpts);

                case "kill":
                    var target = Manager.FindInstance(msg.Key);
                    if (target == null) return Err("未找到实例 " + msg.Key + "（应为 ps 里的 ID：job-<数字> 或 pid，也可传树内任意 pid）");
                    var kmode = Manager.ModeOf(target);
                    var ktitle = target.Cmd.Title ?? "";
                    Manager.Kill(target);
                    return Ok("[" + kmode + "] 已结束「" + ktitle + "」 " + target.Id +
                              (kmode == "JOB" ? "（整棵作业树）" : "（pid 进程树）"));

                case "front":
                    var inst = Manager.FindInstance(msg.Key);
                    if (inst == null) return Err("未找到实例 " + msg.Key);
                    Manager.BringFront(inst);
                    return Ok("[" + Manager.ModeOf(inst) + "] 已置前「" + (inst.Cmd.Title ?? "") + "」 " + inst.Id);

                case "shutdown":
                    shutdown = true;
                    return Ok("bye");

                default:
                    return Err("unknown op: " + msg.Op);
            }
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private static string Ok(string m, HistoryEntry entry = null) =>
        JsonSerializer.Serialize(new QchostResponse
        {
            Ok = true,
            Message = m,
            Data = entry == null ? null : JsonSerializer.Serialize(entry, Store.JsonOpts),
        }, Store.JsonOpts);

    private static string Err(string m) =>
        JsonSerializer.Serialize(new QchostResponse { Ok = false, Message = m }, Store.JsonOpts);

    // ----- 审计（history.jsonl）辅助 -----

    private static string SourceOf(IpcMessage msg) =>
        string.IsNullOrWhiteSpace(msg.Source) ? "cli" : msg.Source.Trim();

    private static HistoryEntry NewEntry(IpcMessage msg, string kind, CommandDef c) => new()
    {
        Event = "start",
        Kind = kind,
        Source = SourceOf(msg),
        ExecId = c.Id,
        Title = c.Title,
        ShortId = c.ShortId,
        CommandLine = History.CommandLineOf(c),
        Workdir = Launcher.EffectiveWorkdir(c),
        RequesterPid = msg.RequesterPid,
    };

    private static void AppendHistory(HistoryEntry e) => History.Append(e, out _);
}
