using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenHandsNET.Infrastructure.Sandbox;

/// <summary>Sandbox infrastructure options (<c>Sandbox</c> config section).</summary>
public sealed class SandboxOptions
{
    /// <summary>Host root for per-conversation workspaces (bind-mounted).</summary>
    public string WorkspaceRoot { get; set; } = "data/workspaces";

    /// <summary>Default sandbox image when a spec does not set one.</summary>
    public string DefaultImage { get; set; } =
        OpenHandsNET.Domain.Sandbox.SandboxSpec.DefaultImage;

    /// <summary>
    /// Identificador de instância gravado na label <c>openhandsnet.owner</c>.
    /// O orphan sweep só remove containers do próprio owner — evita que
    /// réplicas/instâncias distintas no mesmo daemon destruam sandboxes
    /// umas das outras. Default estável garante crash recovery single-node.
    /// </summary>
    public string InstanceOwner { get; set; } = "default";
}

/// <summary>
/// Prepares per-conversation workspaces on the host for bind-mounting into
/// sandboxes, including cloning <c>selected_repository</c> before first use.
/// </summary>
public sealed class WorkspaceProvisioner(
    IOptions<SandboxOptions> options, ILogger<WorkspaceProvisioner> logger)
{
    /// <summary>Creates (or reuses) <c>{WorkspaceRoot}/{conversationId}</c>.</summary>
    public Task<string> PrepareAsync(Guid conversationId, CancellationToken ct)
    {
        var path = Path.Combine(options.Value.WorkspaceRoot, conversationId.ToString("N"));
        Directory.CreateDirectory(path);
        return Task.FromResult(Path.GetFullPath(path));
    }

    /// <summary>
    /// Clones <paramref name="repoUrl"/> into <c>{workspace}/repo</c> and creates
    /// the work branch. Host clone mode (SPEC-12): menos isolado — dev/air-gapped.
    /// </summary>
    public async Task CloneRepositoryAsync(
        Guid conversationId, string repoUrl, string workBranch, CancellationToken ct)
    {
        var workspace = await PrepareAsync(conversationId, ct);
        var repoDir = Path.Combine(workspace, "repo");
        if (Directory.Exists(repoDir) &&
            Directory.EnumerateFileSystemEntries(repoDir).Any())
        {
            return; // repo já existe — nunca sobrescreve trabalho existente
        }

        logger.LogInformation("Cloning {Repo} into workspace {Workspace} (host mode)",
            repoUrl, repoDir);
        await RunGitAsync(workspace, ct, "clone", repoUrl, repoDir);
        await RunGitAsync(repoDir, ct, "checkout", "-b", workBranch);
    }

    private static async Task RunGitAsync(
        string workdir, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workdir,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("git not available on host");
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {args[0]} failed: {stderr.Trim()}");
        }
    }
}
