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

    Task<string> ReadFileAsync(Domain.Sandbox.Sandbox sandbox, string path, CancellationToken ct);

    Task WriteFileAsync(
        Domain.Sandbox.Sandbox sandbox, string path, string content, CancellationToken ct);

    Task<IReadOnlyList<FileEntry>> ListFilesAsync(
        Domain.Sandbox.Sandbox sandbox, string path, CancellationToken ct);

    /// <summary>Workspace git diff vs HEAD (unstaged + staged).</summary>
    Task<GitDiff> GetGitDiffAsync(Domain.Sandbox.Sandbox sandbox, CancellationToken ct);
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

/// <summary>A workspace file entry.</summary>
public sealed record FileEntry(string Path, bool IsDirectory, long Size);

/// <summary>Workspace git diff summary + patch.</summary>
public sealed record GitDiff(
    IReadOnlyList<string> ChangedFiles, string Patch, int Additions, int Deletions);
