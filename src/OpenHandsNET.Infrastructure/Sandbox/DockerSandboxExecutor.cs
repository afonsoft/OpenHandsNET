using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using OpenHandsNET.Application.Sandbox;
using DomainSandbox = OpenHandsNET.Domain.Sandbox.Sandbox;

namespace OpenHandsNET.Infrastructure.Sandbox;

/// <summary>
/// <see cref="ISandboxExecutor"/> over the Docker Engine exec API.
/// One-shot exec captures stdout/stderr demuxed; bash sessions keep a
/// persistent <c>docker exec -i bash</c> stream (state preserved between
/// commands, like the upstream tmux-based session).
/// </summary>
public sealed class DockerSandboxExecutor(IDockerClient docker) : ISandboxExecutor
{
    private const string ExitMarker = "__OHN_EXIT_";

    /// <inheritdoc />
    public async Task<IBashSession> OpenBashSessionAsync(
        DomainSandbox sandbox, CancellationToken ct)
    {
        var exec = await docker.Exec.CreateContainerExecAsync(
            sandbox.ContainerId!, new ContainerExecCreateParameters
            {
                AttachStdin = true, AttachStdout = true, AttachStderr = true,
                TTY = false,
                Cmd = ["bash"],
                WorkingDir = Domain.Sandbox.SandboxSpec.WorkspacePath,
            }, ct);
        var stream = await docker.Exec.StartContainerExecAsync(
            exec.ID, new ContainerExecStartParameters { TTY = false }, ct);
        return new BashSessionImpl(stream);
    }

    /// <inheritdoc />
    public async Task<CommandResult> ExecAsync(
        DomainSandbox sandbox, string command, TimeSpan timeout, CancellationToken ct)
    {
        var exec = await docker.Exec.CreateContainerExecAsync(
            sandbox.ContainerId!, new ContainerExecCreateParameters
            {
                AttachStdout = true, AttachStderr = true, TTY = false,
                Cmd = ["bash", "-c", command],
                WorkingDir = Domain.Sandbox.SandboxSpec.WorkspacePath,
            }, ct);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var timedOut = false;
        using (var stream = await docker.Exec
            .StartContainerExecAsync(
                exec.ID, new ContainerExecStartParameters { TTY = false }, ct))
        {
            try
            {
                await ReadToEndAsync(stream, stdout, stderr, linked.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = true;
            }
        }

        var inspect = await docker.Exec.InspectContainerExecAsync(exec.ID, ct);
        return new CommandResult(
            timedOut ? -1 : (int)(inspect.ExitCode ?? 0),
            stdout.ToString(), stderr.ToString(), timedOut);
    }

    /// <inheritdoc />
    public async Task<string> ReadFileAsync(
        DomainSandbox sandbox, string path, CancellationToken ct)
    {
        var r = await ExecAsync(
            sandbox, $"cat -- '{Escape(path)}'", TimeSpan.FromSeconds(30), ct);
        return r.ExitCode == 0
            ? r.StdOut
            : throw new FileNotFoundException(
                $"sandbox file not found/readable: {path}", path);
    }

    /// <inheritdoc />
    public async Task WriteFileAsync(
        DomainSandbox sandbox, string path, string content, CancellationToken ct)
    {
        // base64 no comando evita problemas de quoting/tamanho de argv.
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        var r = await ExecAsync(
            sandbox,
            $"mkdir -p -- \"$(dirname -- '{Escape(path)}')\" && " +
            $"echo '{b64}' | base64 -d > '{Escape(path)}'",
            TimeSpan.FromSeconds(60), ct);
        if (r.ExitCode != 0)
        {
            throw new IOException($"sandbox write failed ({path}): {r.StdErr}");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FileEntry>> ListFilesAsync(
        DomainSandbox sandbox, string path, CancellationToken ct)
    {
        var r = await ExecAsync(sandbox,
            "find '" + Escape(path) + "' -mindepth 1 -maxdepth 1 -exec sh -c '" +
            "for f do if [ -d \"$f\" ]; then t=d; else t=f; fi; " +
            "s=$(wc -c < \"$f\" 2>/dev/null || echo 0); echo \"$f|$t|$s\"; " +
            "done' _ {} +",
            TimeSpan.FromSeconds(30), ct);
        if (r.ExitCode != 0)
        {
            return [];
        }

        return r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('|'))
            .Where(p => p.Length == 3)
            .Select(p => new FileEntry(p[0], p[1] == "d", long.Parse(p[2])))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<GitDiff> GetGitDiffAsync(
        DomainSandbox sandbox, CancellationToken ct)
    {
        const string ws = "/workspace";
        var patch = await ExecAsync(sandbox,
            $"git -C {ws} diff HEAD 2>/dev/null || git -C {ws} diff", TimeSpan.FromSeconds(30), ct);
        var stat = await ExecAsync(sandbox,
            $"git -C {ws} diff --numstat HEAD 2>/dev/null || git -C {ws} diff --numstat",
            TimeSpan.FromSeconds(30), ct);
        var files = stat.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t'))
            .Where(p => p.Length == 3)
            .ToList();
        return new GitDiff(
            files.Select(p => p[2]).ToList(),
            patch.StdOut,
            files.Sum(p => int.TryParse(p[0], out var n) ? n : 0),
            files.Sum(p => int.TryParse(p[1], out var n) ? n : 0));
    }

    private static string Escape(string path) => path.Replace("'", "'\\''");

    /// <summary>Reads a multiplexed stream to EOF, demuxing stdout/stderr.</summary>
    internal static async Task ReadToEndAsync(
        MultiplexedStream stream, StringBuilder stdout, StringBuilder stderr,
        CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (true)
        {
            var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct);
            if (result.EOF)
            {
                break;
            }

            (result.Target == MultiplexedStream.TargetStream.StandardError ? stderr : stdout)
                .Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
    }

    private sealed class BashSessionImpl(MultiplexedStream stream) : IBashSession
    {
        public async Task<CommandResult> RunAsync(
            string command, TimeSpan timeout, CancellationToken ct)
        {
            var marker = ExitMarker + Guid.NewGuid().ToString("N")[..8];
            var bytes = Encoding.ASCII.GetBytes(
                $"{command}\nprintf '\\n{marker}%d\\n' $?\n");
            await stream.WriteAsync(bytes, 0, bytes.Length, ct);

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var buffer = new byte[8192];
            var timedOut = false;
            var exitCode = 0;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            try
            {
                while (true)
                {
                    var r = await stream.ReadOutputAsync(
                        buffer, 0, buffer.Length, linked.Token);
                    if (r.EOF)
                    {
                        break;
                    }

                    var chunk = Encoding.UTF8.GetString(buffer, 0, r.Count);
                    (r.Target == MultiplexedStream.TargetStream.StandardError
                        ? stderr : stdout).Append(chunk);
                    var s = stdout.ToString();
                    var idx = s.LastIndexOf(marker, StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        var code = s[(idx + marker.Length)..];
                        var nl = code.IndexOf('\n', StringComparison.Ordinal);
                        if (nl >= 0)
                        {
                            int.TryParse(code[..nl], out exitCode);
                            stdout.Remove(idx, s.Length - idx);
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = true;
            }

            return new CommandResult(exitCode, stdout.ToString(), stderr.ToString(), timedOut);
        }

        public async ValueTask DisposeAsync()
        {
            try { stream.CloseWrite(); }
            catch { /* stream já fechado */ }
            stream.Dispose();
        }
    }
}
