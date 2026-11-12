using System.Diagnostics;
using System.Text.Json;

namespace Sandcastle.SandboxAgent;

/// <summary>
/// Endpoint handlers extraídos do top-level do Program.cs (SPEC-107) —
/// mantém lambdas de mapeamento finas e a complexidade fora do arquivo
/// de boot.
/// </summary>
public sealed class AgentHandlers(AgentState runtime, SurfaceRegistry surfaces)
{
    private const string StatusReady = "ready";
    private const string StatusDormant = "dormant";
    private const string StatusUnauthorized = "unauthorized";

    private static readonly JsonSerializerOptions ExecStreamJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public bool Authorized(HttpContext http) =>
        runtime.Status == StatusReady &&
        runtime.SessionKey is not null &&
        http.Request.Headers["X-Session-Key"] == runtime.SessionKey;

    public bool Ready() => runtime.Status == StatusReady;

    public static IResult Dormant() =>
        Results.Json(new { error = StatusDormant }, statusCode: 503);

    public static IResult UnauthorizedResult() =>
        Results.Json(new { error = StatusUnauthorized }, statusCode: 401);

    /// <summary>POST /api/init — deferred init (paridade upstream).</summary>
    public IResult HandleInit(HttpContext http, InitRequest req)
    {
        if (runtime.Status == StatusReady)
        {
            return Results.Conflict(new { error = "already_initialized" });
        }

        if (runtime.Deferred)
        {
            var initKey = http.Request.Headers["X-Init-API-Key"].ToString();
            if (runtime.InitKey is null || initKey != runtime.InitKey)
            {
                return Results.Json(
                    new { error = "invalid_init_key" }, statusCode: 401);
            }
        }

        if (string.IsNullOrEmpty(req.SessionKey))
        {
            return Results.BadRequest(new { error = "session_key_required" });
        }

        runtime.Status = "initializing";
        try
        {
            runtime.SessionKey = req.SessionKey;
            if (req.Workspace is { Length: > 0 })
            {
                runtime.WorkspaceRoot = req.Workspace;
            }

            if (req.Env is not null)
            {
                runtime.SessionEnv = new Dictionary<string, string>(req.Env);
                runtime.WriteProfileEnv();
            }

            Directory.CreateDirectory(runtime.WorkspaceRoot);
            runtime.Status = StatusReady;
            // SPEC-40 RF-002/003: surfaces opt-in via env do blueprint/init.
            surfaces.StartEnabled(req.Env);

            return Results.Ok(new { state = runtime.Status });
        }
        catch (Exception ex)
        {
            runtime.Status = StatusDormant;
            return Results.Json(new { error = ex.Message }, statusCode: 500);
        }
    }

    /// <summary>POST /exec — exec agregado (compat).</summary>
    public async Task<IResult> HandleExec(
        HttpContext http, ExecRequest req, CancellationToken ct)
    {
        if (runtime.Status != StatusReady)
        {
            return Results.Json(new { error = StatusDormant }, statusCode: 503);
        }

        if (!Authorized(http))
        {
            return Results.Json(new { error = StatusUnauthorized }, statusCode: 401);
        }

        var timeout = TimeSpan.FromSeconds(
            Math.Clamp(req.TimeoutSeconds ?? 120, 1, 3600));
        runtime.Record("exec_start", ProgramTrunc(req.Command));
        CommandResult result;
        if (req.SessionId is { Length: > 0 } sid)
        {
            var session = runtime.Sessions.GetOrAdd(
                sid, _ => new BashSession(runtime.WorkspaceRoot, runtime.SessionEnv));
            session.LastCommand = ProgramTrunc(req.Command);
            result = await session.RunAsync(req.Command, timeout, ct);
        }
        else
        {
            result = await ExecOnce.RunAsync(req.Command,
                req.Workdir ?? runtime.WorkspaceRoot, runtime.SessionEnv,
                timeout, ct);
        }

        runtime.Record("exec_end",
            $"exit={result.ExitCode} timeout={result.TimedOut}");
        return Results.Ok(result);
    }

