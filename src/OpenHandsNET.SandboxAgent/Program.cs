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

// SPEC-40 RF-002/003: surfaces web opt-in — env do container ativa no
// boot; env do blueprint/init ativa após /api/init. Sem flag → surface
// ausente e nenhum processo extra roda no sandbox.
var surfaces = new SurfaceRegistry(runtime);
surfaces.Register("terminal", "SANDBOX_TERMINAL", 7681, "ws",
    "ttyd",
    s => ["--port", "7681", "--writable", "--cwd", s,
        "-t", "disableLeaveAlert=true", "bash", "-l"],
    installer: null);
surfaces.Register("vscode", "SANDBOX_VSCODE", 8080, "http",
    "/opt/ohn/bin/code-server",
    s => ["--bind-addr", "0.0.0.0:8080", "--auth", "none",
        "--disable-telemetry", "--disable-update-check", s],
    installer: "/opt/ohn/sdks/code-server.sh");
surfaces.StartEnabled(builder.Configuration);

var ExecStreamJson = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
};

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

// SPEC-24 RF-006: heartbeat v2 — agent_version sinaliza /exec/stream etc.
app.MapGet("/alive", () => Results.Ok(new
{
    status = "ok",
    state = runtime.Status,
    agent_version = runtime.AgentVersion,
    uptime_s = (long)(DateTimeOffset.UtcNow - runtime.StartedAt)
        .TotalSeconds,
    workspace = runtime.WorkspaceRoot,
    sessions = runtime.Sessions.Count,
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
        // SPEC-40 RF-002/003: surfaces opt-in via env do blueprint/init.
        surfaces.StartEnabled(req.Env);

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
    runtime.Record("exec_start", Trunc(req.Command));
    CommandResult result;
    if (req.SessionId is { Length: > 0 } sid)
    {
        var session = runtime.Sessions.GetOrAdd(
            sid, _ => new BashSession(runtime.WorkspaceRoot, runtime.SessionEnv));
        session.LastCommand = Trunc(req.Command);
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
});

// SPEC-24 RF-001: exec com stream SSE — chunks stdout/stderr contínuos +
// evento terminal "exit". /exec (agregado) continua para compat.
app.MapPost("/exec/stream", async (HttpContext http, ExecRequest req,
    CancellationToken ct) =>
{
    if (runtime.Status != "ready")
    {
        return Results.Json(new { error = "dormant" }, statusCode: 503);
    }

    if (!Authorized(http))
    {
        return Results.Json(new { error = "unauthorized" }, statusCode: 401);
    }

    http.Response.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    var timeout = TimeSpan.FromSeconds(
        Math.Clamp(req.TimeoutSeconds ?? 120, 1, 3600));
    runtime.Record("exec_start", Trunc(req.Command));
    var sw = Stopwatch.StartNew();

    CommandResult result;
    var truncated = false;
    void OnChunk(string channel, string text)
    {
        runtime.Record("output", $"{channel}:{Trunc(text, 120)}");
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

    if (req.SessionId is { Length: > 0 } sid)
    {
        var session = runtime.Sessions.GetOrAdd(
            sid, _ => new BashSession(runtime.WorkspaceRoot, runtime.SessionEnv));
        session.LastCommand = Trunc(req.Command);
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
            duration_ms = (long)sw.ElapsedMilliseconds,
            timed_out = result.TimedOut,
            truncated,
            std_out = result.StdOut,
            std_err = result.StdErr,
        }, ExecStreamJson) + "\n\n", ct);
    await http.Response.Body.FlushAsync(ct);
    return Results.Empty;
});

// SPEC-24 RF-001: sessões bash vivas + processos + ring de atividade.
app.MapGet("/exec/sessions", (HttpContext http) =>
{
    if (!Authorized(http))
    {
        return Results.Json(new { error = "unauthorized" }, statusCode: 401);
    }

    return Results.Ok(runtime.Sessions.Select(kv => new
    {
        id = kv.Key,
        started_at = kv.Value.StartedAt,
        last_command = kv.Value.LastCommand,
    }));
});

app.MapGet("/proc", (HttpContext http) =>
{
    if (!Authorized(http))
    {
        return Results.Json(new { error = "unauthorized" }, statusCode: 401);
    }

    return Results.Ok(ProbeProc());
});

app.MapGet("/activity", (HttpContext http) =>
{
    if (!Authorized(http))
    {
        return Results.Json(new { error = "unauthorized" }, statusCode: 401);
    }

    // Last-Event-ID → replay só do que o cliente ainda não viu.
    var after = long.TryParse(http.Request.Headers["Last-Event-ID"],
        out var seq) ? seq : 0;
    return Results.Ok(runtime.Activity
        .Where(a => a.Seq > after).ToList());
});

static List<object> ProbeProc()
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

            long rssKb = 0;
            foreach (var line in File.ReadLines(
                Path.Combine(dir, "status")))
            {
                if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ',
                        StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        long.TryParse(parts[1], out rssKb);
                    }

                    break;
                }
            }

            list.Add(new { pid, cmd = Trunc(cmd, 200), rss_kb = rssKb });
        }
        catch
        {
            // Processo sumiu entre o enumerate e o read — best-effort.
        }
    }

    return list;
}

