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

    /// <summary>Global default sandbox profile (SPEC-14 RF-001).</summary>
    public string Profile { get; set; } =
        OpenHandsNET.Domain.Sandbox.SandboxProfiles.Default;

    /// <summary>Docker-in-sandbox opt-in (SPEC-14 RF-002).</summary>
    public bool EnableDinD { get; set; }

    /// <summary>
    /// Weaker-isolation alternative to DinD: bind-mounts the host docker
    /// socket into the sandbox. Ignored when <see cref="EnableDinD"/> is on.
    /// </summary>
    public bool DockerSocketMount { get; set; }

    /// <summary>Egress policy: <c>unrestricted</c> | <c>allowlist</c> | <c>deny</c>.</summary>
    public string EgressMode { get; set; } = "unrestricted";

    /// <summary>Allowlist hosts/CIDRs, <c>;</c>-separated (egress=allowlist).</summary>
    public string? EgressAllow { get; set; }

    /// <summary>Warm pool size (SPEC-14 RF-005) — 0 disables.</summary>
    public int WarmPoolSize { get; set; }
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
    /// Full host path for a conversation workspace — no side effects
    /// (SPEC-14 RF-005 warm-claim uses it as the Directory.Move target).
    /// </summary>
    public string PreparePath(Guid conversationId) =>
        Path.GetFullPath(Path.Combine(
            options.Value.WorkspaceRoot, conversationId.ToString("N")));

    /// <summary>
    /// Creates a fresh warm-pool workspace dir under
    /// <c>{WorkspaceRoot}/.warm/</c> and returns its full path.
    /// </summary>
    public string PrepareWarmPath()
    {
        var dir = Path.Combine(
            options.Value.WorkspaceRoot, ".warm", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.GetFullPath(dir);
    }

    /// <summary>
    /// Clones <paramref name="repoUrl"/> into <c>{workspace}/repo</c> and creates
    /// the work branch. Host clone mode (SPEC-12): menos isolado — dev/air-gapped.
    /// SPEC-15 RF-004: <paramref name="token"/> autentica via
    /// <c>http.extraHeader</c> em <c>GIT_CONFIG_*</c> env — nunca em argv/ps.
    /// </summary>
    public async Task CloneRepositoryAsync(
        Guid conversationId, string repoUrl, string workBranch,
        string? token, CancellationToken ct)
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
        await RunGitAsync(workspace, token, ct, "clone", repoUrl, repoDir);
        await RunGitAsync(repoDir, token, ct, "checkout", "-b", workBranch);
    }

    private static async Task RunGitAsync(
        string workdir, string? token, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workdir,
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (token is not null)
        {
            // http.extraHeader via GIT_CONFIG_* env — credencial não aparece
            // em argv/ps nem em .git/config.
            var basic = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"oauth2:{token}"));
            psi.Environment["GIT_CONFIG_COUNT"] = "1";
            psi.Environment["GIT_CONFIG_KEY_0"] = "http.extraHeader";
            psi.Environment["GIT_CONFIG_VALUE_0"] = $"Authorization: Basic {basic}";
        }

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
