using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using OpenHandsNET.Application.Identity;
using OpenHandsNET.Domain.Identity;
using OpenHandsNET.Infrastructure.Identity.Provisioning;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Identity;

/// <summary>
/// Admin provisioning (SPEC-08 RF-007): broker user (email-verified, skips
/// TOS/onboarding) → local user → membership → initial API key. On failure
/// after broker creation, compensating rollback deletes the broker user and
/// any local rows.
/// </summary>
public sealed class ProvisioningService(
    AppDbContext db,
    IBrokerUserAdmin broker,
    IApiKeyService apiKeys,
    IAuditService audit,
    ILogger<ProvisioningService> logger) : IProvisioningService
{
    /// <inheritdoc />
    public async Task<ProvisionedUser> ProvisionAsync(
        AuthUser actor, string email, string? name, Guid orgId, string role,
        bool generatePassword, CancellationToken cancellationToken = default)
    {
        if (!RoleName.IsOrgRole(role))
            throw new ArgumentException($"invalid org role '{role}'");
        var org = await db.Organizations.FindAsync([orgId], cancellationToken)
            ?? throw new ArgumentException("org not found");

        var password = generatePassword
            ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(12))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=')
            : null;

        string? brokerUserId = null;
        Guid? localUserId = null;
        try
        {
            brokerUserId = await broker.CreateUserAsync(
                email, name, password, cancellationToken);

            var user = new UserRecord
            {
                Id = Guid.NewGuid(),
                Email = email.ToLowerInvariant(),
                DisplayName = name,
                EmailVerified = true,
                OnboardingDone = true,
                TosAcceptedAt = DateTimeOffset.UtcNow,
                CurrentOrgId = orgId.ToString(),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(user);
            db.OrgMembers.Add(new OrgMemberRecord
            {
                OrgId = orgId,
                UserId = user.Id,
                Role = role,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(cancellationToken);
            localUserId = user.Id;

            var authUser = new AuthUser(
                user.Id, user.Email, user.DisplayName,
                IsSuperAdmin: false, user.CurrentOrgId, AuthType.Cookie);
            var key = await apiKeys.CreateAsync(
                authUser, "Provisioning Initial Key", null, null, orgId,
                cancellationToken);

            await audit.RecordAsync(
                "admin.provision_user", actor.UserId.ToString(), orgId,
                "user", user.Id.ToString(),
                metadata: new { role }, cancellationToken: cancellationToken);
            return new ProvisionedUser(user.Email, password, key.Plaintext);
        }
        catch
        {
            logger.LogWarning(
                "provisioning failed for org {OrgId} — running compensations", orgId);
            if (localUserId is { } uid)
            {
                db.OrgMembers.RemoveRange(
                    db.OrgMembers.Where(m => m.UserId == uid));
                var user = await db.Users.FindAsync([uid], cancellationToken);
                if (user is not null)
                    db.Users.Remove(user);
                await db.SaveChangesAsync(cancellationToken);
            }
            if (brokerUserId is not null)
            {
                try { await broker.DeleteUserAsync(brokerUserId, cancellationToken); }
                catch (Exception e)
                {
                    logger.LogError(e, "broker rollback failed for {BrokerUserId}", brokerUserId);
                }
            }
            throw;
        }
    }
}