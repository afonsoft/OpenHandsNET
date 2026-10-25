using System.Diagnostics;
using System.Text.Json;

namespace OpenHandsNET.SandboxAgent;

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

        void OnChunk(string channel, string text)
        {
            runtime.Record("output", $"{channel}:{ProgramTrunc(text, 120)}");
            WriteSseChunk(http, ct, channel, text);
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
        await http.Response.WriteAsync("data: " + JsonSerializer.Serialize(
            new
            {
                type = "exit",
                exit_code = result.ExitCode,
                duration_ms = sw.ElapsedMilliseconds,
                timed_out = result.TimedOut,
                truncated,
                std_out = result.StdOut,
                std_err = result.StdErr,
            }, ExecStreamJson) + "\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
        return Results.Empty;
    }

    private static void WriteSseChunk(
        HttpContext http, CancellationToken ct, string channel, string text)
    {
        try
        {
            // Callback síncrono vindo da pump — se o cliente desconectar,
            // a escrita falha e o chunk é simplesmente descartado.
            http.Response.WriteAsync("data: " + JsonSerializer.Serialize(
                new { channel, text }, ExecStreamJson) + "\n\n", ct)
                .GetAwaiter().GetResult();
            http.Response.Body.FlushAsync(ct).GetAwaiter().GetResult();
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

    public static string ProgramTrunc(string s, int max = 200) =>
        s.Length <= max ? s : s[..max] + "…";
}