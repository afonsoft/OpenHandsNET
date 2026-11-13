using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.ChatOps;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.ChatOps;

/// <summary>
/// SPEC-147/151 — opt-in de pings de grupo, compartilhado entre sinks:
/// instância (<c>ChatOps:AllowGroupMentions</c>) ou setting da org
/// (<c>chatops_allow_group_mentions</c>). Fail-closed — qualquer erro
/// de parse mantém o default (neutralizar).
/// </summary>
public static class GroupMentionGate
{
    /// <summary>Org setting key em <c>organizations.settings_json</c>.</summary>
    public const string OrgSettingAllowGroupMentions =
        "chatops_allow_group_mentions";

    /// <summary>True quando pings podem sair sem neutralização.</summary>
    public static async Task<bool> AllowsAsync(
        AppDbContext db, ChatOpsOptions options, Guid? orgId,
        CancellationToken ct)
    {
        if (options.AllowGroupMentions)
        {
            return true;
        }
        return await OrgAllowsAsync(db, orgId, ct);
    }

    /// <summary>Org opt-in via settings dict (default false).</summary>
    private static async Task<bool> OrgAllowsAsync(
        AppDbContext db, Guid? orgId, CancellationToken ct)
    {
        if (orgId is not { } id)
        {
            return false;
        }

        var json = await db.Organizations.AsNoTracking()
            .Where(o => o.Id == id)
            .Select(o => o.SettingsJson)
            .FirstOrDefaultAsync(ct);
        if (json is null)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(
                    OrgSettingAllowGroupMentions, out var v)
                && v.ValueKind == JsonValueKind.String
                && bool.TryParse(v.GetString(), out var allow)
                && allow;
        }
        catch (JsonException)
        {
            return false; // fail-closed: settings inválidas não liberam ping
        }
    }
}