    /// <summary>POST /exec/stream — SSE com chunks stdout/stderr.</summary>
    public async Task<IResult> HandleExecStream(
        HttpContext http, ExecRequest req, CancellationToken ct)
    {
        if (runtime.Status != StatusReady)
        {
            return Results.Json(new { error = StatusDormant }, statusCode: 503);
        }

        if (!Authorized(http))
        {
            return Results.Json(new { error = StatusUnauthorized }, statusCode: 401);
        }

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        var timeout = TimeSpan.FromSeconds(
            Math.Clamp(req.TimeoutSeconds ?? 120, 1, 3600));
        runtime.Record("exec_start", ProgramTrunc(req.Command));
        var sw = Stopwatch.StartNew();

        // As pumps de stdout e stderr rodam em tasks paralelas e ambas
        // escrevem no response — WriteAsync concorrente no mesmo stream
        // falha/intercala bytes e o catch descartava o chunk (race real
        // de produto). O gate serializa chunks + frame de exit.
        var sseGate = new object();

        void OnChunk(string channel, string text)
        {
            runtime.Record("output", $"{channel}:{ProgramTrunc(text, 120)}");
            WriteSseChunk(http, sseGate, ct, channel, text);
        }

        CommandResult result;
        var truncated = false;
        if (req.SessionId is { Length: > 0 } sid)
        {
            var session = runtime.Sessions.GetOrAdd(
                sid, _ => new BashSession(runtime.WorkspaceRoot, runtime.SessionEnv));
            session.LastCommand = ProgramTrunc(req.Command);
            result = await session.RunAsync(req.Command, timeout, ct, OnChunk);
        }
        else
        {
            var streamed = await ExecOnce.RunStreamingAsync(req.Command,
                req.Workdir ?? runtime.WorkspaceRoot, runtime.SessionEnv,
                timeout, ct, OnChunk);
            result = streamed.Result;
            truncated = streamed.Truncated;
        }

        runtime.Record("exec_end",
            $"exit={result.ExitCode} timeout={result.TimedOut}");
        // Exit frame também sob o gate — o tap de sessão pode emitir um
        // chunk straggler depois que RunAsync retorna.
        lock (sseGate)
        {
            http.Response.WriteAsync("data: " + JsonSerializer.Serialize(
                new
                {
                    type = "exit",
                    exit_code = result.ExitCode,
                    duration_ms = sw.ElapsedMilliseconds,
                    timed_out = result.TimedOut,
                    truncated,
                    std_out = result.StdOut,
                    std_err = result.StdErr,
                }, ExecStreamJson) + "\n\n", ct)
                .GetAwaiter().GetResult();
            http.Response.Body.FlushAsync(ct).GetAwaiter().GetResult();
        }
        return Results.Empty;
    }

    private static void WriteSseChunk(
        HttpContext http, object gate, CancellationToken ct,
        string channel, string text)
    {
        try
        {
            // Callback síncrono vindo da pump — se o cliente desconectar,
            // a escrita falha e o chunk é simplesmente descartado.
            lock (gate)
            {
                http.Response.WriteAsync("data: " + JsonSerializer.Serialize(
                    new { channel, text }, ExecStreamJson) + "\n\n", ct)
                    .GetAwaiter().GetResult();
                http.Response.Body.FlushAsync(ct).GetAwaiter().GetResult();
            }
        }
        catch
        {
            // Cliente SSE desconectou — o processo continua; o /activity
            // ring ainda registra a saída para replay.
        }
    }

    /// <summary>GET /files/{**path} — list/read confinado ao workspace.</summary>
    public IResult HandleFilesGet(string? path, HttpContext http)
    {
        if (!Ready()) return Dormant();
        if (!Authorized(http)) return UnauthorizedResult();
        var full = runtime.Resolve(path ?? "");
        if (full is null) return Results.BadRequest(new { error = "outside_workspace" });
        if (Directory.Exists(full))
        {
            var items = Directory.EnumerateFileSystemEntries(full).Select(e =>
            {
                var i = new FileInfo(e);
                return new
                {
                    path = "/" + Path.GetRelativePath(runtime.WorkspaceRoot, e)
                        .Replace('\\', '/'),
                    is_directory = (i.Attributes & FileAttributes.Directory) != 0,
                    size = (i.Attributes & FileAttributes.Directory) != 0
                        ? 0 : i.Length,
                };
            });
            return Results.Ok(items);
        }

        return File.Exists(full)
            ? Results.Text(File.ReadAllText(full))
            : Results.NotFound(new { error = "not_found" });
    }

