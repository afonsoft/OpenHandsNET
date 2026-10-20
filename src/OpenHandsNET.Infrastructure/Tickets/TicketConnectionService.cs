using Microsoft.EntityFrameworkCore;

using OpenHandsNET.Application.Identity;
using OpenHandsNET.Application.Tickets;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Tickets;

/// <summary>CRUD de ticket_connections + project configs (SPEC-55).</summary>
public sealed class TicketConnectionService(
    AppDbContext db, IAuditService audit) : ITicketConnectionService
{
    /// <inheritdoc />
    public async Task<TicketConnection> CreateAsync(
        Guid orgId, TicketKind kind, string apiUrl,
        string tokenSecretRef, string? webhookSecretRef,
        string? workspaceId, string? triggerConfigJson,
        string createdByUserId, CancellationToken ct)
    {
        var rec = new TicketConnectionRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Kind = kind.ToWire(),
            ApiUrl = apiUrl.TrimEnd('/'),
            TokenSecretRef = tokenSecretRef,
            WebhookSecretRef = webhookSecretRef,
            WorkspaceId = workspaceId,
            TriggerConfigJson = triggerConfigJson,
            CreatedByUserId = createdByUserId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.TicketConnections.Add(rec);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("ticket.connection_created",
            actorUserId: createdByUserId, orgId: orgId,
            targetType: "ticket_connection", targetId: rec.Id.ToString(),
            metadata: new { kind = kind.ToWire() }, cancellationToken: ct);
        return rec.ToModel();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TicketConnection>> ListAsync(
        Guid orgId, CancellationToken ct) =>
        (await db.TicketConnections.AsNoTracking()
            .Where(c => c.OrgId == orgId).ToListAsync(ct))
            .Select(c => c.ToModel()).ToList();

    /// <inheritdoc />
    public async Task<TicketConnection?> GetAsync(
        Guid id, CancellationToken ct) =>
        (await db.TicketConnections.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == id, ct))?.ToModel();

    /// <inheritdoc />
    public async Task<TicketConnection?> FindForWebhookAsync(
        TicketKind kind, string? workspaceId, CancellationToken ct)
    {
        var wire = kind.ToWire();
        var q = db.TicketConnections.IgnoreQueryFilters()
            .Where(c => c.Kind == wire);
        if (workspaceId is not null)
        {
            var exact = await q
                .FirstOrDefaultAsync(c => c.WorkspaceId == workspaceId, ct);
            if (exact is not null)
            {
                return exact.ToModel();
            }
        }
        // Fallback: única connection do kind (self-host típico).
        var all = await q.ToListAsync(ct);
        return all.Count == 1 ? all[0].ToModel() : null;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        Guid id, Guid orgId, CancellationToken ct)
    {
        var rec = await db.TicketConnections.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == id && c.OrgId == orgId, ct);
        if (rec is null)
        {
            return false;
        }
        db.TicketConnections.Remove(rec);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("ticket.connection_deleted",
            orgId: rec.OrgId, targetType: "ticket_connection",
            targetId: id.ToString(), cancellationToken: ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<TicketProjectConfig?> SetProjectConfigAsync(
        Guid connectionId, Guid orgId, string projectKey,
        string? repository, string? branch, CancellationToken ct)
    {
        if (!await db.TicketConnections.IgnoreQueryFilters()
                .AnyAsync(c => c.Id == connectionId && c.OrgId == orgId,
                    ct))
        {
            return null;
        }
        var rec = await db.TicketProjectConfigs
            .FirstOrDefaultAsync(p => p.ConnectionId == connectionId
                && p.ProjectKey == projectKey, ct);
        if (rec is null)
        {
            rec = new TicketProjectConfigRecord
            {
                Id = Guid.NewGuid(),
                ConnectionId = connectionId,
                ProjectKey = projectKey,
            };
            db.TicketProjectConfigs.Add(rec);
        }
        rec.SelectedRepository = repository;
        rec.SelectedBranch = branch;
        await db.SaveChangesAsync(ct);
        return rec.ToModel();
    }

    /// <inheritdoc />
    public async Task<TicketProjectConfig?> GetProjectConfigAsync(
        Guid connectionId, string projectKey, CancellationToken ct) =>
        (await db.TicketProjectConfigs.AsNoTracking()
            .FirstOrDefaultAsync(p => p.ConnectionId == connectionId
                && p.ProjectKey == projectKey, ct))?.ToModel();

    /// <inheritdoc />
    public async Task<IReadOnlyList<TicketProjectConfig>?>
        ListProjectConfigsAsync(
            Guid connectionId, Guid orgId, CancellationToken ct)
    {
        var owns = await db.TicketConnections.IgnoreQueryFilters()
            .AnyAsync(c => c.Id == connectionId && c.OrgId == orgId, ct);
        return owns
            ? (await db.TicketProjectConfigs.AsNoTracking()
                .Where(p => p.ConnectionId == connectionId)
                .ToListAsync(ct)).Select(p => p.ToModel()).ToList()
            : null;
    }
}