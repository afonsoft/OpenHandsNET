using System.Diagnostics;
using System.Text;

namespace Sandcastle.Cli.Services;

/// <summary>
/// Wrapper testável sobre invocações locais de <c>git</c> — os comandos
/// <c>handoff</c>/<c>pickup</c> falam só com esta interface (SPEC-150).
/// </summary>
public interface IGitRunner
{
    /// <summary>Roda <c>git {args}</c> no cwd; retorna exit/stdout/stderr.</summary>
    Task<GitResult> RunAsync(string args, CancellationToken ct = default);

    /// <summary>Roda git com stdin (ex.: <c>apply</c> lendo patch).</summary>
    Task<GitResult> RunWithInputAsync(
        string args, string input, CancellationToken ct = default);

    /// <summary>Cwd de trabalho (root do repo).</summary>
    string WorkDir { get; }
}

/// <summary>Resultado de uma invocação git.</summary>
public sealed record GitResult(int ExitCode, string StdOut, string StdErr);

/// <summary><see cref="IGitRunner"/> real — processo git no cwd.</summary>
public sealed class GitRunner(string? workDir = null) : IGitRunner
{
    /// <inheritdoc />
    public string WorkDir { get; } = workDir ?? Directory.GetCurrentDirectory();

    /// <inheritdoc />
    public Task<GitResult> RunAsync(string args, CancellationToken ct = default) =>
        ExecAsync(args, null, ct);

    /// <inheritdoc />
    public Task<GitResult> RunWithInputAsync(
        string args, string input, CancellationToken ct = default) =>
        ExecAsync(args, input, ct);

    private async Task<GitResult> ExecAsync(
        string args, string? input, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = WorkDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("git não encontrado no PATH");
        if (input is not null)
        {
            await p.StandardInput.WriteAsync(
                input.AsMemory(), ct);
            p.StandardInput.Close();
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        await Task.WhenAll(
            DrainAsync(p.StandardOutput, stdout, ct),
            DrainAsync(p.StandardError, stderr, ct));
        await p.WaitForExitAsync(ct);
        return new GitResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static async Task DrainAsync(
        StreamReader reader, StringBuilder sb, CancellationToken ct)
    {
        var buf = new char[8192];
        int n;
        while ((n = await reader.ReadAsync(buf.AsMemory(), ct)) > 0)
        {
            sb.Append(buf, 0, n);
        }
    }
}