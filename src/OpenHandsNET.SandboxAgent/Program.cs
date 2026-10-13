using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

// SPEC-17 RF-002: openhandsnet-sandbox-agent — executor HTTP fino que roda
// como PID 1 do sandbox (sob tini). Estados: dormant → initializing → ready.
// Auth: X-Session-Key (pronto) / X-Init-API-Key (bootstrap do init).

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls(
    builder.Configuration["OHN_AGENT_URL"] ?? "http://0.0.0.0:8000");
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

var runtime = LoadState(builder.Configuration);
var app = builder.Build();

AgentState LoadState(IConfiguration cfg)
{
    var deferred = string.Equals(
        cfg["OHN_DEFERRED_INIT"], "true", StringComparison.OrdinalIgnoreCase);
    var s = new AgentState(
        WorkspaceRoot: cfg["OHN_WORKSPACE"] is { Length: > 0 } w
            ? w : "/workspace",
        InitKey: cfg["OHN_INIT_KEY"],
        Deferred: deferred);
    if (!deferred)
    {
        // Cold start: session key vem no env — sobe direto em ready.
        s.SessionKey = cfg["OHN_SESSION_KEY"];
        s.Ready = s.SessionKey is not null;
    }

    return s;
}

app.MapGet("/alive", () => Results.Ok(new
{
    status = "ok",
    state = runtime.Status,
}));

// ---- deferred-init (paridade upstream /api/init) ----
app.MapGet("/api/init", () => Results.Ok(new
{
    state = runtime.Status,
    workspace = runtime.WorkspaceRoot,
}));

