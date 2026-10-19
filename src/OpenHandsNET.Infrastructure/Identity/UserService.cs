using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using OpenHandsNET.Application.Identity;
using OpenHandsNET.Domain.Identity;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Identity;

/// <summary>
/// JIT user provisioning (SPEC-08 RF-001). Bootstrap rule: the first user in
/// the instance becomes <c>superadmin</c> + owner of a default org
/// (multi-tenancy on) so a fresh deployment is administrable.
/// </summary>
public sealed class UserService(AppDbContext db, IAuditService audit) : IUserService
{
    /// <inheritdoc />
    public async Task<AuthUser> GetOrCreateAsync(
        string email, string? displayName, string? idpType,
        bool multiTenancyEnabled, CancellationToken cancellationToken = default)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.Email == normalized, cancellationToken);

        if (user is null)
        {
            var isFirst = !await db.Users.AnyAsync(cancellationToken);
            user = new UserRecord
            {
                Id = Guid.NewGuid(),
                Email = normalized,
                DisplayName = displayName,
                IdpType = idpType,
                RolesJson = isFirst
                    ? JsonSerializer.Serialize(new[] { RoleName.SuperAdmin })
                    : null,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(user);

            if (isFirst)
            {
                // Bootstrap: default org owned by the first user.
                var org = new OrganizationRecord
                {
                    Id = Guid.NewGuid(),
                    Slug = "default",
                    Name = "Default Organization",
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                db.Organizations.Add(org);
                db.OrgMembers.Add(new OrgMemberRecord
                {
                    OrgId = org.Id,
                    UserId = user.Id,
                    Role = RoleName.Owner,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                user.CurrentOrgId = org.Id.ToString();
            }
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync(
                "auth.user_created", user.Id.ToString(),
                Guid.TryParse(user.CurrentOrgId, out var cid) ? cid : null,
                "user", user.Id.ToString(),
                metadata: new { idp_type = idpType, first_user = isFirst },
                cancellationToken: cancellationToken);
        }
        else if (user.IdpType is null && idpType is not null)
        {
            user.IdpType = idpType;
            await db.SaveChangesAsync(cancellationToken);
        }

        return ToAuthUser(user);
    }

    /// <inheritdoc />
    public async Task<AuthUser?> GetAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FindAsync([userId], cancellationToken);
        return user is null || user.DisabledAt is not null ? null : ToAuthUser(user);
    }

    /// <inheritdoc />
    public async Task AcceptTosAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FindAsync([userId], cancellationToken);
        if (user is not null && user.TosAcceptedAt is null)
        {
            user.TosAcceptedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task CompleteOnboardingAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FindAsync([userId], cancellationToken);
        if (user is not null && !user.OnboardingDone)
        {
            user.OnboardingDone = true;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<(bool NeedsOnboarding, bool TosAccepted)?> OnboardingStatusAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FindAsync([userId], cancellationToken);
        return user is null ? null : (!user.OnboardingDone, user.TosAcceptedAt is not null);
    }

    private static AuthUser ToAuthUser(UserRecord u) => new(
        u.Id, u.Email, u.DisplayName,
        SuperRoles.IsSuperAdmin(u), u.CurrentOrgId, AuthType.Cookie,
        IdpType: u.IdpType);
}