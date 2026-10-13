using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Sandbox;

/// <summary>
/// Warm pool (SPEC-14 RF-005, SPEC-17 RF-006/007): keeps pre-created,
/// running containers labelled <c>openhandsnet.pool=warm</c> —
/// <b>por imagem</b>: <c>Sandbox:WarmPoolSize</c> cobre a imagem default e
/// <c>Sandbox:WarmPoolImages</c> (<c>img:count;…</c>) adiciona pools extras
/// (<c>openhandsnet.pool_key=&lt;image&gt;</c>). Containers de imagem-agente
/// nascem <b>dormant</b> (<c>OHN_DEFERRED_INIT=true</c> +
/// <c>OHN_INIT_API_KEY</c> na label <c>openhandsnet.init_key</c>) e são
/// ativados no claim via <c>POST /api/init</c> (paridade deferred-init
/// upstream); imagens legadas seguem <c>sleep infinity</c>.
/// Because Docker labels are immutable post-create, a claimed warm container
/// keeps the label — liveness is decided by the DB: a warm container
/// referenced by a sandbox row is <b>claimed</b>; one referenced by nothing
/// is a <b>free slot</b>. Each warm container pre-mounts a disposable
/// workspace dir; on claim the dir is renamed to the conversation path,
/// which preserves the bind target.
/// </summary>
public sealed class WarmPoolService(
    IDockerClient docker,
    IOptions<SandboxOptions> options,
    WorkspaceProvisioner workspaces,
    IServiceScopeFactory scopes,
    ILogger<WarmPoolService> logger) : BackgroundService
{
    /// <summary>Label marking a pool container.</summary>
    public const string PoolLabel = $"{DockerSandboxService.LabelPrefix}pool";

    /// <summary>Label com a imagem-pool (<c>pool_key</c> do sandbox record).</summary>
    public const string PoolKeyLabel = $"{DockerSandboxService.LabelPrefix}pool_key";

    /// <summary>Label com a init key bootstrap de containers dormant.</summary>
    public const string InitKeyLabel = $"{DockerSandboxService.LabelPrefix}init_key";

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var targets = PoolTargets();
        if (targets.Count == 0)
        {
            return;
        }

        // Restart semantics: pool containers are pruned, never resumed —
        // anything warm from a previous run is orphaned by definition.
        await PrunePoolContainersAsync(stoppingToken);

        try
        {
            await EnsurePoolSizeAsync(stoppingToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "warm pool initial fill failed");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await EnsurePoolSizeAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "warm pool replenish failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown — StopAsync prunes free pool containers
        }
    }

    /// <summary>
    /// Shutdown cleanup: unclaimed pool containers are always pruned so a
    /// restart never leaves silent orphans (SPEC-14 RF-005); claimed ones
    /// keep serving their sandboxes.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (PoolTargets().Count > 0)
        {
            try
            {
                await PrunePoolContainersAsync(cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogDebug(e, "warm pool shutdown prune skipped");
            }
        }
        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Claim a free warm container of <paramref name="image"/> for a
    /// conversation (null = pool miss → caller creates cold). A claimed
    /// container id is written into the sandbox row by the caller — the pool
    /// then replenishes on next tick.
    /// </summary>
    public async Task<string?> TryClaimAsync(string image, CancellationToken ct)
    {
        if (!PoolTargets().ContainsKey(image))
        {
            return null;
        }

        var claimedIds = await ClaimedContainerIdsAsync(ct);
        var warm = await ListWarmContainersAsync(ct);
        var free = warm.FirstOrDefault(c => !claimedIds.Contains(c.ID)
            && PoolKeyOf(c) == image);
        return free?.ID;
    }

    /// <summary>Current count of free warm slots for <paramref name="image"/>.</summary>
    public async Task<int> FreeCountAsync(string image, CancellationToken ct)
    {
        var claimedIds = await ClaimedContainerIdsAsync(ct);
        var warm = await ListWarmContainersAsync(ct);
        return warm.Count(c => !claimedIds.Contains(c.ID)
            && PoolKeyOf(c) == image);
    }

    /// <summary>
    /// Mapa <c>image → count</c>: <see cref="SandboxOptions.WarmPoolSize"/>
    /// cobre a imagem default; <see cref="SandboxOptions.WarmPoolImages"/>
    /// adiciona pools por imagem (<c>imgA:2;imgB:1</c>).
    /// </summary>
    internal IReadOnlyDictionary<string, int> PoolTargets()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (options.Value.WarmPoolSize > 0)
        {
            map[options.Value.DefaultImage] = options.Value.WarmPoolSize;
        }

        foreach (var part in (options.Value.WarmPoolImages ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = part.LastIndexOf(':');
            if (colon > 0 && int.TryParse(part[(colon + 1)..], out var n) && n > 0)
            {
                map[part[..colon].Trim()] = n;
            }
        }

        return map;
    }

    private static string PoolKeyOf(ContainerListResponse c) =>
        c.Labels is not null && c.Labels.TryGetValue(PoolKeyLabel, out var k)
            ? k : c.Image;

    private async Task EnsurePoolSizeAsync(CancellationToken ct)
    {
        foreach (var (image, target) in PoolTargets())
        {
            var free = await FreeCountAsync(image, ct);
            for (var i = free; i < target; i++)
            {
                await CreateWarmContainerAsync(image, ct);
            }
        }
    }

    private async Task CreateWarmContainerAsync(string image, CancellationToken ct)
    {
        var spec = await DefaultSpecAsync(ct);
        var isAgentImage = options.Value.IsAgentImage(image);
        var labels = new Dictionary<string, string>
        {
            [$"{DockerSandboxService.LabelPrefix}managed"] = "true",
            [$"{DockerSandboxService.LabelPrefix}owner"] =
                options.Value.InstanceOwner,
            [PoolLabel] = "warm",
            [PoolKeyLabel] = image,
        };

        var env = new List<string>();
        if (isAgentImage)
        {
            // SPEC-17 RF-007: nasce dormant — claim ativa via /api/init.
            var initKey = Ulid.NewUlid().ToString();
            labels[InitKeyLabel] = initKey;
            env.Add("OHN_DEFERRED_INIT=true");
            env.Add($"OHN_INIT_API_KEY={initKey}");
            env.Add("OHN_WORKSPACE=/workspace");
        }

        // Workspace bind pré-montado num dir descartável: no claim o dir é
        // renomeado para o path da conversa (rename preserva o bind target).
        var warmWs = workspaces.PrepareWarmPath();
        var createParams = new CreateContainerParameters
        {
            Image = image,
            Labels = labels,
            Env = env,
            HostConfig = new HostConfig
            {
                Memory = spec.MemoryMb * 1024L * 1024L,
                NanoCPUs = (long)(spec.CpuLimit * 1_000_000_000L),
                PidsLimit = spec.PidsLimit,
                Binds =
                [
                    $"{warmWs}:{Domain.Sandbox.SandboxSpec.WorkspacePath}:rw",
                ],
                AutoRemove = false,
            },
            WorkingDir = Domain.Sandbox.SandboxSpec.WorkspacePath,
        };
        if (!isAgentImage)
        {
            createParams.Cmd = ["sleep", "infinity"];
        }

        var created = await docker.Containers.CreateContainerAsync(
            createParams, ct);
        await docker.Containers.StartContainerAsync(created.ID, null, ct);
        logger.LogInformation(
            "warm pool container {ContainerId} ready (image {Image}, dormant {Dormant})",
            created.ID[..12], image, isAgentImage);
    }

    private async Task<IReadOnlyList<ContainerListResponse>> ListWarmContainersAsync(
        CancellationToken ct) =>
        [.. await docker.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool>
                    {
                        [$"{DockerSandboxService.LabelPrefix}managed=true"] = true,
                        [$"{DockerSandboxService.LabelPrefix}owner={options.Value.InstanceOwner}"] = true,
                        [$"{PoolLabel}=warm"] = true,
                    },
                },
            }, ct)];

    private async Task<HashSet<string>> ClaimedContainerIdsAsync(
        CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.Sandboxes
                .Where(s => s.ContainerId != null)
                .Select(s => s.ContainerId!)
                .ToListAsync(ct))
            .ToHashSet();
    }

    private async Task<Domain.Sandbox.SandboxSpec> DefaultSpecAsync(CancellationToken ct)
    {
        var profile = Domain.Sandbox.SandboxProfiles
            .Resolve(options.Value.Profile)
            ?? Domain.Sandbox.SandboxProfiles.Resolve(
                Domain.Sandbox.SandboxProfiles.Default)!;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var specRecord = await db.SandboxSpecs
            .OrderBy(s => s.Name).FirstOrDefaultAsync(ct);
        return specRecord?.ToDomain() ?? new Domain.Sandbox.SandboxSpec(
            Guid.Empty, "default", options.Value.DefaultImage,
            profile.MemoryMb, profile.CpuLimit,
            PidsLimit: profile.PidsLimit);
    }

    private async Task PrunePoolContainersAsync(CancellationToken ct)
    {
        var warm = await ListWarmContainersAsync(ct);
        var claimed = await ClaimedContainerIdsAsync(ct);
        foreach (var c in warm.Where(c => !claimed.Contains(c.ID)))
        {
            try
            {
                await docker.Containers.RemoveContainerAsync(
                    c.ID, new ContainerRemoveParameters { Force = true }, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogDebug(e, "warm pool prune of {ContainerId} skipped", c.ID[..12]);
            }
        }
    }
}
