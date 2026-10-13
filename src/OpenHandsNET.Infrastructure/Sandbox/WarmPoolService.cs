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
/// Warm pool (SPEC-14 RF-005): keeps <c>Sandbox:WarmPoolSize</c> pre-created,
/// running containers labelled <c>openhandsnet.pool=warm</c>. Because Docker
/// labels are immutable post-create, a claimed warm container keeps the label
/// — liveness is decided by the DB: a warm container referenced by a sandbox
/// row is <b>claimed</b>; one referenced by nothing is a <b>free slot</b>.
/// Each warm container pre-mounts a disposable workspace dir; on claim the
/// dir is renamed to the conversation path, which preserves the bind target.
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

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.WarmPoolSize <= 0)
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
        if (options.Value.WarmPoolSize > 0)
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
    /// Claim a free warm container for a conversation (null = pool miss →
    /// caller creates cold). A claimed container id is written into the
    /// sandbox row by the caller — the pool then replenishes on next tick.
    /// </summary>
    public async Task<string?> TryClaimAsync(CancellationToken ct)
    {
        if (options.Value.WarmPoolSize <= 0)
        {
            return null;
        }

        var claimedIds = await ClaimedContainerIdsAsync(ct);
        var warm = await ListWarmContainersAsync(ct);
        var free = warm.FirstOrDefault(c => !claimedIds.Contains(c.ID));
        return free?.ID;
    }

    /// <summary>Current count of free warm slots.</summary>
    public async Task<int> FreeCountAsync(CancellationToken ct)
    {
        var claimedIds = await ClaimedContainerIdsAsync(ct);
        var warm = await ListWarmContainersAsync(ct);
        return warm.Count(c => !claimedIds.Contains(c.ID));
    }

    private async Task EnsurePoolSizeAsync(CancellationToken ct)
    {
        var target = options.Value.WarmPoolSize;
        var free = await FreeCountAsync(ct);
        for (var i = free; i < target; i++)
        {
            await CreateWarmContainerAsync(ct);
        }
    }

    private async Task CreateWarmContainerAsync(CancellationToken ct)
    {
        var spec = await DefaultSpecAsync(ct);
        var labels = new Dictionary<string, string>
        {
            [$"{DockerSandboxService.LabelPrefix}managed"] = "true",
            [$"{DockerSandboxService.LabelPrefix}owner"] =
                options.Value.InstanceOwner,
            [PoolLabel] = "warm",
        };
        // Workspace bind pré-montado num dir descartável: no claim o dir é
        // renomeado para o path da conversa (rename preserva o bind target).
        var warmWs = workspaces.PrepareWarmPath();
        var created = await docker.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Image = spec.Image,
                Labels = labels,
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
                Cmd = ["sleep", "infinity"],
            }, ct);
        await docker.Containers.StartContainerAsync(created.ID, null, ct);
        logger.LogInformation("warm pool container {ContainerId} ready", created.ID[..12]);
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
