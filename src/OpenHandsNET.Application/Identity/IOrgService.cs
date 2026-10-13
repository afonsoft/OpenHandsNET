namespace OpenHandsNET.Application.Identity;

/// <summary>Org + membership management (SPEC-08 RF-005/RF-006).</summary>
public interface IOrgService
{
    Task<IReadOnlyList<OrgInfo>> ListMineAsync(
        AuthUser user, CancellationToken cancellationToken = default);

    Task<OrgInfo> CreateAsync(
        AuthUser user, string name, string? slug,
        CancellationToken cancellationToken = default);

    Task<OrgInfo?> GetAsync(
        AuthUser user, Guid orgId, CancellationToken cancellationToken = default);

    Task<OrgInfo?> UpdateAsync(
        AuthUser user, Guid orgId, string? name,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        AuthUser user, Guid orgId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemberInfo>> ListMembersAsync(
        AuthUser user, Guid orgId, string? role,
        CancellationToken cancellationToken = default);

    Task<int> CountMembersAsync(
        AuthUser user, Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>Change a member role (false = member not found).</summary>
    Task<bool> ChangeMemberRoleAsync(
        AuthUser actor, Guid orgId, Guid targetUserId, string role,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveMemberAsync(
        AuthUser actor, Guid orgId, Guid targetUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Org settings map (auto_add domain, feature flags, etc.).</summary>
    Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(
        AuthUser user, Guid orgId, CancellationToken cancellationToken = default);

    Task<bool> UpdateSettingsAsync(
        AuthUser user, Guid orgId, IReadOnlyDictionary<string, string> settings,
        CancellationToken cancellationToken = default);
}

/// <summary>Org invitations (batch create + token accept).</summary>
public interface IInvitationService
{
    Task<IReadOnlyList<InvitationInfo>> InviteAsync(
        AuthUser actor, Guid orgId, IReadOnlyList<string> emails, string role,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InvitationInfo>> ListPendingAsync(
        AuthUser actor, Guid orgId, CancellationToken cancellationToken = default);

    Task<bool> RevokeAsync(
        AuthUser actor, Guid orgId, Guid invitationId,
        CancellationToken cancellationToken = default);

    /// <summary>Validate token without consuming (redirect to signup).</summary>
    Task<InvitationInfo?> PeekAsync(
        string token, CancellationToken cancellationToken = default);

    /// <summary>Accept: creates membership + marks accepted_at (false = invalid/expired).</summary>
    Task<bool> AcceptAsync(
        AuthUser user, string token, CancellationToken cancellationToken = default);
}
