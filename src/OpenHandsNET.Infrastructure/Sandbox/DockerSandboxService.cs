using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Sandbox;
using OpenHandsNET.Infrastructure.Persistence;
using DomainSandbox = OpenHandsNET.Domain.Sandbox.Sandbox;

namespace OpenHandsNET.Infrastructure.Sandbox;

/// <summary>
/// <see cref="ISandboxService"/> backed by the local Docker engine.
/// Pull → create (labels, limits, workspace bind) → start → Running.
/// </summary>
public sealed class DockerSandboxService(
    AppDbContext db,
    IDockerClient docker,
    WorkspaceProvisioner workspaces,
    DockerNetworkPolicy egress,
    WarmPoolService warmPool,
    IOptions<SandboxOptions> options,
    ILogger<DockerSandboxService> logger,
    Git.GitCredentialProvider? gitCredentials = null,
    Microsoft.AspNetCore.DataProtection.IDataProtectionProvider? dp = null,
    SandboxAgentClient? agentClient = null,
    EgressProxyService? egressProxy = null,
    Application.Contracts.ISecretStore? secretStore = null) : ISandboxService
{
    /// <summary>Protector da session key do agente in-container (SPEC-17).</summary>
    private Microsoft.AspNetCore.DataProtection.IDataProtector? SessionProtector =>
        dp?.CreateProtector("openhandsnet.sandbox.session");

    /// <summary>Gera ULID de session key, protege no record, devolve plaintext.</summary>
    private string? NewSessionKey(SandboxRecord record)
    {
        if (SessionProtector is null)
        {
            return null;
        }

        var key = Ulid.NewUlid().ToString();
        record.SessionKeyProtected = SessionProtector.Protect(key);
        return key;
    }

    /// <summary>Label prefix on all managed containers.</summary>
    public const string LabelPrefix = "openhandsnet.";

    /// <inheritdoc />
    public async Task<DomainSandbox> StartAsync(
        Guid specId, Guid? conversationId, CancellationToken ct)
    {
        using var span = Telemetry.OpenNetTelemetry.StartSpan(
            "sandbox_op", conversationId);
        span?.SetTag("op", "start");
        var spec = await GetSpecAsync(specId, ct)
            ?? throw new InvalidOperationException($"Sandbox spec {specId} not found");
        spec = await ApplyProfileAsync(spec, conversationId, ct);
        spec = await ApplyBlueprintOverridesAsync(spec, conversationId, ct);

        var record = new SandboxRecord
        {
            Id = Guid.NewGuid(), SpecId = specId, ConversationId = conversationId,
            Status = SandboxStatus.Starting.ToString(), CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(spec.TimeoutMinutes),
        };
        db.Sandboxes.Add(record);
        await db.SaveChangesAsync(ct);

        try
        {
            // Warm-pool claim: só para spec default (specId vazio) — specs
            // customizadas têm imagem/recursos próprios e não cabem no pool.
            // SPEC-19 RF-005: warm containers nascem sem proxy env — em modo
            // allowlist o claim é impossível (env é imutável) → cold start.
            var warmId = specId == Guid.Empty && spec.MountWorkspace
                && conversationId is not null
                && !egress.IsAllowlist
                ? await TryClaimWarmAsync(
                    record.Id, conversationId.Value, spec.Image, ct)
                : null;
            if (warmId is not null)
            {
                record.ContainerId = warmId;
                record.Pool = "warm";
                record.PoolKey = spec.Image;
                // SPEC-17 RF-007: container warm nasce dormant — claim ativa
                // via POST /api/init (bootstrap key na label do container).
                await ActivateWarmAsync(record, ct);
                record.Status = SandboxStatus.Running.ToString();
                await db.SaveChangesAsync(ct);
                logger.LogInformation(
                    "Sandbox {SandboxId} attached warm container {ContainerId}",
                    record.Id, warmId[..12]);
                return record.ToDomain();
            }

            await EnsureImageAsync(spec.Image, ct);
            var binds = new List<string>();
            if (spec.MountWorkspace && conversationId is not null)
            {
                var hostPath = await workspaces.PrepareAsync(conversationId.Value, ct);
                binds.Add($"{hostPath}:{Domain.Sandbox.SandboxSpec.WorkspacePath}:rw");
            }

            // DinD alternativa fraca: socket mount explícito (SPEC-14 RF-002).
            if (options.Value.DockerSocketMount && !options.Value.EnableDinD)
            {
                binds.Add("/var/run/docker.sock:/var/run/docker.sock");
            }

            var labels = new Dictionary<string, string>
            {
                [$"{LabelPrefix}managed"] = "true",
                [$"{LabelPrefix}owner"] = options.Value.InstanceOwner,
                [$"{LabelPrefix}sandbox_id"] = record.Id.ToString(),
            };
            if (conversationId is not null)
            {
                labels[$"{LabelPrefix}conversation_id"] = conversationId.Value.ToString();
            }

            var env = spec.Env?.Select(kv => $"{kv.Key}={kv.Value}").ToList() ?? [];
            env.AddRange(egress.EgressEnv());
            // SPEC-19 RF-003/004: allowlist → o sandbox fica na rede internal e
            // recebe o proxy compartilhado por env. Falha aqui aborta o start
            // (fail-closed — nunca sobe sandbox restrito sem enforcement).
            if (egress.IsAllowlist)
            {
                var net = await egress.EnsureNetworkAsync(ct)
                    ?? throw new InvalidOperationException(
                        "egress internal network unavailable");
                if (egressProxy is null)
                {
                    throw new InvalidOperationException(
                        "egress proxy service unavailable — allowlist mode " +
                        "cannot start sandboxes without enforcement");
                }

                await egressProxy.EnsureProxyAsync(net, ct);
                env.AddRange(EgressProxyService.ProxyEnv());
            }
            env.AddRange(await ResolveCredentialEnvAsync(conversationId, ct));

            // SPEC-17: imagem-agente não leva Cmd (entrypoint é o agente);
            // session key vai por env e fica protegida no record.
            var isAgentImage = options.Value.IsAgentImage(spec.Image);
            if (isAgentImage)
            {
                var sessionKey = NewSessionKey(record);
                if (sessionKey is not null)
                {
                    env.Add($"OHN_SESSION_KEY={sessionKey}");
                    env.Add("OHN_DEFERRED_INIT=false");
                    env.Add("OHN_WORKSPACE=/workspace");
                }
            }

            var hostConfig = new HostConfig
            {
                Memory = spec.MemoryMb * 1024L * 1024L,
                NanoCPUs = (long)(spec.CpuLimit * 1_000_000_000L),
                PidsLimit = spec.PidsLimit,
                Binds = binds,
                AutoRemove = false,
            };
            if (isAgentImage && options.Value.PublishAgentPort)
            {
                // Docker Desktop / daemon remoto: publica 8000 em 127.0.0.1.
                hostConfig.PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    ["8000/tcp"] = [new PortBinding
                    { HostIP = "127.0.0.1", HostPort = "" }],
                };
            }

            var createParams = new CreateContainerParameters
            {
                Image = spec.Image,
                Name = $"ohn-sandbox-{record.Id:N}",
                Labels = labels,
                Env = env,
                HostConfig = hostConfig,
                NetworkingConfig = await BuildNetworkingConfigAsync(ct),
                WorkingDir = Domain.Sandbox.SandboxSpec.WorkspacePath,
                Tty = false,
            };
            if (!isAgentImage)
            {
                // Imagem legada sem agente: mantém o container vivo para exec.
                createParams.Cmd = ["sleep", "infinity"];
            }

            var container = await docker.Containers.CreateContainerAsync(
                createParams, ct);

            await docker.Containers.StartContainerAsync(container.ID, null, ct);
            record.ContainerId = container.ID;

            // DinD opt-in (SPEC-14 RF-002): sidecar privileged compartilhando
            // o netns do sandbox → DOCKER_HOST=tcp://localhost:2375.
            if (options.Value.EnableDinD)
            {
                await StartDinDSidecarAsync(container.ID, ct);
            }

            // SPEC-17: registra a URL do agente para o executor HTTP.
            if (isAgentImage && agentClient is not null)
            {
                record.AgentUrl = (await agentClient.ResolveBaseUrlAsync(
                    container.ID, ct))?.ToString();
            }

            record.Status = SandboxStatus.Running.ToString();
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Sandbox {SandboxId} running (container {ContainerId}, image {Image}, profile {Profile})",
                record.Id, container.ID[..12], spec.Image,
                spec.Profile ?? "custom");
        }
        catch (Exception ex)
        {
            record.Status = SandboxStatus.Error.ToString();
            record.LastError = ex.Message;
            await db.SaveChangesAsync(ct);
            throw;
        }

        return record.ToDomain();
    }

    /// <inheritdoc />
    public async Task<DomainSandbox> PauseAsync(Guid sandboxId, CancellationToken ct)
    {
        var record = await RequireAsync(sandboxId, ct);
        if (record.ContainerId is not null)
        {
            try
            {
                await docker.Containers.PauseContainerAsync(
                    record.ContainerId, ct);
                record.Status = SandboxStatus.Paused.ToString();
            }
            catch (DockerApiException ex)
            {
                // SPEC-16 NFR: cgroup pause indisponível (rootless/v1) →
                // fallback stop preserva fs/workspace (processos morrem).
                logger.LogWarning(ex,
                    "docker pause failed for {ContainerId} — falling back to stop",
                    record.ContainerId);
                await docker.Containers.StopContainerAsync(
                    record.ContainerId, new ContainerStopParameters(), ct);
                record.Status = SandboxStatus.Stopped.ToString();
            }
        }
        else
        {
            record.Status = SandboxStatus.Paused.ToString();
        }

        Telemetry.OpenNetTelemetry.SandboxSleeps.Inc();
        await db.SaveChangesAsync(ct);
        return record.ToDomain();
    }

    /// <inheritdoc />
    public async Task<DomainSandbox> StopAsync(Guid sandboxId, CancellationToken ct)
    {
        var record = await RequireAsync(sandboxId, ct);
        if (record.ContainerId is not null &&
            record.Status is nameof(SandboxStatus.Running) or nameof(SandboxStatus.Paused))
        {
            try
            {
                if (record.Status == nameof(SandboxStatus.Paused))
                {
                    await docker.Containers.UnpauseContainerAsync(
                        record.ContainerId, ct);
                }

                await docker.Containers.StopContainerAsync(
                    record.ContainerId, new ContainerStopParameters(), ct);
                record.Status = SandboxStatus.Stopped.ToString();
                Telemetry.OpenNetTelemetry.SandboxSleeps.Inc();
            }
            catch (DockerContainerNotFoundException)
            {
                record.Status = SandboxStatus.Missing.ToString();
            }
        }

        await db.SaveChangesAsync(ct);
        return record.ToDomain();
    }

    /// <inheritdoc />
    public async Task<DomainSandbox> ResumeAsync(Guid sandboxId, CancellationToken ct)
    {
        var record = await RequireAsync(sandboxId, ct);
        var wakeFrom = record.Status == nameof(SandboxStatus.Stopped)
            ? "stop" : "pause";
        if (record.ContainerId is null)
        {
            record.Status = SandboxStatus.Missing.ToString();
        }
        else if (record.Status == nameof(SandboxStatus.Stopped))
        {
            // SPEC-16 RF-003: docker start + probe antes de declarar Running.
            try
            {
                await docker.Containers.StartContainerAsync(
                    record.ContainerId, null, ct);
                await docker.Containers.InspectContainerAsync(record.ContainerId, ct);
                record.Status = SandboxStatus.Running.ToString();
            }
            catch (DockerContainerNotFoundException)
            {
                record.Status = SandboxStatus.Missing.ToString();
            }
            catch (DockerApiException ex)
            {
                record.Status = SandboxStatus.Error.ToString();
                record.LastError = ex.Message;
            }
        }
        else
        {
            try
            {
                await docker.Containers.UnpauseContainerAsync(record.ContainerId, ct);
                record.Status = SandboxStatus.Running.ToString();
            }
            catch (DockerContainerNotFoundException)
            {
                record.Status = SandboxStatus.Missing.ToString();
            }
        }

        // SPEC-16 RF-004: wake é atividade — renova o lease do timeout watcher.
        if (record.Status == nameof(SandboxStatus.Running))
        {
            Telemetry.OpenNetTelemetry.SandboxWakes.WithLabels(wakeFrom).Inc();
            var spec = await GetSpecAsync(record.SpecId, ct);
            record.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(
                spec?.TimeoutMinutes ?? 60);
        }

        await db.SaveChangesAsync(ct);
        return record.ToDomain();
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid sandboxId, CancellationToken ct)
    {
        var record = await db.Sandboxes.FindAsync([sandboxId], ct);
        if (record is null)
        {
            return;
        }

        if (record.ContainerId is not null)
        {
            try
            {
                await docker.Containers.RemoveContainerAsync(
                    record.ContainerId,
                    new ContainerRemoveParameters { Force = true }, ct);
            }
            catch (DockerContainerNotFoundException)
            {
                // already gone — delete stays idempotent
            }
            catch (DockerApiException ex)
            {
                // daemon races (ex.: removal already in progress) não bloqueiam
                logger.LogWarning(ex,
                    "Container {ContainerId} removal reported an error (ignored)",
                    record.ContainerId);
            }
        }

        Telemetry.OpenNetTelemetry.SandboxSeconds
            .Inc((DateTimeOffset.UtcNow - record.CreatedAt).TotalSeconds);
        db.Sandboxes.Remove(record);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<DomainSandbox?> GetAsync(Guid sandboxId, CancellationToken ct)
    {
        var record = await db.Sandboxes.FindAsync([sandboxId], ct);
        if (record is null)
        {
            return null;
        }

        await ReconcileAsync(record, ct);
        return record.ToDomain();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DomainSandbox>> SearchAsync(
        SandboxStatus? status, Guid? conversationId, CancellationToken ct)
    {
        var q = db.Sandboxes.AsQueryable();
        if (status is not null)
        {
            var s = status.Value.ToString();
            q = q.Where(r => r.Status == s);
        }

        if (conversationId is not null)
        {
            q = q.Where(r => r.ConversationId == conversationId);
        }

        var rows = await q.OrderByDescending(r => r.CreatedAtTicks).ToListAsync(ct);
        foreach (var row in rows)
        {
            await ReconcileAsync(row, ct);
        }

        return rows.Select(r => r.ToDomain()).ToList();
    }

    /// <inheritdoc />
    public async Task<Domain.Sandbox.SandboxSpec> UpsertSpecAsync(
        Domain.Sandbox.SandboxSpec spec, CancellationToken ct)
    {
        var existing = await db.SandboxSpecs.FindAsync([spec.Id], ct);
        if (existing is null)
        {
            db.SandboxSpecs.Add(SandboxSpecRecord.From(spec));
        }
        else
        {
            var updated = SandboxSpecRecord.From(spec);
            db.Entry(existing).CurrentValues.SetValues(updated);
        }

        await db.SaveChangesAsync(ct);
        return spec;
    }

    /// <inheritdoc />
    public async Task<Domain.Sandbox.SandboxSpec?> GetSpecAsync(
        Guid specId, CancellationToken ct)
    {
        if (specId == Guid.Empty)
        {
            return new Domain.Sandbox.SandboxSpec(
                Guid.Empty, "default", options.Value.DefaultImage);
        }

        var record = await db.SandboxSpecs.FindAsync([specId], ct);
        return record?.ToDomain();
    }

    /// <summary>
    /// Removes managed containers whose records are gone/stale (boot sweep).
    /// </summary>
    public async Task SweepOrphansAsync(CancellationToken ct)
    {
        var containers = await docker.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool>
                    {
                        [$"{LabelPrefix}managed=true"] = true,
                        [$"{LabelPrefix}owner={options.Value.InstanceOwner}"] = true,
                    },
                },
            }, ct);

        var known = (await db.Sandboxes
            .Select(r => r.ContainerId)
            .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var c in containers)
        {
            if (known.Contains(c.ID))
            {
                continue;
            }

            logger.LogWarning("Removing orphan sandbox container {ContainerId}", c.ID[..12]);
            try
            {
                await docker.Containers.RemoveContainerAsync(
                    c.ID, new ContainerRemoveParameters { Force = true }, ct);
            }
            catch (DockerContainerNotFoundException)
            {
                // already gone
            }
            catch (DockerApiException ex)
            {
                // daemon races (ex.: removal already in progress) não bloqueiam o sweep
                logger.LogWarning(ex,
                    "Orphan container {ContainerId} removal reported an error (ignored)",
                    c.ID[..12]);
            }
        }
    }

    /// <summary>
    /// Profile resolution (SPEC-14 RF-001): spec.Profile → conversation →
    /// org settings → global default (default spec only). A resolved profile
    /// overrides the resource fields; custom specs keep their explicit
    /// values unless a profile is explicitly requested.
    /// </summary>
    private async Task<Domain.Sandbox.SandboxSpec> ApplyProfileAsync(
        Domain.Sandbox.SandboxSpec spec, Guid? conversationId, CancellationToken ct)
    {
        var name = spec.Profile;
        if (name is null && conversationId is not null)
        {
            var conv = await db.Conversations
                .AsNoTracking()
                .Where(c => c.Id == conversationId)
                .Select(c => new { c.SandboxProfile, c.OrgId })
                .FirstOrDefaultAsync(ct);
            name = conv?.SandboxProfile;
            if (name is null && conv?.OrgId is not null)
            {
                var orgJson = await db.Organizations
                    .AsNoTracking()
                    .Where(o => o.Id == conv.OrgId)
                    .Select(o => o.SettingsJson)
                    .FirstOrDefaultAsync(ct);
                name = ReadOrgSandboxProfile(orgJson);
            }
        }
        // Global default only shapes the built-in spec — a custom spec's
        // explicit resources are intentional and stay untouched.
        name ??= spec.Id == Guid.Empty ? options.Value.Profile : null;
        if (name is null)
        {
            return spec;
        }

        var profile = SandboxProfiles.Resolve(name);
        if (profile is null)
        {
            logger.LogWarning("Unknown sandbox profile '{Profile}' — keeping spec resources", name);
            return spec;
        }

        return spec with
        {
            MemoryMb = profile.MemoryMb,
            CpuLimit = profile.CpuLimit,
            PidsLimit = profile.PidsLimit,
            Profile = name,
        };
    }

    private static string? ReadOrgSandboxProfile(string? settingsJson)
    {
        if (settingsJson is null)
        {
            return null;
        }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(settingsJson);
            return doc.RootElement.TryGetProperty("sandbox_profile", out var p)
                && p.ValueKind == System.Text.Json.JsonValueKind.String
                    ? p.GetString() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Claims a warm container: moves its pre-mounted workspace dir to the
    /// conversation path (rename keeps the bind target) and returns the id.
    /// </summary>
    private async Task<string?> TryClaimWarmAsync(
        Guid sandboxId, Guid conversationId, string image, CancellationToken ct)
    {
        var containerId = await warmPool.TryClaimAsync(image, ct);
        if (containerId is null)
        {
            return null;
        }

        try
        {
            var inspect = await docker.Containers.InspectContainerAsync(containerId, ct);
            var wsMount = inspect.Mounts.FirstOrDefault(m =>
                m.Destination == Domain.Sandbox.SandboxSpec.WorkspacePath);
            if (wsMount is not null)
            {
                var target = workspaces.PreparePath(conversationId);
                if (!Directory.Exists(target))
                {
                    Directory.Move(wsMount.Source, target);
                }
                else
                {
                    Directory.Delete(wsMount.Source, recursive: true);
                }
            }
            await workspaces.PrepareAsync(conversationId, ct);
            return containerId;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e,
                "Warm claim of {ContainerId} failed — falling back to cold start",
                containerId[..12]);
            try
            {
                await docker.Containers.RemoveContainerAsync(containerId,
                    new ContainerRemoveParameters { Force = true }, ct);
            }
            catch (DockerApiException)
            {
                // best-effort cleanup; pool prune/sweep picks leftovers
            }
            return null;
        }
    }

    /// <summary>
    /// SPEC-17 RF-004: aplica <c>image</c>/<c>env</c> resolvidos pelo blueprint
    /// de um sandbox anterior da mesma conversa (repo persistido no workspace).
    /// </summary>
    private async Task<Domain.Sandbox.SandboxSpec> ApplyBlueprintOverridesAsync(
        Domain.Sandbox.SandboxSpec spec, Guid? conversationId,
        CancellationToken ct)
    {
        if (conversationId is null)
        {
            return spec;
        }

        var prior = await db.Sandboxes
            .Where(s => s.ConversationId == conversationId
                && (s.BlueprintImage != null || s.BlueprintEnvJson != null))
            .OrderByDescending(s => s.CreatedAtTicks)
            .Select(s => new { s.BlueprintImage, s.BlueprintEnvJson })
            .FirstOrDefaultAsync(ct);
        if (prior is null)
        {
            return spec;
        }

        var env = spec.Env is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(spec.Env);
        if (prior.BlueprintEnvJson is not null)
        {
            try
            {
                foreach (var kv in System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, string>>(
                        prior.BlueprintEnvJson) ?? [])
                {
                    env[kv.Key] = kv.Value;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // blueprint env corrompido — ignora
            }
        }

        return spec with
        {
            Image = prior.BlueprintImage ?? spec.Image,
            Env = env,
        };
    }

    /// <summary>
    /// SPEC-17 RF-007: ativa um container warm dormant — lê a init key da
    /// label <c>openhandsnet.init_key</c>, gera session key nova e chama
    /// <c>POST /api/init</c>. Falha/legado sem agente → segue em exec-fallback.
    /// </summary>
    private async Task ActivateWarmAsync(SandboxRecord record, CancellationToken ct)
    {
        if (agentClient is null || record.ContainerId is null)
        {
            return;
        }

        try
        {
            var inspect = await docker.Containers
                .InspectContainerAsync(record.ContainerId, ct);
            var initKey = inspect.Config?.Labels is { } l
                && l.TryGetValue($"{LabelPrefix}init_key", out var k) ? k : null;
            var baseUrl = await agentClient.ResolveBaseUrlAsync(
                record.ContainerId, ct);
            if (initKey is null || baseUrl is null)
            {
                return; // imagem legada no pool — sem agente, segue exec
            }

            var sessionKey = NewSessionKey(record);
            if (sessionKey is null)
            {
                return;
            }

            var ok = await agentClient.InitAsync(baseUrl, initKey, sessionKey,
                Domain.Sandbox.SandboxSpec.WorkspacePath, null, ct);
            if (ok)
            {
                record.AgentUrl = baseUrl.ToString();
            }
            else
            {
                logger.LogWarning(
                    "warm container {ContainerId} /api/init failed — exec fallback",
                    record.ContainerId[..12]);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e,
                "warm activation failed for {ContainerId} — exec fallback",
                record.ContainerId[..12]);
        }
    }

    /// <summary>Egress network wiring: internal network for deny/allowlist.</summary>
    private async Task<NetworkingConfig?> BuildNetworkingConfigAsync(CancellationToken ct)
    {
        var net = await egress.EnsureNetworkAsync(ct);
        return net is null ? null : new NetworkingConfig
        {
            EndpointsConfig = new Dictionary<string, EndpointSettings>
            {
                [net] = new(),
            },
        };
    }

    /// <summary>
    /// DinD sidecar sharing the sandbox net namespace (SPEC-14 RF-002):
    /// privileged <c>docker:dind</c> + shared workspace bind; the sandbox
    /// reaches the daemon via <c>DOCKER_HOST=tcp://localhost:2375</c>.
    /// </summary>
    private async Task StartDinDSidecarAsync(string sandboxContainerId, CancellationToken ct)
    {
        const string dindImage = "docker:27-dind";
        await EnsureImageAsync(dindImage, ct);
        var inspect = await docker.Containers.InspectContainerAsync(sandboxContainerId, ct);
        var wsMount = inspect.Mounts.FirstOrDefault(m =>
            m.Destination == Domain.Sandbox.SandboxSpec.WorkspacePath);
        var binds = wsMount is null ? []
            : new List<string>
            {
                $"{wsMount.Source}:{Domain.Sandbox.SandboxSpec.WorkspacePath}:rw",
            };
        var sidecar = await docker.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Image = dindImage,
                Name = $"ohn-dind-{sandboxContainerId[..12]}",
                Labels = new Dictionary<string, string>
                {
                    [$"{LabelPrefix}managed"] = "true",
                    [$"{LabelPrefix}owner"] = options.Value.InstanceOwner,
                    [$"{LabelPrefix}dind_for"] = sandboxContainerId,
                },
                HostConfig = new HostConfig
                {
                    Privileged = true,
                    Binds = binds,
                    NetworkMode = $"container:{sandboxContainerId}",
                    AutoRemove = true,
                },
                Env = ["DOCKER_TLS_CERTDIR="],
            }, ct);
        await docker.Containers.StartContainerAsync(sidecar.ID, null, ct);
        logger.LogInformation("DinD sidecar {SidecarId} for sandbox {ContainerId}",
            sidecar.ID[..12], sandboxContainerId[..12]);
    }

    private async Task<SandboxRecord> RequireAsync(Guid id, CancellationToken ct) =>
        await db.Sandboxes.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Sandbox {id} not found");

    /// <summary>
    /// SPEC-15 RF-004: injeta <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c> (github.com) ou
    /// <c>GH_ENTERPRISE_TOKEN</c>+<c>GH_HOST</c> (GHES) no container quando o
    /// dono da conversa (ou o escopo <c>system</c>) tem credencial git para o
    /// host. Env do container = mesmo domínio de confiança do askpass.
    /// </summary>
    private async Task<List<string>> ResolveCredentialEnvAsync(
        Guid? conversationId, CancellationToken ct)
    {
        if (conversationId is null
            || (gitCredentials is null && secretStore is null))
        {
            return [];
        }

        var conv = await db.Conversations.AsNoTracking()
            .Where(c => c.Id == conversationId.Value)
            .Select(c => new
            {
                c.CreatedByUserId, c.SelectedRepository, c.SecretNamesJson,
            })
            .FirstOrDefaultAsync(ct);
        if (conv is null)
        {
            return [];
        }

        var env = new List<string>();

        // SPEC-33 RF-003: secrets por referência — conv.secret_names_json
        // guarda NOMES; o valor só existe aqui (env do container).
        if (secretStore is not null
            && !string.IsNullOrEmpty(conv.SecretNamesJson)
            && !string.IsNullOrEmpty(conv.CreatedByUserId))
        {
            try
            {
                var names = System.Text.Json.JsonSerializer
                    .Deserialize<List<string>>(conv.SecretNamesJson) ?? [];
                foreach (var name in names)
                {
                    var value = await secretStore.ResolveAsync(
                        "user", conv.CreatedByUserId, name, ct);
                    if (value is not null)
                    {
                        env.Add($"{name}={value}");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Secret env resolution failed for conversation {Id}",
                    conversationId);
            }
        }

        if (gitCredentials is null)
        {
            return env;
        }

        var ghToken = await gitCredentials.ResolveTokenAsync(
            "github.com", conv.CreatedByUserId, ct);
        if (ghToken is not null)
        {
            env.Add($"GH_TOKEN={ghToken}");
            env.Add($"GITHUB_TOKEN={ghToken}");
        }

        var repoHost = TryRepoHost(conv.SelectedRepository);
        if (repoHost is not null &&
            !repoHost.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var entToken = await gitCredentials.ResolveTokenAsync(
                repoHost, conv.CreatedByUserId, ct);
            if (entToken is not null)
            {
                env.Add($"GH_ENTERPRISE_TOKEN={entToken}");
                env.Add($"GH_HOST={repoHost}");
            }
        }

        return env;
    }

    /// <summary>Extrai o host de <c>selected_repository</c> (url ou owner/repo).</summary>
    private static string? TryRepoHost(string? selectedRepository)
    {
        if (string.IsNullOrEmpty(selectedRepository))
        {
            return null;
        }

        if (Uri.TryCreate(selectedRepository, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return selectedRepository.Count(c => c == '/') == 1
            && !selectedRepository.StartsWith('/')
            && !selectedRepository.StartsWith("file:", StringComparison.Ordinal)
            ? "github.com"
            : null;
    }

    private async Task ReconcileAsync(SandboxRecord record, CancellationToken ct)
    {
        if (record.Status is nameof(SandboxStatus.Error) or nameof(SandboxStatus.Missing)
            || record.ContainerId is null)
        {
            return;
        }

        try
        {
            var inspect = await docker.Containers.InspectContainerAsync(
                record.ContainerId, ct);
            // SPEC-16 RF-005: "exited"/"created" é o estado *esperado* de um
            // record Stopped (docker stop preserva o container); só é Error
            // quando o record dizia que estava vivo.
            var status = inspect.State?.Status switch
            {
                "running" => SandboxStatus.Running,
                "paused" => SandboxStatus.Paused,
                "created" => record.Status == nameof(SandboxStatus.Starting)
                    ? SandboxStatus.Starting : SandboxStatus.Stopped,
                "exited" or "dead" => record.Status == nameof(SandboxStatus.Stopped)
                    ? SandboxStatus.Stopped : SandboxStatus.Error,
                _ => SandboxStatus.Error,
            };
            if (status.ToString() != record.Status)
            {
                record.Status = status.ToString();
                if (status is SandboxStatus.Error)
                {
                    record.LastError = inspect.State?.Error;
                }

                await db.SaveChangesAsync(ct);
            }
        }
        catch (DockerContainerNotFoundException)
        {
            record.Status = SandboxStatus.Missing.ToString();
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task EnsureImageAsync(string image, CancellationToken ct)
    {
        try
        {
            await docker.Images.InspectImageAsync(image, ct);
        }
        catch (DockerImageNotFoundException)
        {
            var (name, tag) = image.Split(':') is [var n, var t] ? (n, t) : (image, "latest");
            logger.LogInformation("Pulling sandbox image {Image}…", image);
            await docker.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = name, Tag = tag },
                null, new Progress<JSONMessage>(), ct);
        }
    }
}
