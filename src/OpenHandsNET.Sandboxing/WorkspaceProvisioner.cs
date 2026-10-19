using System.Diagnostics;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenHandsNET.Sandboxing;

/// <summary>Sandbox infrastructure options (<c>Sandbox</c> config section).</summary>
public sealed class SandboxOptions
{
    /// <summary>
    /// SPEC-35 RF-002: engine provider — <c>local</c> (Docker.DotNet in-process,
    /// default) ou <c>remote</c> (<c>openhandsnet-runtime-api</c> via HTTP).
    /// </summary>
    public string Provider { get; set; } = "local";

    /// <summary>
    /// SPEC-35: base URL do runtime-api remoto (ex.: <c>http://runtime-api:8090</c>).
    /// </summary>
    public string? RemoteBaseUrl { get; set; }

    /// <summary>
    /// SPEC-35: chave interna do runtime-api (<c>X-Runtime-Key</c>) — nunca
    /// uma <c>sk-*</c> de usuário; injetada por env.
    /// </summary>
    public string? RemoteApiKey { get; set; }

    /// <summary>
    /// SPEC-35: rede de controle compartilhada entre Api e sandboxes
    /// (modo split) — o agente é alcançado pelo IP nesta rede, sem publicar
    /// portas no host. Null = bridge IP atual.
    /// </summary>
    public string? ControlNetwork { get; set; }

    /// <summary>
    /// SPEC-35: host acessível para URLs de agente publicadas
    /// (<c>PublishAgentPort</c> — ex.: <c>host.docker.internal</c> no compose
    /// split). Null = <c>127.0.0.1</c>.
    /// </summary>
    public string? AgentHost { get; set; }

    /// <summary>
    /// SPEC-38: <c>internal</c> (default — comportamento atual) |
    /// <c>path</c> (proxy /sandbox/{id}/…) | <c>host</c>
    /// ({id}-{port}.{PublicBaseDomain}, wildcard DNS/TLS).
    /// </summary>
    public string RoutingMode { get; set; } = "internal";

    /// <summary>SPEC-38: domínio público wildcard p/ RoutingMode=host.</summary>
    public string? PublicBaseDomain { get; set; }

    /// <summary>
    /// SPEC-40 RF-004: segredo HMAC dos preview tokens de surfaces.
    /// Null → chave efêmera por boot (tokens morrem no restart).
    /// </summary>
    public string? PreviewTokenSecret { get; set; }

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

    /// <summary>
    /// Imagem do proxy de egress compartilhado (SPEC-19 RF-001) — pinada;
    /// troque também o digest documentado em docs/deployment/egress-allowlist.md.
    /// </summary>
    public string EgressProxyImage { get; set; } = "ubuntu/squid:6.6-24.04_beta";

    /// <summary>
    /// Diretório host onde o <see cref="SquidConfigWriter"/> materializa
    /// <c>squid.conf</c>/<c>allowlist.txt</c>. Default:
    /// <c>{WorkspaceRoot}/../egress</c>.
    /// </summary>
    public string? EgressConfigDir { get; set; }

    /// <summary>Warm pool size (SPEC-14 RF-005) — 0 disables.</summary>
    public int WarmPoolSize { get; set; }

    /// <summary>
    /// SPEC-16 RF-001: segundos de graça após o fim do run antes do
    /// <c>docker pause</c>. 0 = pausa imediata; -1 = desliga o auto-sleep
    /// (o timeout watcher continua pausando por <c>ExpiresAt</c>).
    /// </summary>
    public int IdleSleepSeconds { get; set; } = 30;

    /// <summary>
    /// SPEC-16 RF-003: horas em <c>Paused</c> antes do <c>docker stop</c>
    /// (libera RAM; workspace e container fs persistem). 0 = nunca stop.
    /// </summary>
    public double SleepStopAfterHours { get; set; }

    /// <summary>
    /// SPEC-17: prefixos de imagem que trazem o sandbox-agent como entrypoint
    /// (<c>;</c>-separados). Para elas o container não leva <c>Cmd=sleep</c>
    /// e recebe <c>OHN_SESSION_KEY</c>/<c>OHN_DEFERRED_INIT</c>.
    /// </summary>
    public string AgentImagePrefixes { get; set; } = "openhandsnet/runtime";

    /// <summary>
    /// SPEC-17: publica a porta 8000 do agente em <c>127.0.0.1:{port}</c>
    /// (Docker Desktop / daemon remoto). Default: reach via IP na bridge.
    /// </summary>
    public bool PublishAgentPort { get; set; }

    /// <summary>
    /// SPEC-17 RF-006: mapa <c>imagem→count</c> do warm pool
    /// (<c>imgA:2;imgB:1</c>). <see cref="WarmPoolSize"/> cobre a imagem default.
    /// </summary>
    public string? WarmPoolImages { get; set; }

    /// <summary>SPEC-17: timeout do blueprint (sdk installers + setup.sh).</summary>
    public int BlueprintTimeoutMinutes { get; set; } = 15;

    /// <summary>SPEC-17: detecta se a imagem embute o sandbox-agent.</summary>
    public bool IsAgentImage(string image) =>
        AgentImagePrefixes.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Any(p => image.Contains(p.Trim(), StringComparison.OrdinalIgnoreCase));
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
    /// SPEC-16 RF-006: removes a conversation workspace from the host —
    /// só quando a conversa é deletada/arquivada (container já removido).
    /// </summary>
    public void RemoveWorkspace(Guid conversationId)
    {
        var path = PreparePath(conversationId);
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                logger.LogInformation(
                    "Workspace {Path} removed with conversation {Id}",
                    path, conversationId);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Workspace {Path} cleanup failed", path);
        }
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


    private static string ResolveOnPath(string cmd)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH")
            ?? "").Split(Path.PathSeparator))
        {
            var full = Path.Combine(dir, cmd);
            if (File.Exists(full)) { return full; }
        }
        return cmd;
    }

    private static async Task RunGitAsync(
            string workdir, string? token, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo(ResolveOnPath("git"))
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