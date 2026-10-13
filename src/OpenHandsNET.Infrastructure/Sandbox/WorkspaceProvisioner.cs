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
    /// Clones <paramref name="repoUrl"/> into the workspace (shallow) if it is
    /// empty. Runs on the host so the sandbox sees the repo already mounted.
    /// </summary>
    public async Task CloneRepositoryAsync(
        Guid conversationId, string repoUrl, CancellationToken ct)
    {
        var path = await PrepareAsync(conversationId, ct);
        if (Directory.EnumerateFileSystemEntries(path).Any())
        {
            return; // workspace já populado — nunca sobrescreve trabalho existente
        }

        logger.LogInformation("Cloning {Repo} into workspace {Workspace}", repoUrl, path);
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = path,
        };
        psi.ArgumentList.Add("clone");
        psi.ArgumentList.Add("--depth=1");
        psi.ArgumentList.Add(repoUrl);
        psi.ArgumentList.Add(".");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("git not available on host");
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"git clone failed: {stderr.Trim()}");
        }
    }
}
