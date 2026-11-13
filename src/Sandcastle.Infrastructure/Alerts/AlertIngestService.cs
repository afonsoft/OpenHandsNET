using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Alerts;
using Sandcastle.Application.Contracts;
using Sandcastle.Application.Coordination;
using Sandcastle.Application.Identity;
using Sandcastle.Infrastructure.Agent;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Scm;

namespace Sandcastle.Infrastructure.Alerts;

/// <summary>
/// SPEC-151 RF-001 — pipeline inbound dos webhooks de alerta:
/// source lookup → signature verify (fail-closed) → rate limit por
/// source → normalização → dedup de entrega → upsert do evento →
/// routing (repo mapping) → auto-sessão. O endpoint autentica só por
/// signature — sem sessão de usuário.
/// </summary>
public sealed class AlertIngestService(
    AppDbContext db,
    ISecretStore secrets,
    IDistributedCounter counter,
    AlertSessionPipeline sessions,
    IAuditService audit,
    IOptions<AlertOptions> options,
    ILogger<AlertIngestService> logger) : IAlertIngestService
{
    /// <summary>Headers de delivery-id por provider (idempotência).</summary>
    private static readonly string[] DeliveryHeaders =
    [
        "X-Alert-Delivery", "X-Delivery-Id", "X-Request-Id",
        "Sentry-Hook-UUID",
    ];

    /// <inheritdoc />
    public async Task<AlertIngestResult> HandleAsync(
        Guid sourceId,
        IReadOnlyDictionary<string, string?> headers,
        byte[] payload,
        CancellationToken ct)
    {
        if (!options.Value.Enabled)
        {
            return AlertIngestResult.Ignored;
        }

        var source = await db.AlertSources.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source is null || !source.Enabled)
        {
            return AlertIngestResult.NoSource;
        }
        var provider = AlertProviderExtensions.Parse(source.Provider);
        if (provider is null)
        {
            return AlertIngestResult.NoSource;
        }

        // Signature verify — fail-closed antes de qualquer parse.
        var secret = await secrets.ResolveAsync(
            "org", source.OrgId?.ToString() ?? "", source.SecretRef, ct);
        if (!AlertSignatureVerifiers.Verify(
                provider.Value, secret, headers, payload))
        {
            await audit.RecordAsync("alert.webhook_rejected",
                orgId: source.OrgId, targetType: "alert_source",
                targetId: source.Id.ToString(),
                metadata: new { reason = "bad_signature" },
                cancellationToken: ct);
            return AlertIngestResult.InvalidSignature;
        }

        // Rate limit inbound por source (SPEC-151 NFR).
        if (await OverInboundLimitAsync(source.Id, ct))
        {
            await audit.RecordAsync("alert.webhook_rejected",
                orgId: source.OrgId, targetType: "alert_source",
                targetId: source.Id.ToString(),
                metadata: new { reason = "rate_limited" },
                cancellationToken: ct);
            return AlertIngestResult.RateLimited;
        }

        var alert = AlertNormalizer.Normalize(
            provider.Value, payload, DeliveryId(headers));
        if (alert is null)
        {
            await audit.RecordAsync("alert.payload_ignored",
                orgId: source.OrgId, targetType: "alert_source",
                targetId: source.Id.ToString(),
                metadata: new { reason = "not_an_alert" },
                cancellationToken: ct);
            return AlertIngestResult.Ignored;
        }

        // Idempotência de entrega (delivery id → payload sha fallback).
        var deliveryKey = alert.DeliveryId
            ?? "sha:" + Convert.ToHexStringLower(SHA256.HashData(payload));
        if (await IsDuplicateDeliveryAsync(
                provider.Value, $"{sourceId}:{deliveryKey}", payload, ct))
        {
            return AlertIngestResult.Duplicate;
        }

        // Dedup de evento + close-the-loop (resolved inbound).
        var existing = await db.AlertEvents.IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.SourceId == sourceId
                && e.ExternalId == alert.ExternalId, ct);
        if (existing is not null)
        {
            if (alert.Kind == AlertEventKinds.Resolved
                && existing.Status is not (
                    AlertEventStatuses.Resolved
                    or AlertEventStatuses.Dismissed))
            {
                existing.Status = AlertEventStatuses.Resolved;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                await audit.RecordAsync("alert.event_resolved",
                    orgId: source.OrgId, targetType: "alert_event",
                    targetId: existing.Id.ToString(),
                    metadata: new { external_id = alert.ExternalId },
                    cancellationToken: ct);
                return AlertIngestResult.Processed;
            }
            return AlertIngestResult.Duplicate;
        }

        var evt = NewRecord(source, provider.Value, alert);
        db.AlertEvents.Add(evt);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Corrida com mesmo (source, external_id) — duplicata.
            db.Entry(evt).State = EntityState.Detached;
            return AlertIngestResult.Duplicate;
        }

        await audit.RecordAsync("alert.event_ingested",
            orgId: source.OrgId, targetType: "alert_event",
            targetId: evt.Id.ToString(),
            metadata: new
            {
                provider = source.Provider,
                kind = evt.Kind,
                severity = evt.Severity,
                repository = evt.Repository,
                unrouted = evt.Repository is null,
            },
            cancellationToken: ct);

        // Auto-session: mapped + opt-in + não-resolved (severity/cap
        // dentro do pipeline).
        if (source.AutoCreateSession && evt.Repository is not null
            && evt.Kind != AlertEventKinds.Resolved)
        {
            var result = await sessions.TryCreateAsync(
                source, evt, actorUserId: null,
                applySeverityGate: true, ct);
            if (result.Error is { } err
                && err != AlertSessionError.BelowSeverity)
            {
                logger.LogInformation(
                    "alert auto-session skipped ({Err}) event {Id}",
                    err, evt.Id);
            }
        }

        return AlertIngestResult.Processed;
    }

    /// <summary>Rate limit por source via janela de minuto (counter dist.).</summary>
    private async Task<bool> OverInboundLimitAsync(
        Guid sourceId, CancellationToken ct)
    {
        var limit = options.Value.MaxInboundPerMin;
        if (limit <= 0)
        {
            return false;
        }
        var window = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmm");
        var count = await counter.IncrementAsync(
            $"alerts:inbound:{sourceId}:{window}",
            TimeSpan.FromSeconds(70), ct);
        return count > limit;
    }

    /// <summary>
    /// Idempotência por entrega — reusa <c>webhook_deliveries</c> com
    /// Kind <c>alerts-{provider}</c> (mesma tabela do SCM, SPEC-52).
    /// </summary>
    private async Task<bool> IsDuplicateDeliveryAsync(
        AlertProvider provider, string deliveryKey, byte[] payload,
        CancellationToken ct)
    {
        var kind = $"alerts-{provider.ToWire()}";
        var exists = await db.WebhookDeliveries.IgnoreQueryFilters()
            .AnyAsync(d => d.Kind == kind && d.DeliveryId == deliveryKey,
                ct);
        if (exists)
        {
            return true;
        }

        db.WebhookDeliveries.Add(new WebhookDeliveryRecord
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            DeliveryId = deliveryKey,
            PayloadSha = Convert.ToHexStringLower(SHA256.HashData(payload)),
            ProcessedAt = DateTimeOffset.UtcNow,
        });
        try
        {
            await db.SaveChangesAsync(ct);
            return false;
        }
        catch (DbUpdateException)
        {
            var local = db.WebhookDeliveries.Local
                .FirstOrDefault(d => d.DeliveryId == deliveryKey);
            if (local is not null)
            {
                db.Entry(local).State = EntityState.Detached;
            }
            return true;
        }
    }

    /// <summary>Header de delivery do provider (ou null).</summary>
    private static string? DeliveryId(
        IReadOnlyDictionary<string, string?> headers)
    {
        foreach (var name in DeliveryHeaders)
        {
            foreach (var (k, v) in headers)
            {
                if (string.Equals(k, name,
                        StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(v))
                {
                    return v;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Monta o record normalizado — campos untrusted passam por
    /// <see cref="SecretRedactor"/> antes de persistir (SPEC-151 NFR).
    /// </summary>
    internal static AlertEventRecord NewRecord(
        AlertSourceRecord source, AlertProvider provider,
        NormalizedAlert alert)
    {
        var labelsJson = alert.Labels is { Count: > 0 } labels
            ? JsonSerializer.Serialize(labels) : null;
        var now = DateTimeOffset.UtcNow;
        return new AlertEventRecord
        {
            Id = Guid.NewGuid(),
            OrgId = source.OrgId,
            SourceId = source.Id,
            Provider = provider.ToWire(),
            ExternalId = SecretRedactor.Redact(alert.ExternalId),
            Kind = alert.Kind,
            Title = SecretRedactor.Redact(alert.Title),
            Url = alert.Url is null ? null
                : SecretRedactor.Redact(alert.Url),
            Severity = alert.Severity,
            Service = alert.Service is null ? null
                : SecretRedactor.Redact(alert.Service),
            Culprit = alert.Culprit is null ? null
                : SecretRedactor.Redact(alert.Culprit),
            Repository = ResolveRepository(
                source.RepoMappingJson, alert),
            LabelsJson = labelsJson is null ? null
                : SecretRedactor.Redact(labelsJson),
            FirstSeen = alert.FirstSeen,
            Status = alert.Kind == AlertEventKinds.Resolved
                ? AlertEventStatuses.Resolved : AlertEventStatuses.New,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Repo mapping: <c>repo_mapping_json</c> = dict
    /// <c>{chave: "org/repo"}</c> — chaves testadas: routing hint
    /// (project/repository do payload) depois service.
    /// </summary>
    internal static string? ResolveRepository(
        string? repoMappingJson, NormalizedAlert alert)
    {
        if (string.IsNullOrWhiteSpace(repoMappingJson))
        {
            return null;
        }
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string,
                string>>(repoMappingJson);
            if (map is null)
            {
                return null;
            }
            foreach (var key in AlertNormalizer.RoutingKeys(alert))
            {
                foreach (var (k, v) in map)
                {
                    if (string.Equals(k, key,
                        StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(v))
                    {
                        return v.Trim();
                    }
                }
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}