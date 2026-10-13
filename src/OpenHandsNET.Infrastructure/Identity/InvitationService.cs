using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Domain.Identity;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Identity;

/// <summary>Batch invitations with token accept (SPEC-08 RF-006).</summary>
public sealed class InvitationService(AppDbContext db, IAuditService audit) : IInvitationService
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(7);

    /// <inheritdoc />
    public async Task<IReadOnlyList<InvitationInfo>> InviteAsync(
        AuthUser actor, Guid orgId, IReadOnlyList<string> emails, string role,
        CancellationToken cancellationToken = default)
    {
        if (!RoleName.IsOrgRole(role))
            throw new ArgumentException($"invalid org role '{role}'");

        var results = new List<InvitationInfo>(emails.Count);
        foreach (var email in emails.Select(e => e.Trim().ToLowerInvariant()).Distinct())
        {
            var existing = await db.OrgInvitations.FirstOrDefaultAsync(
                i => i.OrgId == orgId && i.Email == email && i.AcceptedAt == null,
                cancellationToken);
            if (existing is not null)
            {
                results.Add(ToInfo(existing));
                continue;
            }
            var record = new OrgInvitationRecord
            {
                Id = Guid.NewGuid(),
                Token = "inv_" + Convert.ToBase64String(
                    RandomNumberGenerator.GetBytes(24))
                    .Replace('+', '-').Replace('/', '_').TrimEnd('='),
                OrgId = orgId,
                Email = email,
                Role = role,
                ExpiresAt = DateTimeOffset.UtcNow + TokenLifetime,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.OrgInvitations.Add(record);
            results.Add(ToInfo(record));
        }
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            "org.invite", actor.UserId.ToString(), orgId,
            "organization", orgId.ToString(),
            metadata: new { count = results.Count, role },
            cancellationToken: cancellationToken);
        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InvitationInfo>> ListPendingAsync(
        AuthUser actor, Guid orgId, CancellationToken cancellationToken = default)
    {
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        return await db.OrgInvitations
            .Where(i => i.OrgId == orgId && i.AcceptedAt == null
                && i.ExpiresAtTicks > nowTicks)
            .OrderBy(i => i.Email)
            .Select(i => ToInfo(i))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(
        AuthUser actor, Guid orgId, Guid invitationId,
        CancellationToken cancellationToken = default)
    {
        var record = await db.OrgInvitations.FirstOrDefaultAsync(
            i => i.Id == invitationId && i.OrgId == orgId, cancellationToken);
        if (record is null)
            return false;
        db.OrgInvitations.Remove(record);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<InvitationInfo?> PeekAsync(
        string token, CancellationToken cancellationToken = default)
    {
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        var record = await db.OrgInvitations.FirstOrDefaultAsync(
            i => i.Token == token && i.AcceptedAt == null
                && i.ExpiresAtTicks > nowTicks,
            cancellationToken);
        return record is null ? null : ToInfo(record);
    }

    /// <inheritdoc />
    public async Task<bool> AcceptAsync(
        AuthUser user, string token, CancellationToken cancellationToken = default)
    {
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        var record = await db.OrgInvitations.FirstOrDefaultAsync(
            i => i.Token == token && i.AcceptedAt == null
                && i.ExpiresAtTicks > nowTicks,
            cancellationToken);
        if (record is null)
            return false;

        var already = await db.OrgMembers.AnyAsync(
            m => m.OrgId == record.OrgId && m.UserId == user.UserId,
            cancellationToken);
        if (!already)
        {
            db.OrgMembers.Add(new OrgMemberRecord
            {
                OrgId = record.OrgId,
                UserId = user.UserId,
                Role = record.Role,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        record.AcceptedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            "org.invite_accept", user.UserId.ToString(), record.OrgId,
            "user", user.UserId.ToString(),
            metadata: new { record.Role }, cancellationToken: cancellationToken);
        return true;
    }

    private static InvitationInfo ToInfo(OrgInvitationRecord i) => new(
        i.Id, i.Token, i.OrgId, i.Email, i.Role, i.ExpiresAt, i.AcceptedAt);
}
