using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Docker.DotNet;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Infrastructure.Persistence;
using DomainSandbox = OpenHandsNET.Domain.Sandbox.Sandbox;

namespace OpenHandsNET.Infrastructure.Sandbox;

/// <summary>
/// SPEC-17 RF-003: <see cref="ISandboxExecutor"/> dual-mode — probe
/// <c>/alive</c> por sandbox (cacheado): agente responde → HTTP
/// <c>SandboxAgentTransport</c>; imagem legada sem agente →
/// <c>docker exec</c> (compat total).
/// </summary>
public sealed class SandboxAgentExecutor(
    IDockerClient docker,
    SandboxAgentClient agent,
    ISandboxEngine engine,
    AppDbContext db,
    IHttpClientFactory httpFactory,
    ILogger<SandboxAgentExecutor> logger,
    IDataProtectionProvider? dp = null) : ISandboxExecutor
{
    private readonly DockerSandboxExecutor _dockerExec = new(docker);
    private readonly ConcurrentDictionary<Guid,
        Task<SandboxAgentClient.AgentAliveInfo?>> _capability = new();
    private readonly IDataProtector? _protector =
        dp?.CreateProtector("openhandsnet.sandbox.session");

    /// <inheritdoc />
    public async Task<CommandResult> ExecAsync(
        DomainSandbox sandbox, string command, TimeSpan timeout,
        CancellationToken ct)
    {
        var transport = await ResolveAsync(sandbox, ct);
        if (transport is null)
        {
            return await _dockerExec.ExecAsync(sandbox, command, timeout, ct);
        }

        return await transport.ExecAsync(command, timeout, ct);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ExecChunk> ExecStreamingAsync(
        DomainSandbox sandbox, string command, TimeSpan timeout,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct)
    {
        var transport = await ResolveAsync(sandbox, ct);
        if (transport is null || transport.AgentVersion < 2)
        {
            // Agente legado (v1) ou docker exec — agrega sem stream.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = transport is null
                ? await _dockerExec.ExecAsync(sandbox, command, timeout, ct)
                : await transport.ExecAsync(command, timeout, ct);
            yield return new ExecCompleted(
                result, (long)sw.ElapsedMilliseconds, false);
            yield break;
        }

        await foreach (var chunk in transport.ExecStreamAsync(
            command, timeout, ct))
        {
            yield return chunk;
        }
    }

    /// <inheritdoc />
    public async Task<IBashSession> OpenBashSessionAsync(
        DomainSandbox sandbox, CancellationToken ct)
    {
        var transport = await ResolveAsync(sandbox, ct);
        if (transport is null)
        {
            return await _dockerExec.OpenBashSessionAsync(sandbox, ct);
        }

        var sessionId = Guid.NewGuid().ToString("N");
        return new AgentBashSession(transport, sessionId);
    }

    /// <inheritdoc />
    public async Task<string> ReadFileAsync(
        DomainSandbox sandbox, string path, CancellationToken ct)
    {
        var transport = await ResolveAsync(sandbox, ct);
        if (transport is null)
        {
            return await _dockerExec.ReadFileAsync(sandbox, path, ct);
        }

        // Confina ao workspace — o agente também valida server-side.
        var rel = RelativeWorkspace(path);
        var res = await transport.GetFileAsync(rel, ct);
        return res ?? throw new FileNotFoundException(
            $"sandbox file not found/readable: {path}", path);
    }

    /// <inheritdoc />
    public async Task WriteFileAsync(
        DomainSandbox sandbox, string path, string content, CancellationToken ct)
    {
        var transport = await ResolveAsync(sandbox, ct);
        if (transport is null)
        {
            await _dockerExec.WriteFileAsync(sandbox, path, content, ct);
            return;
        }

        await transport.PutFileAsync(RelativeWorkspace(path), content, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FileEntry>> ListFilesAsync(
        DomainSandbox sandbox, string path, CancellationToken ct)
    {
        var transport = await ResolveAsync(sandbox, ct);
        if (transport is null)
        {
            return await _dockerExec.ListFilesAsync(sandbox, path, ct);
        }

        return await transport.ListFilesAsync(RelativeWorkspace(path), ct);
    }

    /// <inheritdoc />
    public async Task<GitDiff> GetGitDiffAsync(
        DomainSandbox sandbox, CancellationToken ct)
    {
        // Diff é exec de git — o transporte HTTP cobre via /exec.
        var transport = await ResolveAsync(sandbox, ct);
        if (transport is null)
        {
            return await _dockerExec.GetGitDiffAsync(sandbox, ct);
        }

        var ws = Domain.Sandbox.SandboxSpec.WorkspacePath;
        var patch = await transport.ExecAsync(
            $"git -C {ws} diff HEAD 2>/dev/null || git -C {ws} diff",
            TimeSpan.FromSeconds(30), ct);
        var stat = await transport.ExecAsync(
            $"git -C {ws} diff --numstat HEAD 2>/dev/null || git -C {ws} diff --numstat",
            TimeSpan.FromSeconds(30), ct);
        var files = stat.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t'))
            .Where(p => p.Length == 3)
            .ToList();
        return new GitDiff(
            files.Select(p => p[2]).ToList(), patch.StdOut,
            files.Sum(p => int.TryParse(p[0], out var n) ? n : 0),
            files.Sum(p => int.TryParse(p[1], out var n) ? n : 0));
    }

    /// <summary>
    /// Resolve o transporte HTTP se o agente estiver vivo; cacheia por
    /// sandbox id. Null → fallback docker exec.
    /// </summary>
    internal async Task<SandboxAgentTransport?> ResolveAsync(
        DomainSandbox sandbox, CancellationToken ct)
    {
        if (sandbox.ContainerId is null)
        {
            return null;
        }

        var capable = await _capability.GetOrAdd(sandbox.Id, _ => ProbeAsync(sandbox));
        if (capable is null)
        {
            return null;
        }

        var key = await SessionKeyAsync(sandbox.Id, ct);
        var baseUrl = (await engine.InspectAsync(sandbox.ContainerId, ct))
            ?.AgentUrl;
        if (key is null || baseUrl is null)
        {
            _capability.TryRemove(sandbox.Id, out _);
            return null;
        }

        return new SandboxAgentTransport(
            httpFactory, baseUrl, key, capable.AgentVersion);
    }

    /// <inheritdoc/>
    public async Task<SandboxAgentInfo?> AgentInfoAsync(
        DomainSandbox sandbox, CancellationToken ct)
    {
        var alive = await _capability.GetOrAdd(
            sandbox.Id, _ => ProbeAsync(sandbox));
        return alive is null
            ? null
            : new SandboxAgentInfo(alive.AgentVersion, alive.State,
                alive.UptimeS, alive.Sessions);
    }

    /// <summary>Agent version detected on this sandbox (null = legacy/none).</summary>
    internal async Task<int?> AgentVersionAsync(
        DomainSandbox sandbox, CancellationToken ct) =>
        (await _capability.GetOrAdd(sandbox.Id, _ => ProbeAsync(sandbox)))
            ?.AgentVersion;

    private async Task<SandboxAgentClient.AgentAliveInfo?> ProbeAsync(
        DomainSandbox sandbox)
    {
        try
        {
            var baseUrl = (await engine.InspectAsync(
                sandbox.ContainerId!, CancellationToken.None))?.AgentUrl;
            return baseUrl is null
                ? null
                : await agent.ProbeAliveV2Async(baseUrl, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "agent probe failed for sandbox {Id}",
                sandbox.Id);
            return null;
        }
    }

    private async Task<string?> SessionKeyAsync(Guid sandboxId, CancellationToken ct)
    {
        var record = await db.Sandboxes.FindAsync([sandboxId], ct);
        if (record?.SessionKeyProtected is null || _protector is null)
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(record.SessionKeyProtected);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "session key unprotect failed for sandbox {Id}", sandboxId);
            return null;
        }
    }

    private static string RelativeWorkspace(string path)
    {
        const string ws = "/workspace";
        var p = path.Replace('\\', '/');
        return p.StartsWith(ws + "/", StringComparison.Ordinal)
            ? p[(ws.Length + 1)..]
            : p == ws ? "" : p.TrimStart('/');
    }

    /// <summary>Sessão bash persistente via <c>session_id</c> do /exec.</summary>
    private sealed class AgentBashSession(
        SandboxAgentTransport transport, string sessionId) : IBashSession
    {
        public Task<CommandResult> RunAsync(
            string command, TimeSpan timeout, CancellationToken ct) =>
            transport.ExecAsync(command, timeout, ct, sessionId);

        public async ValueTask DisposeAsync()
        {
            try { await transport.DeleteSessionAsync(sessionId); }
            catch { /* best-effort */ }
        }
    }
}

/// <summary>Transporte HTTP fino contra o agente in-container.</summary>
public sealed class SandboxAgentTransport(
    IHttpClientFactory httpFactory, Uri baseUrl, string sessionKey,
    int agentVersion = 1)
{
    /// <summary>Versão do protocolo do agente (SPEC-24 RF-002: ≥2 = SSE).</summary>
    public int AgentVersion { get; } = agentVersion;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// <c>POST /exec/stream</c> (SPEC-24 RF-002): SSE <c>{channel,text}</c>
    /// por chunk + <c>{type:"exit",…}</c> terminal.
    /// </summary>
    public async IAsyncEnumerable<ExecChunk> ExecStreamAsync(
        string command, TimeSpan timeout,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct,
        string? sessionId = null)
    {
        var req = NewRequest(HttpMethod.Post, "/exec/stream");
        req.Content = JsonContent.Create(new
        {
            command,
            timeout_seconds = (int)Math.Ceiling(timeout.TotalSeconds),
            session_id = sessionId,
        }, options: Json);
        var res = await httpFactory.CreateClient("sandbox-agent").SendAsync(
            req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            res.EnsureSuccessStatusCode();
        }

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line["data:".Length..].Trim();
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var t)
                && t.GetString() == "exit")
            {
                yield return new ExecCompleted(
                    new CommandResult(
                        root.TryGetProperty("exit_code", out var ec)
                            ? ec.GetInt32() : -1,
                        root.TryGetProperty("std_out", out var so)
                            ? so.GetString() ?? "" : "",
                        root.TryGetProperty("std_err", out var se)
                            ? se.GetString() ?? "" : "",
                        root.TryGetProperty("timed_out", out var to)
                            && to.GetBoolean()),
                    root.TryGetProperty("duration_ms", out var dm)
                        ? dm.GetInt64() : (long)sw.ElapsedMilliseconds,
                    root.TryGetProperty("truncated", out var tr)
                        && tr.GetBoolean());
                yield break;
            }

            var channel = root.TryGetProperty("channel", out var ch)
                ? ch.GetString() ?? "stdout" : "stdout";
            var text = root.TryGetProperty("text", out var tx)
                ? tx.GetString() ?? "" : "";
            if (text.Length > 0)
            {
                yield return new OutputChunk(channel, text);
            }
        }
    }

    public async Task<CommandResult> ExecAsync(
        string command, TimeSpan timeout, CancellationToken ct,
        string? sessionId = null)
    {
        var req = NewRequest(HttpMethod.Post, "/exec");
        req.Content = JsonContent.Create(new
        {
            command,
            timeout_seconds = (int)Math.Ceiling(timeout.TotalSeconds),
            session_id = sessionId,
        }, options: Json);
        var res = await SendAsync(req, ct);
        var body = await res.Content.ReadFromJsonAsync<AgentExecResult>(
            Json, ct);
        return new CommandResult(
            body!.ExitCode, body.StdOut ?? "", body.StdErr ?? "",
            body.TimedOut);
    }

    public async Task<string?> GetFileAsync(string rel, CancellationToken ct)
    {
        var res = await SendAsync(NewRequest(HttpMethod.Get,
            "/files/" + Uri.EscapeDataString(rel).Replace("%2F", "/")), ct);
        return res.IsSuccessStatusCode
            ? await res.Content.ReadAsStringAsync(ct) : null;
    }

    public async Task PutFileAsync(string rel, string content, CancellationToken ct)
    {
        var req = NewRequest(HttpMethod.Put,
            "/files/" + Uri.EscapeDataString(rel).Replace("%2F", "/"));
        req.Content = new StringContent(content);
        var res = await SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<FileEntry>> ListFilesAsync(
        string rel, CancellationToken ct)
    {
        var res = await SendAsync(NewRequest(HttpMethod.Get,
            "/files/" + Uri.EscapeDataString(rel).Replace("%2F", "/")), ct);
        if (!res.IsSuccessStatusCode)
        {
            return [];
        }

        var items = await res.Content.ReadFromJsonAsync<List<AgentFileItem>>(
            Json, ct) ?? [];
        var prefix = Domain.Sandbox.SandboxSpec.WorkspacePath;
        return items.Select(i => new FileEntry(
            prefix + i.Path, i.IsDirectory, i.Size)).ToList();
    }

    public async Task DeleteSessionAsync(string sessionId)
    {
        var req = NewRequest(HttpMethod.Delete, $"/exec/sessions/{sessionId}");
        using var res = await SendAsync(req, CancellationToken.None);
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, new Uri(baseUrl, path));
        req.Headers.Add("X-Session-Key", sessionKey);
        return req;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage req, CancellationToken ct) =>
        await httpFactory.CreateClient("sandbox-agent").SendAsync(req, ct);

    private sealed record AgentExecResult(
        int ExitCode, string? StdOut, string? StdErr, bool TimedOut);

    private sealed record AgentFileItem(string Path, bool IsDirectory, long Size);
}
