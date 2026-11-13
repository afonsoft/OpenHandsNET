using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Alerts;
using Sandcastle.Application.Identity;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Alerts;

/// <summary>
/// CRUD de <see cref="AlertSourceRecord"/> + listagem/ações de
/// <see cref="AlertEventRecord"/> (SPEC-151 RF-001/RF-004). Eventos
/// de auditoria <c>alert.*</c> em todas as mutações.
/// </summary>
public sealed class AlertService(
    AppDbContext db, IAuditService audit,
    AlertSessionPipeline sessions) : IAlertService
{
    /// <inheritdoc />
    public async Task<AlertSource> CreateSourceAsync(
        Guid orgId, AlertProvider provider, string name, string secretRef,
        IReadOnlyDictionary<string, string>? repoMapping,
        bool enabled, bool autoCreateSession, string triageSeverityMin,
        string? createdByUserId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new AlertSourceRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Provider = provider.ToWire(),
            Name = name.Trim(),
            SecretRef = secretRef.Trim(),
            RepoMappingJson = SerializeMapping(repoMapping),
            Enabled = enabled,
            AutoCreateSession = autoCreateSession,
            TriageSeverityMin = triageSeverityMin,
            CreatedByUserId = createdByUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AlertSources.Add(record);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("alert.source_added",
            actorUserId: createdByUserId, orgId: orgId,
            targetType: "alert_source", targetId: record.Id.ToString(),
            metadata: new { provider = provider.ToWire(), name },
            cancellationToken: ct);
        return ToModel(record);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AlertSource>> ListSourcesAsync(
        Guid orgId, CancellationToken ct) =>
        // SQLite não ordena DateTimeOffset — fontes são poucas,
        // ordena em memória.
        (await db.AlertSources.IgnoreQueryFilters()
            .Where(s => s.OrgId == orgId)
            .ToListAsync(ct))
            .OrderByDescending(s => s.CreatedAt)
            .Select(ToModel).ToList();

    /// <inheritdoc />
    public async Task<AlertSource?> GetSourceAsync(
        Guid id, Guid orgId, CancellationToken ct)
    {
        var r = await db.AlertSources.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == id && s.OrgId == orgId, ct);
        return r is null ? null : ToModel(r);
    }

    /// <inheritdoc />
    public async Task<AlertSource?> UpdateSourceAsync(
        Guid id, Guid orgId, string? name, string? secretRef,
        IReadOnlyDictionary<string, string>? repoMapping,
        bool? enabled, bool? autoCreateSession,
        string? triageSeverityMin, CancellationToken ct)
    {
        var r = await db.AlertSources.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == id && s.OrgId == orgId, ct);
        if (r is null)
        {
            return null;
        }
        if (name is not null)
        {
            r.Name = name.Trim();
        }
        if (secretRef is not null)
        {
            r.SecretRef = secretRef.Trim();
        }
        if (repoMapping is not null)
        {
            r.RepoMappingJson = SerializeMapping(repoMapping);
        }
        if (enabled is { } en)
        {
            r.Enabled = en;
        }
        if (autoCreateSession is { } ac)
        {
            r.AutoCreateSession = ac;
        }
        if (triageSeverityMin is { } sev)
        {
            r.TriageSeverityMin = sev;
        }
        r.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("alert.source_updated",
            orgId: orgId, targetType: "alert_source",
            targetId: r.Id.ToString(), cancellationToken: ct);
        return ToModel(r);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteSourceAsync(
        Guid id, Guid orgId, CancellationToken ct)
    {
        var r = await db.AlertSources.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == id && s.OrgId == orgId, ct);
        if (r is null)
        {
            return false;
        }
        db.AlertSources.Remove(r);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("alert.source_deleted",
            orgId: orgId, targetType: "alert_source",
            targetId: id.ToString(), cancellationToken: ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<AlertEventPage> SearchEventsAsync(
        Guid orgId, AlertEventQuery query, CancellationToken ct)
    {
        var q = db.AlertEvents.IgnoreQueryFilters()
            .Where(e => e.OrgId == orgId);
        if (query.SourceId is { } sid)
        {
            q = q.Where(e => e.SourceId == sid);
        }
        if (query.Provider is { } prov)
        {
            q = q.Where(e => e.Provider == prov);
        }
        if (query.Severity is { } sev)
        {
            q = q.Where(e => e.Severity == sev);
        }
        if (query.Repository is { } repo)
        {
            q = q.Where(e => e.Repository == repo);
        }
        if (query.Unrouted)
        {
            q = q.Where(e => e.Repository == null);
        }
        if (query.Status is { } st)
        {
            q = q.Where(e => e.Status == st);
        }

        var limit = Math.Clamp(query.Limit, 1, 200);
        q = q.OrderByDescending(e => e.CreatedAtTicks)
            .ThenByDescending(e => e.Id);
        if (CursorPaginator.TryDecode(
                query.PageId, out var ticks, out var id)
            && Guid.TryParse(id, out var gid))
        {
            q = q.Where(e => e.CreatedAtTicks < ticks
                || (e.CreatedAtTicks == ticks
                    && e.Id.CompareTo(gid) < 0));
        }

        var rows = await q.Take(limit + 1).ToListAsync(ct);
        string? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            var last = rows[^1];
            next = CursorPaginator.Encode(
                last.CreatedAtTicks, last.Id.ToString());
        }

        return new AlertEventPage(
            rows.Select(ToModel).ToList(), next);
    }

    /// <inheritdoc />
    public async Task<AlertEvent?> GetEventAsync(
        Guid id, Guid orgId, CancellationToken ct)
    {
        var r = await db.AlertEvents.IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.Id == id && e.OrgId == orgId, ct);
        return r is null ? null : ToModel(r);
    }

    /// <inheritdoc />
    public async Task<AlertEvent?> UpdateEventAsync(
        Guid id, Guid orgId, string? status, string? repository,
        CancellationToken ct)
    {
        var r = await db.AlertEvents.IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.Id == id && e.OrgId == orgId, ct);
        if (r is null)
        {
            return null;
        }
        if (status is not null)
        {
            r.Status = status;
            await audit.RecordAsync($"alert.event_{status}",
                orgId: orgId, targetType: "alert_event",
                targetId: r.Id.ToString(), cancellationToken: ct);
        }
        if (repository is not null)
        {
            r.Repository = repository.Trim();
            await audit.RecordAsync("alert.event_routed",
                orgId: orgId, targetType: "alert_event",
                targetId: r.Id.ToString(),
                metadata: new { repository = r.Repository },
                cancellationToken: ct);
        }
        r.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToModel(r);
    }

    /// <inheritdoc />
    public async Task<AlertSessionResult> CreateSessionAsync(
        Guid id, Guid orgId, string actorUserId, CancellationToken ct)
    {
        var evt = await db.AlertEvents.IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.Id == id && e.OrgId == orgId, ct);
        if (evt is null)
        {
            return AlertSessionResult.Fail(AlertSessionError.NotFound);
        }
        var source = await db.AlertSources.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == evt.SourceId, ct);
        if (source is null)
        {
            return AlertSessionResult.Fail(AlertSessionError.NotFound);
        }

        var actor = Guid.TryParse(actorUserId, out var g) ? g : (Guid?)null;
        // Path manual explícito pula o severity gate (a triagem já foi
        // decidida pelo humano); dedup/cap/routing seguem iguais.
        return await sessions.TryCreateAsync(
            source, evt, actor, applySeverityGate: false, ct);
    }

    internal static AlertSource ToModel(AlertSourceRecord r) => new(
        r.Id, r.OrgId,
        AlertProviderExtensions.Parse(r.Provider) ?? AlertProvider.Generic,
        r.Name, r.SecretRef, ParseMapping(r.RepoMappingJson),
        r.Enabled, r.AutoCreateSession, r.TriageSeverityMin,
        r.CreatedByUserId, r.CreatedAt, r.UpdatedAt);

    internal static AlertEvent ToModel(AlertEventRecord r) => new(
        r.Id, r.OrgId, r.SourceId,
        AlertProviderExtensions.Parse(r.Provider) ?? AlertProvider.Generic,
        r.ExternalId, r.Kind, r.Title, r.Url, r.Severity, r.Service,
        r.Culprit, r.Repository, r.LabelsJson, r.FirstSeen, r.Status,
        r.SessionId, r.PrUrl, r.CreatedAt, r.UpdatedAt);

    private static string? SerializeMapping(
        IReadOnlyDictionary<string, string>? mapping)
    {
        if (mapping is not { Count: > 0 })
        {
            return null;
        }
        var clean = mapping
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key)
                && !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value.Trim());
        return clean.Count == 0 ? null : JsonSerializer.Serialize(clean);
    }

    private static IReadOnlyDictionary<string, string> ParseMapping(
        string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>();
        }
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string,
                string>>(json) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}