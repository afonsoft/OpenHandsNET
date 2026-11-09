using System.Text.Json;

using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Infrastructure.Agent.Tools;

/// <summary>
/// SPEC-121: background-job tools dentro do sandbox (port do trio
/// OWUIC <c>job_list/job_output/job_kill</c> + <c>job_run</c> para
/// criar). Estado em <c>$HOME/.local/state/ohn/jobs/{id}.{pid,log,cmd,exit}</c> —
/// nada roda no host, tudo via <see
/// cref="Application.Sandbox.ISandboxExecutor.ExecAsync"/>.
/// </summary>
public static class BuiltinJobs
{
    private const string Dir = "/home/openhands/.local/state/ohn/jobs";
    private const int TailBytes = 16 * 1024;

    /// <summary>Inicia um comando em background (nohup) no workspace.</summary>
    public static AgentTool JobRun() => new(
        new ToolSchema("job_run",
            "Start a background command inside the workspace (nohup). "
            + "Returns job_id — inspect with job_output/job_list, "
            + "stop with job_kill.",
            Schema("""
                {"type":"object","properties":{
                  "command":{"type":"string","description":"Command to run in background"},
                  "name":{"type":"string","description":"Optional short label"}},
                 "required":["command"]}
                """)),
        Risky: true,
        Handler: async (ctx, args, ct) =>
        {
            var command = args.GetProperty("command").GetString()!;
            var name = Str(args, "name");
            var id = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}-{Guid.NewGuid():N}"[..20];
            var cmd = $"mkdir -p -m 700 {Dir} && cd /workspace/repo && "
                + $"echo {Sh(command)} > {Dir}/{id}.cmd && "
                + (name is not null ? $"echo {Sh(name)} > {Dir}/{id}.name && " : "")
                + $"nohup bash -c {Sh(command)} > {Dir}/{id}.log 2>&1 & "
                + $"echo $! > {Dir}/{id}.pid && echo {id}";
            var r = await ctx.Executor.ExecAsync(
                ctx.Sandbox, cmd, ctx.DefaultTimeout, ct);
            return r.ExitCode == 0
                ? ToolResult.Ok(new { job_id = id, pid_file = $"{Dir}/{id}.pid" })
                : ToolResult.Error($"job_run failed (exit {r.ExitCode}): {r.StdErr.Trim()}");
        });

    /// <summary>Lista jobs (running/done) do sandbox.</summary>
    public static AgentTool JobList() => new(
        new ToolSchema("job_list",
            "List background jobs (id, name, running/exited, exit code).",
            Schema("""
                {"type":"object","properties":{},"required":[]}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            var cmd = $"for p in {Dir}/*.pid; do "
                + "[ -e \"$p\" ] || continue; "
                + "id=$(basename \"$p\" .pid); pid=$(cat \"$p\"); "
                + "if kill -0 \"$pid\" 2>/dev/null; then st=running; rc=-; "
                + "elif [ -f \"" + Dir + "/$id.exit\" ]; then st=done; rc=$(cat " + Dir + "/$id.exit); "
                + "else st=done; rc=unknown; fi; "
                + "nm=$(cat " + Dir + "/$id.name 2>/dev/null || echo -); "
                + "echo \"$id|$st|$rc|$nm|$pid\"; done";
            var r = await ctx.Executor.ExecAsync(
                ctx.Sandbox, cmd, ctx.DefaultTimeout, ct);
            var jobs = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split('|'))
                .Where(p => p.Length >= 5)
                .Select(p => new
                {
                    job_id = p[0],
                    status = p[1],
                    exit_code = p[2],
                    name = p[3],
                    pid = p[4],
                }).ToList();
            return ToolResult.Ok(new { jobs });
        });

    /// <summary>Tail do log de um job.</summary>
    public static AgentTool JobOutput() => new(
        new ToolSchema("job_output",
            "Read the tail of a background job's output log.",
            Schema("""
                {"type":"object","properties":{
                  "job_id":{"type":"string","description":"Job id from job_run/job_list"},
                  "tail_lines":{"type":"integer","description":"Last N lines (default 200)"}},
                 "required":["job_id"]}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            var id = args.GetProperty("job_id").GetString()!;
            if (id.Any(c => !char.IsLetterOrDigit(c) && c is not '-' and not '_'))
            {
                return ToolResult.Error("job_id inválido");
            }

            var tail = args.TryGetProperty("tail_lines", out var t)
                ? Math.Clamp(t.GetInt32(), 1, 2000) : 200;
            var r = await ctx.Executor.ExecAsync(ctx.Sandbox,
                $"tail -n {tail} {Dir}/{id}.log 2>/dev/null || "
                + $"echo '[no log]'; test -f {Dir}/{id}.pid && "
                + "(kill -0 $(cat " + Dir + $"/{id}.pid) 2>/dev/null "
                + "&& echo __RUNNING__ || echo __DONE__)",
                ctx.DefaultTimeout, ct);
            var running = r.StdOut.Contains("__RUNNING__");
            var log = r.StdOut.Replace("__RUNNING__", "")
                .Replace("__DONE__", "").TrimEnd();
            if (log.Length > TailBytes)
            {
                log = "…[truncated]\n" + log[^TailBytes..];
            }
            return ToolResult.Ok(new { job_id = id, running, output = log });
        });

    /// <summary>Mata um job em execução.</summary>
    public static AgentTool JobKill() => new(
        new ToolSchema("job_kill",
            "Kill a running background job (SIGTERM, then SIGKILL).",
            Schema("""
                {"type":"object","properties":{
                  "job_id":{"type":"string","description":"Job id from job_run/job_list"}},
                 "required":["job_id"]}
                """)),
        Risky: true,
        Handler: async (ctx, args, ct) =>
        {
            var id = args.GetProperty("job_id").GetString()!;
            if (id.Any(c => !char.IsLetterOrDigit(c) && c is not '-' and not '_'))
            {
                return ToolResult.Error("job_id inválido");
            }

            var r = await ctx.Executor.ExecAsync(ctx.Sandbox,
                $"pid=$(cat {Dir}/{id}.pid 2>/dev/null) && "
                + $"{{ kill \"$pid\" 2>/dev/null; sleep 1; "
                + $"kill -0 \"$pid\" 2>/dev/null && kill -9 \"$pid\" 2>/dev/null; "
                + $"echo killed; }} || echo not-found",
                ctx.DefaultTimeout, ct);
            return r.StdOut.Contains("killed")
                ? ToolResult.Ok(new { job_id = id, killed = true })
                : ToolResult.Error($"job '{id}' not found or already exited");
        });

    private static string? Str(JsonElement a, string n) =>
        a.TryGetProperty(n, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string Sh(string v) =>
        "'" + v.Replace("'", "'\\''") + "'";

    private static JsonElement Schema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}