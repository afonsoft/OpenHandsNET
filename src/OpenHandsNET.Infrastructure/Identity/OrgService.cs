using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Domain.Identity;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Identity;

/// <summary>Org + membership management (SPEC-08 RF-005).</summary>
public sealed class OrgService(AppDbContext db, IAuditService audit) : IOrgService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<OrgInfo>> ListMineAsync(
        AuthUser user, CancellationToken cancellationToken = default)
    {
        var query =
            from m in db.OrgMembers
            join o in db.Organizations on m.OrgId equals o.Id
            where m.UserId == user.UserId
            orderby o.Name
            select new { Org = o, m.Role };
        var rows = await query.ToListAsync(cancellationToken);
        return rows
            .Select(r => ToInfo(r.Org, r.Role))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<OrgInfo> CreateAsync(
        AuthUser user, string name, string? slug,
        CancellationToken cancellationToken = default)
    {
        var org = new OrganizationRecord
        {
            Id = Guid.NewGuid(),
            Slug = string.IsNullOrWhiteSpace(slug) ? Slugify(name) : slug!,
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Organizations.Add(org);
        db.OrgMembers.Add(new OrgMemberRecord
        {
            OrgId = org.Id, UserId = user.UserId,
            Role = RoleName.Owner, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            "org.create", user.UserId.ToString(), org.Id,
            "organization", org.Id.ToString(),
            metadata: new { name }, cancellationToken: cancellationToken);
        return ToInfo(org, RoleName.Owner);
    }

    /// <inheritdoc />
    public async Task<OrgInfo?> GetAsync(
        AuthUser user, Guid orgId, CancellationToken cancellationToken = default)
    {
        var role = await RoleOfAsync(user.UserId, orgId, cancellationToken);
        var org = await db.Organizations.FindAsync([orgId], cancellationToken);
        return org is null ? null : ToInfo(org, role);
    }

    /// <inheritdoc />
    public async Task<OrgInfo?> UpdateAsync(
        AuthUser user, Guid orgId, string? name,
        CancellationToken cancellationToken = default)
    {
        var org = await db.Organizations.FindAsync([orgId], cancellationToken);
        if (org is null)
            return null;
        if (!string.IsNullOrWhiteSpace(name))
            org.Name = name;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            "org.update", user.UserId.ToString(), orgId,
            "organization", orgId.ToString(),
            metadata: new { name }, cancellationToken: cancellationToken);
        var role = await RoleOfAsync(user.UserId, orgId, cancellationToken);
        return ToInfo(org, role);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        AuthUser user, Guid orgId, CancellationToken cancellationToken = default)
    {
        var org = await db.Organizations.FindAsync([orgId], cancellationToken);
        if (org is null)
            return false;
        db.Organizations.Remove(org);
        db.OrgMembers.RemoveRange(db.OrgMembers.Where(m => m.OrgId == orgId));
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            "org.delete", user.UserId.ToString(), orgId,
            "organization", orgId.ToString(), cancellationToken: cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemberInfo>> ListMembersAsync(
        AuthUser user, Guid orgId, string? role,
        CancellationToken cancellationToken = default)
    {
        var query =
            from m in db.OrgMembers
            join u in db.Users on m.UserId equals u.Id
            where m.OrgId == orgId
            select new { Member = m, User = u };
        if (!string.IsNullOrWhiteSpace(role))
            query = query.Where(x => x.Member.Role == role);
        var rows = await query.OrderBy(x => x.User.Email).ToListAsync(cancellationToken);
        return rows
            .Select(r => new MemberInfo(
                r.User.Id, r.User.Email, r.User.DisplayName,
                r.Member.Role, r.Member.CreatedAt))
            .ToList();
    }

    /// <inheritdoc />
    public Task<int> CountMembersAsync(
        AuthUser user, Guid orgId, CancellationToken cancellationToken = default) =>
        db.OrgMembers.CountAsync(m => m.OrgId == orgId, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> ChangeMemberRoleAsync(
        AuthUser actor, Guid orgId, Guid targetUserId, string role,
        CancellationToken cancellationToken = default)
    {
        if (!RoleName.IsOrgRole(role))
            throw new ArgumentException($"invalid org role '{role}'");
        var member = await db.OrgMembers.FindAsync(
            [orgId, targetUserId], cancellationToken);
        if (member is null)
            return false;
        member.Role = role;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            "org.member_role", actor.UserId.ToString(), orgId,
            "user", targetUserId.ToString(),
            metadata: new { role }, cancellationToken: cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveMemberAsync(
        AuthUser actor, Guid orgId, Guid targetUserId,
        CancellationToken cancellationToken = default)
    {
        var member = await db.OrgMembers.FindAsync(
            [orgId, targetUserId], cancellationToken);
        if (member is null)
            return false;
        db.OrgMembers.Remove(member);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            "org.member_remove", actor.UserId.ToString(), orgId,
            "user", targetUserId.ToString(), cancellationToken: cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(
        AuthUser user, Guid orgId, CancellationToken cancellationToken = default)
    {
        var org = await db.Organizations.FindAsync([orgId], cancellationToken);
        if (org?.SettingsJson is not { } json)
            return new Dictionary<string, string>();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? new Dictionary<string, string>();
    }

    /// <inheritdoc />
    public async Task<bool> UpdateSettingsAsync(
        AuthUser user, Guid orgId, IReadOnlyDictionary<string, string> settings,
        CancellationToken cancellationToken = default)
    {
        var org = await db.Organizations.FindAsync([orgId], cancellationToken);
        if (org is null)
            return false;
        org.SettingsJson = JsonSerializer.Serialize(settings);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Org role of a user (null = not a member).</summary>
    public async Task<string?> RoleOfAsync(
        Guid userId, Guid orgId, CancellationToken cancellationToken = default) =>
        await db.OrgMembers
            .Where(m => m.OrgId == orgId && m.UserId == userId)
            .Select(m => m.Role)
            .FirstOrDefaultAsync(cancellationToken);

    private static OrgInfo ToInfo(OrganizationRecord o, string? role) => new(
        o.Id, o.Slug, o.Name, o.PlanId, o.SuspendedAt is not null, role, o.CreatedAt);

    private static string Slugify(string name) =>
        new string(name.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray()).Trim('-');
}
