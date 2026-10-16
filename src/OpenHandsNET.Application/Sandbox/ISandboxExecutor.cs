using OpenHandsNET.Domain.Sandbox;

namespace OpenHandsNET.Application.Sandbox;

/// <summary>
/// Boundary between the agent worker and the sandbox: commands, persistent
/// bash, file ops and git — nothing runs on the host.
/// </summary>
public interface ISandboxExecutor
{
    /// <summary>Opens a persistent bash session (state kept across commands).</summary>
    Task<IBashSession> OpenBashSessionAsync(
        Domain.Sandbox.Sandbox sandbox, CancellationToken ct);

    /// <summary>One-shot command: returns exit code + captured streams.</summary>
    Task<CommandResult> ExecAsync(
        Domain.Sandbox.Sandbox sandbox, string command, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// One-shot command with live output (SPEC-24 RF-002). Default
    /// implementation aggregates via <see cref="ExecAsync"/> — transports
    /// without streaming still work, just without incremental chunks.
    /// </summary>
    async IAsyncEnumerable<ExecChunk> ExecStreamingAsync(
        Domain.Sandbox.Sandbox sandbox, string command, TimeSpan timeout,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await ExecAsync(sandbox, command, timeout, ct);
        yield return new ExecCompleted(
            result, (long)sw.ElapsedMilliseconds, false);
    }

    Task<string> ReadFileAsync(Domain.Sandbox.Sandbox sandbox, string path, CancellationToken ct);

    Task WriteFileAsync(
        Domain.Sandbox.Sandbox sandbox, string path, string content, CancellationToken ct);

    /// <summary>
    /// SPEC-45 RF-005: binary-safe write. Default: base64 via
    /// <see cref="ExecAsync"/> em chunks de 64KB (abaixo do
    /// MAX_ARG_STRLEN) acumulados em <c>{path}.upload.b64</c> + decode —
    /// funciona em qualquer transporte com exec. Transports com HTTP
    /// file-put podem sobrescrever p/ 1 chamada.
    /// </summary>
    async Task WriteFileBytesAsync(
        Domain.Sandbox.Sandbox sandbox, string path, byte[] content,
        CancellationToken ct)
    {
        var b64 = Convert.ToBase64String(content);
        var tmp = path + ".upload.b64";
        const int chunk = 64 * 1024;
        for (var i = 0; i < b64.Length; i += chunk)
        {
            var part = b64.Substring(i, Math.Min(chunk, b64.Length - i));
            var op = i == 0 ? ">" : ">>";
            var r = await ExecAsync(sandbox,
                $"echo -n '{part}' {op} '{tmp}'",
                TimeSpan.FromSeconds(30), ct);
            if (r.ExitCode != 0)
            {
                throw new IOException(
                    $"sandbox upload staging failed ({path}): {r.StdErr}");
            }
        }

        var done = await ExecAsync(sandbox,
            $"mkdir -p -- \"$(dirname -- '{path}')\" && " +
            $"base64 -d '{tmp}' > '{path}' && rm -f -- '{tmp}'",
            TimeSpan.FromSeconds(60), ct);
        if (done.ExitCode != 0)
        {
            throw new IOException(
                $"sandbox upload decode failed ({path}): {done.StdErr}");
        }
    }

    Task<IReadOnlyList<FileEntry>> ListFilesAsync(
        Domain.Sandbox.Sandbox sandbox, string path, CancellationToken ct);

    /// <summary>Workspace git diff vs HEAD (unstaged + staged).</summary>
    Task<GitDiff> GetGitDiffAsync(Domain.Sandbox.Sandbox sandbox, CancellationToken ct);

    /// <summary>
    /// SPEC-24 RF-007: probes the in-container agent (<c>/alive</c> v2).
    /// Default: <c>null</c> — transports without an agent report nothing.
    /// </summary>
    Task<SandboxAgentInfo?> AgentInfoAsync(
        Domain.Sandbox.Sandbox sandbox, CancellationToken ct) =>
        Task.FromResult<SandboxAgentInfo?>(null);

    /// <summary>
    /// SPEC-40 RF-002: surfaces ativas no sandbox (<c>GET /surfaces</c> do
    /// agente — terminal ttyd etc.). Default: vazio — transports sem
    /// agente não expõem surfaces.
    /// </summary>
    Task<IReadOnlyList<AgentSurface>> SurfacesAsync(
        Domain.Sandbox.Sandbox sandbox, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AgentSurface>>([]);
}

/// <summary>Persistent bash inside the sandbox (stdin/stdout multiplexed).</summary>
public interface IBashSession : IAsyncDisposable
{
    /// <summary>
    /// Runs <paramref name="command"/> in the session, returning when the
    /// completion marker is seen or <paramref name="timeout"/> elapses.
    /// </summary>
    Task<CommandResult> RunAsync(string command, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Result of a sandbox command (upstream CmdOutputObservation parity).</summary>
public sealed record CommandResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

/// <summary>Stream item of <see cref="ISandboxExecutor.ExecStreamingAsync"/> (SPEC-24).</summary>
public abstract record ExecChunk;

/// <summary>Live output on one channel (stdout/stderr).</summary>
public sealed record OutputChunk(string Channel, string Text) : ExecChunk;

/// <summary>Terminal stream item carrying the aggregated result.</summary>
public sealed record ExecCompleted(
    CommandResult Result, long DurationMs, bool Truncated) : ExecChunk;

/// <summary>
/// In-container agent heartbeat (SPEC-24 RF-007, <c>/alive</c> v2).
/// <c>AgentVersion &gt;= 2</c> supports <c>ExecStreamingAsync</c>.
/// </summary>
public sealed record SandboxAgentInfo(
    int AgentVersion, string State, long UptimeS, int Sessions);

/// <summary>Surface exposta pelo agente (SPEC-40 RF-002).</summary>
public sealed record AgentSurface(string Name, int Port, string Protocol);

/// <summary>A workspace file entry.</summary>
public sealed record FileEntry(string Path, bool IsDirectory, long Size);

/// <summary>Workspace git diff summary + patch.</summary>
public sealed record GitDiff(
    IReadOnlyList<string> ChangedFiles, string Patch, int Additions, int Deletions);
