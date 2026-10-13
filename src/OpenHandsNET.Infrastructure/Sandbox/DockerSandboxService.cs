using Docker.DotNet;
using Docker.DotNet.Models;
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
    Git.GitCredentialProvider? gitCredentials = null) : ISandboxService
{
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
            var warmId = specId == Guid.Empty && spec.MountWorkspace
                && conversationId is not null
                ? await TryClaimWarmAsync(record.Id, conversationId.Value, ct)
                : null;
            if (warmId is not null)
            {
                record.ContainerId = warmId;
                record.Pool = "warm";
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
            env.AddRange(await ResolveCredentialEnvAsync(conversationId, ct));

            var container = await docker.Containers.CreateContainerAsync(
                new CreateContainerParameters
                {
                    Image = spec.Image,
                    Name = $"ohn-sandbox-{record.Id:N}",
                    Labels = labels,
                    Env = env,
                    HostConfig = new HostConfig
                    {
                        Memory = spec.MemoryMb * 1024L * 1024L,
                        NanoCPUs = (long)(spec.CpuLimit * 1_000_000_000L),
                        PidsLimit = spec.PidsLimit,
                        Binds = binds,
                        AutoRemove = false,
                    },
                    NetworkingConfig = await BuildNetworkingConfigAsync(ct),
                    WorkingDir = Domain.Sandbox.SandboxSpec.WorkspacePath,
                    // Keep the sandbox alive; agent drives work via exec.
                    Cmd = ["sleep", "infinity"],
                    Tty = false,
                }, ct);

            await docker.Containers.StartContainerAsync(container.ID, null, ct);
            record.ContainerId = container.ID;

            // DinD opt-in (SPEC-14 RF-002): sidecar privileged compartilhando
            // o netns do sandbox → DOCKER_HOST=tcp://localhost:2375.
            if (options.Value.EnableDinD)
            {
                await StartDinDSidecarAsync(container.ID, ct);
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
            await docker.Containers.PauseContainerAsync(record.ContainerId, ct);
        }

        record.Status = SandboxStatus.Paused.ToString();
        await db.SaveChangesAsync(ct);
        return record.ToDomain();
    }

    /// <inheritdoc />
    public async Task<DomainSandbox> ResumeAsync(Guid sandboxId, CancellationToken ct)
    {
        var record = await RequireAsync(sandboxId, ct);
        if (record.ContainerId is null)
        {
            record.Status = SandboxStatus.Missing.ToString();
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
        Guid sandboxId, Guid conversationId, CancellationToken ct)
    {
        var containerId = await warmPool.TryClaimAsync(ct);
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
        if (conversationId is null || gitCredentials is null)
        {
            return [];
        }

        var conv = await db.Conversations.AsNoTracking()
            .Where(c => c.Id == conversationId.Value)
            .Select(c => new { c.CreatedByUserId, c.SelectedRepository })
            .FirstOrDefaultAsync(ct);
        if (conv is null)
        {
            return [];
        }

        var env = new List<string>();
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
        if (record.Status is nameof(SandboxStatus.Paused) or nameof(SandboxStatus.Error)
            || record.ContainerId is null)
        {
            return;
        }

        try
        {
            var inspect = await docker.Containers.InspectContainerAsync(
                record.ContainerId, ct);
            var status = inspect.State?.Status switch
            {
                "running" => SandboxStatus.Running,
                "paused" => SandboxStatus.Paused,
                "exited" or "dead" => SandboxStatus.Error,
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
