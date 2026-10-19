namespace OpenHandsNET.Application.Identity;

/// <summary>Exposes the authenticated principal of the current request (SPEC-08 RF-002).</summary>
public interface ICurrentUserAccessor
{
    /// <summary>Current user, or null for anonymous requests.</summary>
    AuthUser? User { get; }

    /// <summary>User id or throws when unauthenticated.</summary>
    Guid RequireUserId();
}