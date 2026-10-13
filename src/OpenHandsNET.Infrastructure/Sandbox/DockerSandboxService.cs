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
    IOptions<SandboxOptions> options,
    ILogger<DockerSandboxService> logger) : ISandboxService
{
    /// <summary>Label prefix on all managed containers.</summary>
    public const string LabelPrefix = "openhandsnet.";

    /// <inheritdoc />
    public async Task<DomainSandbox> StartAsync(
        Guid specId, Guid? conversationId, CancellationToken ct)
    {
        var spec = await GetSpecAsync(specId, ct)
            ?? throw new InvalidOperationException($"Sandbox spec {specId} not found");

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
            await EnsureImageAsync(spec.Image, ct);
            var binds = new List<string>();
            if (spec.MountWorkspace && conversationId is not null)
            {
                var hostPath = await workspaces.PrepareAsync(conversationId.Value, ct);
                binds.Add($"{hostPath}:{Domain.Sandbox.SandboxSpec.WorkspacePath}:rw");
            }

            var labels = new Dictionary<string, string>
            {
                [$"{LabelPrefix}managed"] = "true",
                [$"{LabelPrefix}sandbox_id"] = record.Id.ToString(),
            };
            if (conversationId is not null)
            {
                labels[$"{LabelPrefix}conversation_id"] = conversationId.Value.ToString();
            }

            var container = await docker.Containers.CreateContainerAsync(
                new CreateContainerParameters
                {
                    Image = spec.Image,
                    Name = $"ohn-sandbox-{record.Id:N}",
                    Labels = labels,
                    Env = spec.Env?.Select(kv => $"{kv.Key}={kv.Value}").ToList() ?? [],
                    HostConfig = new HostConfig
                    {
                        Memory = spec.MemoryMb * 1024L * 1024L,
                        NanoCPUs = (long)(spec.CpuLimit * 1_000_000_000L),
                        Binds = binds,
                        AutoRemove = false,
                    },
                    WorkingDir = Domain.Sandbox.SandboxSpec.WorkspacePath,
                    // Keep the sandbox alive; agent drives work via exec.
                    Cmd = ["sleep", "infinity"],
                    Tty = false,
                }, ct);

            await docker.Containers.StartContainerAsync(container.ID, null, ct);
            record.ContainerId = container.ID;
            record.Status = SandboxStatus.Running.ToString();
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Sandbox {SandboxId} running (container {ContainerId}, image {Image})",
                record.Id, container.ID[..12], spec.Image);
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

        var rows = await q.OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
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
            await docker.Containers.RemoveContainerAsync(
                c.ID, new ContainerRemoveParameters { Force = true }, ct);
        }
    }

    private async Task<SandboxRecord> RequireAsync(Guid id, CancellationToken ct) =>
        await db.Sandboxes.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Sandbox {id} not found");

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