static string Trunc(string s, int max = 200) =>
    s.Length <= max ? s : s[..max] + "…";

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

// SPEC-40 RF-002/003: surfaces ativas — a Api publica cada porta via
// SandboxUrlBuilder (SPEC-38) + preview token (RF-004).
app.MapGet("/surfaces", (HttpContext http) =>
{
    if (!Authorized(http))
    {
        return Results.Json(new { error = "unauthorized" }, statusCode: 401);
    }

    return Results.Ok(surfaces.Active().Select(s => new
    {
        name = s.Name,
        port = s.Port,
        protocol = s.Protocol,
    }).ToList());
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
    runtime.Record("file_write", path);
    return Results.NoContent();
});

bool Ready(HttpContext _) => runtime.Status == "ready";
IResult Dormant(HttpContext _) =>
    Results.Json(new { error = "dormant" }, statusCode: 503);
IResult Unauthorized(HttpContext _) =>
    Results.Json(new { error = "unauthorized" }, statusCode: 401);

app.Run();

/// <summary>Uma entrada do ring <c>/activity</c> (SPEC-24 RF-001).</summary>
public sealed record ActivityItem(long Seq, string Ts, string Type, string Detail);

/// <summary>Deferred-init state + session key + workspace confinement.</summary>
public sealed class AgentState
{
    private const int ActivityCap = 200;
    private long _seq;

    public AgentState(string WorkspaceRoot, string? InitKey, bool Deferred)
    {
        this.WorkspaceRoot = WorkspaceRoot;
        this.InitKey = InitKey;
        this.Deferred = Deferred;
        Status = Deferred ? "dormant" : "initializing";
    }

    /// <summary>Protocol version — &gt;=2 exposes /exec/stream + /proc +
    /// /activity; &gt;=3 exposes /surfaces (SPEC-40).</summary>
    public int AgentVersion => 3;
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public string Status { get; set; }
    public string WorkspaceRoot { get; set; }
    public string? InitKey { get; }
    public bool Deferred { get; }
    public string? SessionKey { get; set; }
    public bool Ready { set => Status = value ? "ready" : Status; }
    public IReadOnlyDictionary<string, string> SessionEnv { get; set; } =
        new Dictionary<string, string>();
    public ConcurrentDictionary<string, BashSession> Sessions { get; } = new();
    public ConcurrentQueue<ActivityItem> Activity { get; } = new();

