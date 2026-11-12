using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Identity;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Agent.Routing;

/// <summary>
/// SPEC-149: <see cref="IRoutingRulesService"/> sobre
/// <c>organizations.settings_json</c> (kv
/// <see cref="IRoutingRulesService.SettingsKey"/>). Leitura falha aberta
/// (documento corrompido ⇒ null ⇒ pass-through); escrita valida o
/// schema <c>routing_rules_v1</c> e audita
/// <c>org.routing_rules.update</c>.
/// </summary>
public sealed class RoutingRulesService(
    AppDbContext db,
    IAuditService? audit,
    ILogger<RoutingRulesService> logger) : IRoutingRulesService
{
    /// <inheritdoc />
    public async Task<RoutingRulesDocument?> GetAsync(
        Guid? orgId, CancellationToken ct)
    {
        if (orgId is not { } oid)
        {
            return null;
        }

        var settingsJson = await db.Organizations.AsNoTracking()
            .Where(o => o.Id == oid)
            .Select(o => o.SettingsJson)
            .FirstOrDefaultAsync(ct);
        var raw = ReadKey(settingsJson);
        if (raw is null)
        {
            return null;
        }

        RoutingRulesDocument? doc;
        try
        {
            doc = RoutingRulesDocument.Parse(raw);
        }
        catch (RoutingRulesException e)
        {
            // Falha aberta: org com documento corrompido não pode ficar
            // sem LLM — loga e cai no pass-through.
            logger.LogWarning(
                "routing_rules corrupt for org {Org}: {Err} — pass-through",
                oid, e.Message);
            return null;
        }

        return doc is { IsEmpty: false } ? doc : null;
    }

    /// <inheritdoc />
    public async Task SetAsync(
        Guid orgId, RoutingRulesDocument? doc, string? actorUserId,
        CancellationToken ct)
    {
        var org = await db.Organizations
            .FirstOrDefaultAsync(o => o.Id == orgId, ct);
        if (org is null)
        {
            throw new RoutingRulesException("organization not found");
        }

        if (doc is not null)
        {
            RoutingRulesDocument.Validate(doc);
        }

        var settings = ReadAll(org.SettingsJson);
        if (doc is null or { IsEmpty: true })
        {
            settings.Remove(IRoutingRulesService.SettingsKey);
        }
        else
        {
            settings[IRoutingRulesService.SettingsKey] = doc.ToJson();
        }

        org.SettingsJson = settings.Count > 0
            ? JsonSerializer.Serialize(settings)
            : null;
        await db.SaveChangesAsync(ct);

        if (audit is not null)
        {
            await audit.RecordAsync(
                "org.routing_rules.update", actorUserId, orgId,
                targetType: "routing_rules", targetId: orgId.ToString(),
                metadata: new
                {
                    cleared = doc is null or { IsEmpty: true },
                    roles = doc?.Rules.Keys.OrderBy(k => k).ToArray()
                        ?? [],
                    allowed = doc?.AllowedModels.Count ?? 0,
                    denied = doc?.DenyModels.Count ?? 0,
                },
                cancellationToken: ct);
        }
    }

    /// <inheritdoc />
    public async Task<LlmRouteContext?> ContextForConversationAsync(
        Guid conversationId, CancellationToken ct)
    {
        var conv = await db.Conversations.AsNoTracking()
            .Where(c => c.Id == conversationId)
            .Select(c => new { c.OrgId, c.CreatedByUserId, c.LlmModel })
            .FirstOrDefaultAsync(ct);
        return conv is null
            ? null
            : new LlmRouteContext(conversationId, conv.OrgId,
                conv.CreatedByUserId, conv.LlmModel);
    }

    private static string? ReadKey(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return null;
        }

        try
        {
            var map = JsonSerializer
                .Deserialize<Dictionary<string, string>>(settingsJson);
            return map is not null
                && map.TryGetValue(IRoutingRulesService.SettingsKey,
                    out var v)
                ? v
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> ReadAll(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return new Dictionary<string, string>(
                StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer
                       .Deserialize<Dictionary<string, string>>(
                           settingsJson)
                   ?? new Dictionary<string, string>(
                       StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(
                StringComparer.Ordinal);
        }
    }
}