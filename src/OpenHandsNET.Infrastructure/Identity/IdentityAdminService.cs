using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using OpenHandsNET.Application.Identity;
using OpenHandsNET.Domain.Identity;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Identity;

/// <summary>
/// SPEC-58: tokens SCIM (<c>scim_*</c>), CRUD de group mappings/tiers/
/// overrides e sync de claims do IdP nas memberships.
/// </summary>
public sealed class IdentityAdminService(AppDbContext db, IAuditService audit)
    : IScimTokenService, IIdentityAdminService, IIdpSyncService
{
    public const string ScimKeyPrefix = "scim_";

    // ---------- scim_* tokens ----------

    public async Task<ScimTokenIssue> MintAsync(
        Guid orgId, string? name, CancellationToken ct)
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .TrimEnd('=');
        var plaintext = ScimKeyPrefix + raw;
        var rec = new ScimTokenRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Prefix = plaintext[..12],
            KeyHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)))
                .ToLowerInvariant(),
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.ScimTokens.Add(rec);
        await db.SaveChangesAsync(ct);
        return new ScimTokenIssue(rec.Id, rec.Prefix, plaintext);
    }

    public async Task<Guid?> ValidateAsync(string presented, CancellationToken ct)
    {
        if (!presented.StartsWith(ScimKeyPrefix, StringComparison.Ordinal))
            return null;
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)))
            .ToLowerInvariant();
        var rec = await db.ScimTokens
            .FirstOrDefaultAsync(
                t => t.KeyHash == hash && t.DisabledAt == null, ct);
        if (rec is null)
            return null;
        rec.LastUsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return rec.OrgId;
    }

    public async Task<IReadOnlyList<(Guid, string, string?, DateTimeOffset)>>
        ListAsync(Guid orgId, CancellationToken ct) => await db.ScimTokens
        .AsNoTracking()
        .Where(t => t.OrgId == orgId && t.DisabledAt == null)
        .Select(t => new ValueTuple<Guid, string, string?, DateTimeOffset>(
            t.Id, t.Prefix, t.Name, t.CreatedAt))
        .ToListAsync(ct);

    public async Task<bool> RevokeAsync(
        Guid orgId, Guid tokenId, CancellationToken ct)
    {
        var rec = await db.ScimTokens
            .FirstOrDefaultAsync(t => t.Id == tokenId && t.OrgId == orgId, ct);
        if (rec is null || rec.DisabledAt is not null)
            return false;
        rec.DisabledAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ---------- idp group mappings ----------

    public async Task<IReadOnlyList<IdpMappingInfo>> ListMappingsAsync(
        Guid orgId, CancellationToken ct) => await db.IdpGroupMappings
        .AsNoTracking()
        .Where(m => m.OrgId == orgId)
        .Select(m => new IdpMappingInfo(
            m.Id, m.OrgId!.Value, m.IdpGroup, m.Role, m.UsageTierId))
        .ToListAsync(ct);

    public async Task<IdpMappingInfo> CreateMappingAsync(
        Guid orgId, string idpGroup, string role, Guid? usageTierId,
        CancellationToken ct)
    {
        if (!RoleName.IsOrgRole(role))
            throw new ArgumentException($"invalid role: {role}");
        var rec = new IdpGroupMappingRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            IdpGroup = idpGroup,
            Role = role,
            UsageTierId = usageTierId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.IdpGroupMappings.Add(rec);
        await db.SaveChangesAsync(ct);
        return new IdpMappingInfo(
            rec.Id, orgId, rec.IdpGroup, rec.Role, rec.UsageTierId);
    }

    public async Task<bool> DeleteMappingAsync(
        Guid orgId, Guid id, CancellationToken ct)
    {
        var n = await db.IdpGroupMappings
            .Where(m => m.Id == id && m.OrgId == orgId)
            .ExecuteDeleteAsync(ct);
        return n > 0;
    }

    // ---------- usage tiers ----------

    public async Task<IReadOnlyList<UsageTierInfo>> ListTiersAsync(
        Guid orgId, CancellationToken ct) => await db.UsageTiers
        .AsNoTracking()
        .Where(t => t.OrgId == orgId)
        .Select(t => new UsageTierInfo(
            t.Id, t.OrgId!.Value, t.Name, t.LimitsJson))
        .ToListAsync(ct);

    public async Task<UsageTierInfo> CreateTierAsync(
        Guid orgId, string name, string? limitsJson, CancellationToken ct)
    {
        var rec = new UsageTierRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Name = name,
            LimitsJson = limitsJson,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.UsageTiers.Add(rec);
        await db.SaveChangesAsync(ct);
        return new UsageTierInfo(rec.Id, orgId, rec.Name, rec.LimitsJson);
    }

    public async Task<UsageTierInfo?> UpdateTierAsync(
        Guid orgId, Guid id, string? name, string? limitsJson,
        CancellationToken ct)
    {
        var rec = await db.UsageTiers
            .FirstOrDefaultAsync(t => t.Id == id && t.OrgId == orgId, ct);
        if (rec is null)
            return null;
        if (name is not null) rec.Name = name;
        if (limitsJson is not null) rec.LimitsJson = limitsJson;
        await db.SaveChangesAsync(ct);
        return new UsageTierInfo(rec.Id, orgId, rec.Name, rec.LimitsJson);
    }

    public async Task<bool> DeleteTierAsync(
        Guid orgId, Guid id, CancellationToken ct)
    {
        // Referências em mappings/memberships ficam órfãs — resolução
        // falha aberta (tier sumiu → cai no plano da org).
        var n = await db.UsageTiers
            .Where(t => t.Id == id && t.OrgId == orgId)
            .ExecuteDeleteAsync(ct);
        return n > 0;
    }

    // ---------- user overrides ----------

    public async Task<UserOverrideInfo> SetUserOverrideAsync(
        Guid orgId, Guid userId, Guid actorId, string? limitsJson,
        DateTimeOffset? expiresAt, CancellationToken ct)
    {
        var rec = await db.UserUsageOverrides
            .FirstOrDefaultAsync(
                o => o.OrgId == orgId && o.UserId == userId, ct)
            ?? new UserUsageOverrideRecord
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                UserId = userId,
                CreatedBy = actorId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
        if (rec.CreatedAt == default)
            rec.CreatedAt = DateTimeOffset.UtcNow;
        rec.LimitsJson = limitsJson;
        rec.ExpiresAt = expiresAt;
        if (rec.CreatedBy == default)
            rec.CreatedBy = actorId;
        if (db.Entry(rec).State == EntityState.Detached)
            db.UserUsageOverrides.Add(rec);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(
            "usage.tier_applied", actorId.ToString(), orgId, "user",
            userId.ToString(), null, null,
            metadata: new { limits_json = limitsJson, expires_at = expiresAt },
            cancellationToken: ct);
        return new UserOverrideInfo(
            rec.Id, orgId, userId, rec.LimitsJson, rec.ExpiresAt);
    }

    public async Task<UserOverrideInfo?> GetUserOverrideAsync(
        Guid orgId, Guid userId, CancellationToken ct) =>
        await db.UserUsageOverrides.AsNoTracking()
            .Where(o => o.OrgId == orgId && o.UserId == userId)
            .Select(o => new UserOverrideInfo(
                o.Id, orgId, userId, o.LimitsJson, o.ExpiresAt))
            .FirstOrDefaultAsync(ct);

    public async Task<bool> ClearUserOverrideAsync(
        Guid orgId, Guid userId, CancellationToken ct) =>
        await db.UserUsageOverrides
            .Where(o => o.OrgId == orgId && o.UserId == userId)
            .ExecuteDeleteAsync(ct) > 0;

    // ---------- IdP sync on login ----------

    public async Task<IReadOnlyList<IdpMappingInfo>> ApplyOnLoginAsync(
        Guid userId, IReadOnlyList<string> idpGroups, CancellationToken ct)
    {
        var matched = new List<IdpMappingInfo>();
        var now = DateTimeOffset.UtcNow;
        var groups = new HashSet<string>(
            idpGroups, StringComparer.OrdinalIgnoreCase);

        var all = await db.IdpGroupMappings.ToListAsync(ct);
        foreach (var m in all.Where(m => groups.Contains(m.IdpGroup)))
        {
            var orgId = m.OrgId!.Value;
            var member = await db.OrgMembers.FirstOrDefaultAsync(
                x => x.OrgId == orgId && x.UserId == userId, ct);
            if (member is null)
            {
                db.OrgMembers.Add(new OrgMemberRecord
                {
                    OrgId = orgId,
                    UserId = userId,
                    Role = m.Role,
                    Source = "idp",
                    UsageTierId = m.UsageTierId,
                    CreatedAt = now,
                });
            }
            else if (member.Source == "idp")
            {
                // IdP vence só sobre membership que veio de mapping.
                member.Role = m.Role;
                member.UsageTierId = m.UsageTierId;
            }
            // membership manual: não toca role, mas anota o tier do mapping
            else if (member.UsageTierId != m.UsageTierId)
            {
                member.UsageTierId = m.UsageTierId;
            }
            matched.Add(new IdpMappingInfo(
                m.Id, orgId, m.IdpGroup, m.Role, m.UsageTierId));
            await audit.RecordAsync(
                "idp.group_matched", userId.ToString(), orgId, "user",
                userId.ToString(), null, null,
                metadata: new { idp_group = m.IdpGroup, role = m.Role },
                cancellationToken: ct);
        }

        // Revoga memberships idp-sourced cujo mapping não casou mais.
        var matchedOrgs = matched.Select(m => (Guid?)m.OrgId).ToHashSet();
        var stale = await db.OrgMembers
            .Where(x => x.UserId == userId && x.Source == "idp"
                && !matchedOrgs.Contains((Guid?)x.OrgId))
            .ToListAsync(ct);
        foreach (var s in stale)
        {
            db.OrgMembers.Remove(s);
            await audit.RecordAsync(
                "idp.membership_revoked", userId.ToString(), s.OrgId,
                "user", userId.ToString(), cancellationToken: ct);
        }

        await db.SaveChangesAsync(ct);
        return matched;
    }
}