app.MapPost("/api/init", (HttpContext http, InitRequest req) =>
{
    if (runtime.Status == "ready")
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
        runtime.Status = "ready";
        return Results.Ok(new { state = runtime.Status });
    }
    catch (Exception ex)
    {
        runtime.Status = "dormant";
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

// ---- guarded endpoints (ready + X-Session-Key) ----
bool Authorized(HttpContext http) =>
    runtime.Status == "ready" &&
    runtime.SessionKey is not null &&
    http.Request.Headers["X-Session-Key"] == runtime.SessionKey;

app.MapPost("/exec", async (HttpContext http, ExecRequest req, CancellationToken ct) =>
{
    if (runtime.Status != "ready")
    {
        return Results.Json(new { error = "dormant" }, statusCode: 503);
    }

    if (!Authorized(http))
    {
        return Results.Json(new { error = "unauthorized" }, statusCode: 401);
    }

    var timeout = TimeSpan.FromSeconds(
        Math.Clamp(req.TimeoutSeconds ?? 120, 1, 3600));
    CommandResult result;
    if (req.SessionId is { Length: > 0 } sid)
    {
        var session = runtime.Sessions.GetOrAdd(
            sid, _ => new BashSession(runtime.WorkspaceRoot, runtime.SessionEnv));
        result = await session.RunAsync(req.Command, timeout, ct);
    }
    else
    {
        result = await ExecOnce.RunAsync(req.Command,
            req.Workdir ?? runtime.WorkspaceRoot, runtime.SessionEnv,
            timeout, ct);
    }

    return Results.Ok(result);
});

app.MapDelete("/exec/sessions/{id}", (string id, HttpContext http) =>
{
    if (!Authorized(http))
    {
        return Results.Json(new { error = "unauthorized" }, statusCode: 401);
    }

    if (runtime.Sessions.TryRemove(id, out var s))
    {
        s.Dispose();
    }

    return Results.NoContent();
});

// ---- files (confinados ao workspace) ----
app.MapGet("/files/{**path}", (string? path, HttpContext http) =>
{
    if (!Ready(http)) return Dormant(http);
    if (!Authorized(http)) return Unauthorized(http);
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
});

app.MapPut("/files/{**path}", async (string path, HttpContext http) =>
{
    if (!Ready(http)) return Dormant(http);
    if (!Authorized(http)) return Unauthorized(http);
    var full = runtime.Resolve(path);
    if (full is null) return Results.BadRequest(new { error = "outside_workspace" });
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    using var reader = new StreamReader(http.Request.Body);
    await File.WriteAllTextAsync(full, await reader.ReadToEndAsync());
    return Results.NoContent();
});

bool Ready(HttpContext _) => runtime.Status == "ready";
IResult Dormant(HttpContext _) =>
    Results.Json(new { error = "dormant" }, statusCode: 503);
IResult Unauthorized(HttpContext _) =>
    Results.Json(new { error = "unauthorized" }, statusCode: 401);

app.Run();

/// <summary>Deferred-init state + session key + workspace confinement.</summary>
public sealed class AgentState
{
    public AgentState(string WorkspaceRoot, string? InitKey, bool Deferred)
    {
        this.WorkspaceRoot = WorkspaceRoot;
        this.InitKey = InitKey;
        this.Deferred = Deferred;
        Status = Deferred ? "dormant" : "initializing";
    }

    public string Status { get; set; }
    public string WorkspaceRoot { get; set; }
    public string? InitKey { get; }
    public bool Deferred { get; }
    public string? SessionKey { get; set; }
    public bool Ready { set => Status = value ? "ready" : Status; }
    public IReadOnlyDictionary<string, string> SessionEnv { get; set; } =
        new Dictionary<string, string>();
    public ConcurrentDictionary<string, BashSession> Sessions { get; } = new();

    /// <summary>Resolve path confinado ao workspace (null = escape attempt).</summary>
    public string? Resolve(string rel)
    {
        var combined = Path.GetFullPath(Path.Combine(
            WorkspaceRoot, rel.TrimStart('/')));
        var root = Path.GetFullPath(WorkspaceRoot);
        return combined == root || combined.StartsWith(
            root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? combined : null;
    }

    /// <summary>Env do blueprint exposto a novos processos via profile.d.</summary>
    public void WriteProfileEnv()
    {
        try
        {
            var dir = "/etc/profile.d";
            if (!Directory.Exists(dir) || SessionEnv.Count == 0)
            {
                return;
            }

            var sb = new StringBuilder();
            foreach (var (k, v) in SessionEnv)
            {
                sb.Append("export ").Append(k).Append("='")
                    .Append(v.Replace("'", "'\\''")).Append("'\n");
            }

            File.WriteAllText(Path.Combine(dir, "ohn-session.sh"), sb.ToString());
        }
        catch
        {
            // Sem permissão de profile.d (user não-root) — env ainda é
            // injetado diretamente nos processos filhos do agente.
        }
    }
}

/// <summary>Corpo de <c>POST /api/init</c> (paridade upstream).</summary>
public sealed record InitRequest(
    string? SessionKey = null,
    string? Workspace = null,
    Dictionary<string, string>? Env = null);

/// <summary>Corpo de <c>POST /exec</c>.</summary>
public sealed record ExecRequest(
    string Command,
    int? TimeoutSeconds = null,
    string? Workdir = null,
    string? SessionId = null);

/// <summary>Resultado de comando — espelha o contrato do host.</summary>
public sealed record CommandResult(
    int ExitCode, string StdOut, string StdErr, bool TimedOut);

/// <summary>Execução one-shot <c>bash -c</c> com timeout e cap de saída.</summary>
public static class ExecOnce
{
    private const int OutputCap = 2 * 1024 * 1024;

    public static async Task<CommandResult> RunAsync(
        string command, string workdir,
        IReadOnlyDictionary<string, string> env,
        TimeSpan timeout, CancellationToken ct)
    {
        var psi = NewBash(workdir, env);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        using var proc = Process.Start(psi)!;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outTask = PumpAsync(proc.StandardOutput, stdout);
        var errTask = PumpAsync(proc.StandardError, stderr);
        var timedOut = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await proc.WaitForExitAsync(linked.Token)
                .WaitAsync(timeout, linked.Token);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            try { proc.Kill(entireProcessTree: true); } catch { }
        }

        await Task.WhenAll(outTask, errTask);
        return new CommandResult(
            timedOut ? -1 : proc.ExitCode,
            stdout.ToString(), stderr.ToString(), timedOut);
    }

    internal static ProcessStartInfo NewBash(
        string workdir, IReadOnlyDictionary<string, string> env)
    {
        var psi = new ProcessStartInfo("bash")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Directory.Exists(workdir) ? workdir : "/",
        };
        foreach (var (k, v) in env)
        {
            psi.Environment[k] = v;
        }

        return psi;
    }

    internal static async Task PumpAsync(StreamReader reader, StringBuilder sb)
    {
        var buf = new char[8192];
        int n;
        while ((n = await reader.ReadAsync(buf)) > 0)
        {
            lock (sb)
            {
                if (sb.Length < OutputCap)
                {
                    sb.Append(buf, 0, n);
                }
            }
        }
    }
}

/// <summary>Sessão bash persistente (estado preservado entre comandos).</summary>
public sealed class BashSession : IDisposable
{
    private const string Marker = "__OHN_EXIT_";
    private readonly Process _proc;
    private readonly StringBuilder _out = new();
    private readonly StringBuilder _err = new();
    private readonly object _gate = new();
    private TaskCompletionSource _data = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public BashSession(string workdir, IReadOnlyDictionary<string, string> env)
    {
        _proc = Process.Start(ExecOnce.NewBash(workdir, env))!;
        _ = Task.Run(() => Pump(_proc.StandardOutput, _out));
        _ = Task.Run(() => Pump(_proc.StandardError, _err));
    }

    public async Task<CommandResult> RunAsync(
        string command, TimeSpan timeout, CancellationToken ct)
    {
        var marker = Marker + Guid.NewGuid().ToString("N")[..8];
        lock (_gate)
        {
            _out.Clear();
            _err.Clear();
        }

        await _proc.StandardInput.WriteAsync(
            $"{command}\nprintf '\\n{marker}%d\\n' $?\n");
        await _proc.StandardInput.FlushAsync();

        var deadline = DateTime.UtcNow + timeout;
        var timedOut = false;
        while (true)
        {
            TaskCompletionSource tcs;
            string s;
            lock (_gate)
            {
                s = _out.ToString();
                tcs = _data;
            }

            var idx = s.LastIndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0)
            {
                var code = s[(idx + marker.Length)..];
                var nl = code.IndexOf('\n');
                if (nl >= 0)
                {
                    int.TryParse(code[..nl], out var exitCode);
                    lock (_gate)
                    {
                        _out.Remove(idx, _out.Length - idx);
                    }

                    return new CommandResult(exitCode, s[..idx],
                        LockedErr(), false);
                }
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                timedOut = true;
                break;
            }

            try
            {
                await tcs.Task.WaitAsync(remaining, ct);
            }
            catch (TimeoutException)
            {
                timedOut = true;
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        return new CommandResult(-1, "", LockedErr(), timedOut);
    }

    private string LockedErr()
    {
        lock (_gate)
        {
            return _err.ToString();
        }
    }

    private async Task Pump(StreamReader reader, StringBuilder sb)
    {
        var buf = new char[8192];
        int n;
        while ((n = await reader.ReadAsync(buf)) > 0)
        {
            lock (_gate)
            {
                sb.Append(buf, 0, n);
                var t = _data;
                _data = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                t.TrySetResult();
            }
        }
    }

    public void Dispose()
    {
        try { _proc.Kill(entireProcessTree: true); } catch { }
        _proc.Dispose();
    }
}

/// <summary>Marker para WebApplicationFactory nos testes.</summary>
public partial class SandboxAgentProgram;
