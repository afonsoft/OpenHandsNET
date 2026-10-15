using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Coordination;
using OpenHandsNET.Application.Sandbox;

namespace OpenHandsNET.Sandboxing;

/// <summary>
/// Warm pool filler (SPEC-14 RF-005, SPEC-17 RF-006/007, SPEC-35): keeps
/// pre-created running containers named <c>ohn-pool-*</c> labelled
/// <c>openhandsnet.pool=warm</c> — <b>por imagem</b>
/// (<c>Sandbox:WarmPoolSize</c> cobre a imagem default,
/// <c>Sandbox:WarmPoolImages</c> adiciona <c>img:count;…</c> extras via
/// <c>openhandsnet.pool_key</c>). Containers de imagem-agente nascem
/// <b>dormant</b> (<c>OHN_DEFERRED_INIT=true</c> + init key na label
/// <c>openhandsnet.init_key</c>) e são ativados no claim via
/// <c>POST /api/init</c> (paridade deferred-init upstream).
/// <para>
/// SPEC-35: stateless — nenhum EF. Claimed = container renomeado para
/// <c>ohn-sandbox-*</c> (feito por <see cref="ISandboxEngine.ClaimWarmAsync"/>);
/// free = prefixo <c>ohn-pool-</c>. Hosted onde o engine é local (Api em
/// <c>Provider=local</c>; runtime-api sempre — cada runtime mantém o pool
/// do próprio daemon).
/// </para>
/// <para>
/// SPEC-36 RF-002: multi-réplica compartilhando o mesmo daemon → leader
/// election via <see cref="IDistributedLock"/> (<c>warmpool-leader</c>);
/// só o leader preenche/pruna, standby só observa e assume se o lease
/// cair. Single-replica usa <see cref="NullDistributedLock"/>.
/// </para>
/// </summary>
public sealed class WarmPoolService(
    ISandboxEngine engine,
    IOptions<SandboxOptions> options,
    IDistributedLock distributedLock,
    ILogger<WarmPoolService> logger) : BackgroundService
{
    private IDistributedLockHandle? _leader;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var targets = PoolTargets();
        if (targets.Count == 0 || engine.IsRemote)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            // SPEC-36: leader único enche/poda o pool compartilhado.
            _leader = await distributedLock.TryAcquireAsync(
                "warmpool-leader", TimeSpan.FromMinutes(1), stoppingToken);
            if (_leader is null)
            {
                // standby — retenta até o lease do líder expirar.
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                continue;
            }

            try
            {
                await RunLeaderLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                // shutdown — StopAsync prunes free pool containers
            }
            finally
            {
                var l = _leader;
                _leader = null;
                if (l is not null)
                {
                    await l.DisposeAsync();
                }
            }
        }
    }

    private async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
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
        while (_leader?.IsHeld == true &&
               await timer.WaitForNextTickAsync(stoppingToken))
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

    /// <summary>
    /// Shutdown cleanup: unclaimed pool containers are always pruned so a
    /// restart never leaves silent orphans (SPEC-14 RF-005); claimed ones
    /// (renamed <c>ohn-sandbox-*</c>) keep serving their sandboxes.
    /// SPEC-36: só o nó que detém o lease poda — um standby que morre não
    /// destrói o pool do líder no daemon compartilhado.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (PoolTargets().Count > 0 && !engine.IsRemote
            && _leader?.IsHeld == true)
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

    /// <summary>Free pool slots serving <paramref name="image"/> (tests).</summary>
    public async Task<int> FreeCountAsync(string image, CancellationToken ct) =>
        (await engine.ListPoolAsync(ct)).Count(c => PoolKeyOf(c)
            .Equals(image, StringComparison.OrdinalIgnoreCase));

    private async Task EnsurePoolSizeAsync(CancellationToken ct)
    {
        if (PoolTargets().Count == 0)
        {
            return;
        }

        var free = await engine.ListPoolAsync(ct);
        foreach (var (image, target) in PoolTargets())
        {
            var have = free.Count(c => PoolKeyOf(c)
                .Equals(image, StringComparison.OrdinalIgnoreCase));
            for (var i = have; i < target; i++)
            {
                await CreateWarmContainerAsync(image, ct);
            }
        }
    }

    private static string PoolKeyOf(EngineSandboxInfo c) =>
        c.Labels.TryGetValue(EngineLabels.PoolKey, out var k) ? k : "";

    private async Task CreateWarmContainerAsync(string image, CancellationToken ct)
    {
        var isAgentImage = options.Value.IsAgentImage(image);
        var profile = Domain.Sandbox.SandboxProfiles
            .Resolve(options.Value.Profile)
            ?? Domain.Sandbox.SandboxProfiles.Resolve(
                Domain.Sandbox.SandboxProfiles.Default)!;

        var env = new List<string>();
        var labels = new Dictionary<string, string>
        {
            [EngineLabels.Pool] = "warm",
            [EngineLabels.PoolKey] = image,
        };
        if (isAgentImage)
        {
            // SPEC-17 RF-007: nasce dormant — claim ativa via /api/init.
            var initKey = Ulid.NewUlid().ToString();
            labels[EngineLabels.InitKey] = initKey;
            env.Add("OHN_DEFERRED_INIT=true");
            env.Add($"OHN_INIT_API_KEY={initKey}");
            env.Add("OHN_WORKSPACE=/workspace");
        }

        await engine.CreateAndStartAsync(new EngineCreateRequest(
            Name: $"{EngineLabels.PoolNamePrefix}{Guid.NewGuid():N}",
            Image: image,
            Env: env,
            Binds: [],
            Labels: labels,
            MemoryBytes: profile.MemoryMb * 1024L * 1024L,
            NanoCpus: (long)(profile.CpuLimit * 1_000_000_000L),
            PidsLimit: profile.PidsLimit,
            Cmd: isAgentImage ? null : ["sleep", "infinity"],
            WorkingDir: Domain.Sandbox.SandboxSpec.WorkspacePath,
            IsAgentImage: isAgentImage,
            // Workspace pré-montado num dir descartável — o claim renomeia
            // para o path da conversa (rename preserva o bind target).
            WorkspaceConversationId: null,
            WarmWorkspace: true), ct);
        logger.LogInformation(
            "warm pool container ready (image {Image}, dormant {Dormant})",
            image, isAgentImage);
    }

    private async Task PrunePoolContainersAsync(CancellationToken ct)
    {
        foreach (var c in await engine.ListPoolAsync(ct))
        {
            try
            {
                await engine.RemoveAsync(c.ContainerId, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogDebug(e,
                    "warm pool prune of {ContainerId} skipped",
                    c.ContainerId[..12]);
            }
        }
    }
}