    /// <summary>PUT /files/{**path} — write confinado ao workspace.</summary>
    public async Task<IResult> HandleFilesPut(string path, HttpContext http)
    {
        if (!Ready()) return Dormant();
        if (!Authorized(http)) return UnauthorizedResult();
        var full = runtime.Resolve(path);
        if (full is null) return Results.BadRequest(new { error = "outside_workspace" });
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var reader = new StreamReader(http.Request.Body);
        await File.WriteAllTextAsync(full,
            await reader.ReadToEndAsync(http.RequestAborted),
            http.RequestAborted);
        runtime.Record("file_write", path);
        return Results.NoContent();
    }

    /// <summary>GET /alive — heartbeat v2 (SPEC-24 RF-006).</summary>
    public IResult HandleAlive() => Results.Ok(new
    {
        status = "ok",
        state = runtime.Status,
        agent_version = AgentState.AgentVersion,
        uptime_s = (long)(DateTimeOffset.UtcNow - runtime.StartedAt)
            .TotalSeconds,
        workspace = runtime.WorkspaceRoot,
        sessions = runtime.Sessions.Count,
    });

    /// <summary>GET /api/init — estado do init (deferred-init parity).</summary>
    public IResult HandleInitStatus() => Results.Ok(new
    {
        state = runtime.Status,
        workspace = runtime.WorkspaceRoot,
    });

    /// <summary>GET /exec/sessions — sessões bash vivas (SPEC-24 RF-001).</summary>
    public IResult HandleSessions(HttpContext http)
    {
        if (!Authorized(http))
        {
            return UnauthorizedResult();
        }

        return Results.Ok(runtime.Sessions.Select(kv => new
        {
            id = kv.Key,
            started_at = kv.Value.StartedAt,
            last_command = kv.Value.LastCommand,
        }));
    }

    /// <summary>GET /proc — processos do sandbox via /proc.</summary>
    public IResult HandleProc(HttpContext http) =>
        Authorized(http)
            ? Results.Ok(ProbeProc())
            : UnauthorizedResult();

    /// <summary>GET /activity — ring de atividade; Last-Event-ID → replay.</summary>
    public IResult HandleActivity(HttpContext http)
    {
        if (!Authorized(http))
        {
            return UnauthorizedResult();
        }

        var after = long.TryParse(http.Request.Headers["Last-Event-ID"],
            out var seq) ? seq : 0;
        return Results.Ok(runtime.Activity
            .Where(a => a.Seq > after).ToList());
    }

    /// <summary>DELETE /exec/sessions/{id} — encerra sessão bash.</summary>
    public IResult HandleDeleteSession(string id, HttpContext http)
    {
        if (!Authorized(http))
        {
            return UnauthorizedResult();
        }

        if (runtime.Sessions.TryRemove(id, out var s))
        {
            s.Dispose();
        }

        return Results.NoContent();
    }

    /// <summary>GET /surfaces — surfaces web ativas (SPEC-40 RF-002/003).</summary>
    public IResult HandleSurfaces(HttpContext http)
    {
        if (!Authorized(http))
        {
            return UnauthorizedResult();
        }

        return Results.Ok(surfaces.Active().Select(s => new
        {
            name = s.Name,
            port = s.Port,
            protocol = s.Protocol,
        }).ToList());
    }

    private static List<object> ProbeProc()
    {
        var list = new List<object>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name, out var pid))
            {
                continue;
            }

            try
            {
                var cmd = File.ReadAllText(Path.Combine(dir, "cmdline"))
                    .Replace('\0', ' ').Trim();
                if (cmd.Length == 0)
                {
                    continue; // kernel thread
                }

                list.Add(new { pid, cmd = ProgramTrunc(cmd, 200), rss_kb = RssKb(dir) });
            }
            catch
            {
                // Processo sumiu entre o enumerate e o read — best-effort.
            }
        }

        return list;
    }

    private static long RssKb(string dir) =>
        File.ReadLines(Path.Combine(dir, "status"))
            .Where(l => l.StartsWith("VmRSS:", StringComparison.Ordinal))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2)
            .Select(parts => long.TryParse(parts[1], out var kb) ? kb : 0)
            .FirstOrDefault();

    public static string ProgramTrunc(string s, int max = 200) =>
        s.Length <= max ? s : s[..max] + "…";
}