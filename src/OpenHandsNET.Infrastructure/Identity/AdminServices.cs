using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Domain.Identity;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Identity;

/// <summary>Super-role helpers — users.roles is a JSON array of super-role names.</summary>
internal static class SuperRoles
{
    internal static List<string> Parse(string? rolesJson) =>
        rolesJson is null
            ? []
            : JsonSerializer.Deserialize<List<string>>(rolesJson) ?? [];

    internal static bool IsSuperAdmin(UserRecord u) =>
        Parse(u.RolesJson).Contains(RoleName.SuperAdmin, StringComparer.Ordinal);
}

/// <summary>Instance super-admin management (SPEC-08 RF-008).</summary>
public sealed class SuperAdminService(AppDbContext db, IAuditService audit)
    : ISuperAdminService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<SuperAdminInfo>> ListAsync(
        CancellationToken cancellationToken = default) =>
        (await db.Users.ToListAsync(cancellationToken))
            .Where(SuperRoles.IsSuperAdmin)
            .Select(u => new SuperAdminInfo(u.Id, u.Email, u.DisplayName))
            .ToList();

    /// <inheritdoc />
    public async Task<bool> IsSuperAdminAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FindAsync([userId], cancellationToken);
        return user is not null && SuperRoles.IsSuperAdmin(user);
    }

    /// <inheritdoc />
    public async Task<SuperAdminInfo?> GrantByUserIdAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FindAsync([userId], cancellationToken);
        if (user is null)
            return null;
        var roles = SuperRoles.Parse(user.RolesJson);
        if (!roles.Contains(RoleName.SuperAdmin))
        {
            roles.Add(RoleName.SuperAdmin);
            user.RolesJson = JsonSerializer.Serialize(roles);
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync(
                "super_admin.grant", null, null, "user", userId.ToString(),
                cancellationToken: cancellationToken);
        }
        return new SuperAdminInfo(user.Id, user.Email, user.DisplayName);
    }

    /// <inheritdoc />
    public async Task<SuperAdminInfo?> GrantByEmailAsync(
        string email, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.Email == email, cancellationToken);
        return user is null ? null : await GrantByUserIdAsync(user.Id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FindAsync([userId], cancellationToken);
        if (user is null)
            return false;
        var roles = SuperRoles.Parse(user.RolesJson);
        if (!roles.Remove(RoleName.SuperAdmin))
            return false;

        var remaining = (await db.Users.ToListAsync(cancellationToken))
            .Count(u => u.Id != userId && SuperRoles.IsSuperAdmin(u));
        if (remaining == 0)
            throw new LastSuperAdminException();

        user.RolesJson = JsonSerializer.Serialize(roles);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            "super_admin.revoke", null, null, "user", userId.ToString(),
            cancellationToken: cancellationToken);
        return true;
    }
}

/// <summary>Admin user listing/deletion.</summary>
public sealed class AdminUserService(AppDbContext db, IAuditService audit)
    : IAdminUserService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<SuperAdminInfo>> ListUsersAsync(
        string? query, int page, int pageSize,
        CancellationToken cancellationToken = default)
    {
        var q = db.Users.Where(u => u.DisabledAt == null);
        if (!string.IsNullOrWhiteSpace(query))
            q = q.Where(u => u.Email.Contains(query));
        return await q.OrderBy(u => u.Email)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(u => new SuperAdminInfo(u.Id, u.Email, u.DisplayName))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> CountUsersAsync(CancellationToken cancellationToken = default) =>
        db.Users.CountAsync(u => u.DisabledAt == null, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> DeleteUserAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FindAsync([userId], cancellationToken);
        if (user is null || user.DisabledAt is not null)
            return false;
        if (SuperRoles.IsSuperAdmin(user))
        {
            var remaining = (await db.Users.ToListAsync(cancellationToken))
                .Count(u => u.Id != userId && SuperRoles.IsSuperAdmin(u));
            if (remaining == 0)
                throw new LastSuperAdminException();
        }
        user.DisabledAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            "admin.user_delete", null, null, "user", userId.ToString(),
            cancellationToken: cancellationToken);
        return true;
    }
}