    /// <summary>Appends to the activity ring (bounded at 200 entries).</summary>
    public void Record(string type, string detail)
    {
        Activity.Enqueue(new ActivityItem(
            Interlocked.Increment(ref _seq),
            DateTimeOffset.UtcNow.ToString("O"), type, detail));
        while (Activity.Count > ActivityCap)
        {
            Activity.TryDequeue(out _);
        }
    }

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
        TimeSpan timeout, CancellationToken ct) =>
        (await RunStreamingAsync(command, workdir, env, timeout, ct, null))
            .Result;

    /// <summary>
    /// Executa emitindo chunks por canal via <paramref name="onChunk"/>
    /// (SPEC-24 RF-001). <c>Truncated</c> indica que algum canal estourou o
    /// cap de 2MB — chunks além do cap não são emitidos.
    /// </summary>
    public static async Task<(CommandResult Result, bool Truncated)>
        RunStreamingAsync(
            string command, string workdir,
            IReadOnlyDictionary<string, string> env,
            TimeSpan timeout, CancellationToken ct,
            Action<string, string>? onChunk)
    {
        var psi = NewBash(workdir, env);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        using var proc = Process.Start(psi)!;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var hitCap = false;
        var outTask = PumpAsync(proc.StandardOutput, stdout, "stdout",
            onChunk, () => hitCap = true);
        var errTask = PumpAsync(proc.StandardError, stderr, "stderr",
            onChunk, () => hitCap = true);
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
        return (new CommandResult(
            timedOut ? -1 : proc.ExitCode,
            stdout.ToString(), stderr.ToString(), timedOut), hitCap);
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

    internal static Task PumpAsync(StreamReader reader, StringBuilder sb) =>
        PumpAsync(reader, sb, "stdout", null, null);

    internal static async Task PumpAsync(
        StreamReader reader, StringBuilder sb, string channel,
        Action<string, string>? onChunk, Action? onCap)
    {
        var buf = new char[8192];
        int n;
        var capped = false;
        while ((n = await reader.ReadAsync(buf)) > 0)
        {
            var text = new string(buf, 0, n);
            lock (sb)
            {
                if (sb.Length < OutputCap)
                {
                    sb.Append(buf, 0, n);
                }
                else if (!capped)
                {
                    capped = true;
                    onCap?.Invoke();
                }
            }

            if (!capped)
            {
                onChunk?.Invoke(channel, text);
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
    private volatile Action<string, string>? _onChunk;
    // Hold-back por canal: o marker de exit (~25 chars) nunca é emitido no
    // stream — os últimos bytes ficam retidos até o próximo chunk ou até o
    // fim do comando, quando são descartados junto com o marker.
    private const int Holdback = 48;
    private string _tailOut = "";
    private string _tailErr = "";
    private TaskCompletionSource _data = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public BashSession(string workdir, IReadOnlyDictionary<string, string> env)
    {
        StartedAt = DateTimeOffset.UtcNow;
        _proc = Process.Start(ExecOnce.NewBash(workdir, env))!;
        _ = Task.Run(() => Pump(_proc.StandardOutput, _out, "stdout"));
        _ = Task.Run(() => Pump(_proc.StandardError, _err, "stderr"));
    }

    /// <summary>SPEC-24: metadados exibidos por GET /exec/sessions.</summary>
    public DateTimeOffset StartedAt { get; }
    public string? LastCommand { get; set; }

    public Task<CommandResult> RunAsync(
        string command, TimeSpan timeout, CancellationToken ct) =>
        RunAsync(command, timeout, ct, null);

    public async Task<CommandResult> RunAsync(
        string command, TimeSpan timeout, CancellationToken ct,
        Action<string, string>? onChunk)
    {
        _onChunk = onChunk;
        _tailOut = "";
        _tailErr = "";
        try
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
        finally
        {
            _onChunk = null;
        }
    }

    private string LockedErr()
    {
        lock (_gate)
        {
            return _err.ToString();
        }
    }

    private async Task Pump(
        StreamReader reader, StringBuilder sb, string channel)
    {
        var buf = new char[8192];
        int n;
        while ((n = await reader.ReadAsync(buf)) > 0)
        {
            var text = new string(buf, 0, n);
            lock (_gate)
            {
                sb.Append(buf, 0, n);
                var t = _data;
                _data = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                t.TrySetResult();
            }

            // SPEC-24: tap de streaming — chunks saem ao vivo durante o
            // comando; o marker só determina o fim da execução.
            var tap = _onChunk;
            if (tap is not null)
            {
                var combined = (channel == "stdout" ? _tailOut : _tailErr) + text;
                var emit = combined.Length > Holdback
                    ? combined[..^Holdback]
                    : null;
                var tail = combined.Length > Holdback
                    ? combined[^Holdback..]
                    : combined;
                if (channel == "stdout")
                {
                    _tailOut = tail;
                }
                else
                {
                    _tailErr = tail;
                }

                if (emit is { Length: > 0 })
                {
                    tap.Invoke(channel, emit);
                }
            }
        }
    }

    public void Dispose()
    {
        try { _proc.Kill(entireProcessTree: true); } catch { }
        _proc.Dispose();
    }
}

/// <summary>
/// SPEC-40 RF-002/003: registry de surfaces — cada uma é um processo
/// supervisionado opt-in por env flag. Sem credencial própria: a auth
/// fica no preview token do SandboxHostRouter (RF-004) — as portas só
/// são alcançáveis pela rede interna do sandbox.
/// </summary>
public sealed class SurfaceRegistry(AgentState state)
{
    private readonly List<SurfaceSupervisor> _all = [];

    /// <summary>Registra uma surface (bind 0.0.0.0 dentro do container).</summary>
    public void Register(string name, string envFlag, int port,
        string protocol, string binary, Func<string, string[]> args,
        string? installer) =>
        _all.Add(new SurfaceSupervisor(
            state, name, envFlag, port, protocol, binary, args, installer));

    /// <summary>Liga as surfaces cuja env flag está set (boot ou init).</summary>
    public void StartEnabled(IConfiguration cfg)
    {
        foreach (var s in _all)
        {
            if (s.FlaggedOn(cfg[s.EnvFlag]))
            {
                s.Start();
            }
        }
    }

    /// <inheritdoc cref="StartEnabled(IConfiguration)"/>
    public void StartEnabled(IReadOnlyDictionary<string, string>? env)
    {
        if (env is null)
        {
            return;
        }

        foreach (var s in _all)
        {
            if (env.TryGetValue(s.EnvFlag, out var v) && s.FlaggedOn(v))
            {
                s.Start();
            }
        }
    }

    /// <summary>Surfaces com processo vivo neste instante.</summary>
    public IEnumerable<AgentSurfaceItem> Active() =>
        _all.Where(s => s.Running)
            .Select(s => new AgentSurfaceItem(s.Name, s.Port, s.Protocol));
}

/// <summary>Surface exposta por <c>GET /surfaces</c>.</summary>
public sealed record AgentSurfaceItem(string Name, int Port, string Protocol);

/// <summary>
/// Supervisor de um processo-surface: spawn + respawn com backoff
/// enquanto habilitado. Quando o binário falta da imagem e existe
/// <paramref name="installer"/>, roda o instalador uma vez e tenta de
/// novo; senão a surface fica indisponível sem crash do agente.
/// </summary>
public sealed class SurfaceSupervisor(
    AgentState state, string name, string envFlag, int port,
    string protocol, string binary, Func<string, string[]> args,
    string? installer)
{
    private const int MaxRestarts = 10;
    private Process? _proc;
    private bool _enabled;
    private bool _installTried;

    public string Name { get; } = name;
    public string EnvFlag { get; } = envFlag;
    public int Port { get; } = port;
    public string Protocol { get; } = protocol;

    /// <summary>Processo vivo agora.</summary>
    public bool Running => _proc is { HasExited: false };

    /// <summary>Flag env ("1"/"true") considerada ativa.</summary>
    public bool FlaggedOn(string? v) =>
        string.Equals(v, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Liga a surface e inicia o loop de supervisão.</summary>
    public void Start()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        _ = Task.Run(SuperviseAsync);
    }

    private async Task SuperviseAsync()
    {
        var restarts = 0;
        while (_enabled && restarts < MaxRestarts)
        {
            Process proc;
            try
            {
                var psi = new ProcessStartInfo(binary)
                {
                    WorkingDirectory = state.WorkspaceRoot,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                };
                foreach (var a in args(state.WorkspaceRoot))
                {
                    psi.ArgumentList.Add(a);
                }

                proc = Process.Start(psi)!;
            }
            catch (Exception)
            {
                // Binário ausente → tenta o instalador uma única vez.
                if (installer is null || _installTried
                    || !await RunInstallerAsync())
                {
                    _enabled = false;
                    return;
                }

                continue;
            }

            _proc = proc;
            state.Record("sandbox_surface", $"{Name}:{Port} started");
            try
            {
                await proc.WaitForExitAsync();
            }
            catch
            {
                return; // agente encerrando — sai sem respawn.
            }

            _proc = null;
            restarts++;
            state.Record("sandbox_surface",
                $"{Name}:{Port} stopped exit={proc.ExitCode} restart={restarts}");
            proc.Dispose();
            if (_enabled)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        _enabled = false;
    }

    private async Task<bool> RunInstallerAsync()
    {
        _installTried = true;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("bash")
            {
                ArgumentList = { installer! },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            await p.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromMinutes(5));
            state.Record("sandbox_surface",
                $"{Name} install exit={p.ExitCode}");
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Marker para WebApplicationFactory nos testes.</summary>
public partial class SandboxAgentProgram;