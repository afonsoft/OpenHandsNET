namespace OpenHandsNET.Application.Identity;

/// <summary>Local user store — JIT provisioning from IdP claims (SPEC-08 RF-001).</summary>
public interface IUserService
{
    /// <summary>
    /// Find-or-create a user by email. First user ever in <c>oss</c>/
    /// <c>self_hosted</c> gets the <c>superadmin</c> super-role and a default
    /// org (SPEC-08 bootstrap rule).
    /// </summary>
    Task<AuthUser> GetOrCreateAsync(
        string email, string? displayName, string? idpType,
        bool multiTenancyEnabled, CancellationToken cancellationToken = default);

    /// <summary>Lookup by id — null when absent/disabled.</summary>
    Task<AuthUser?> GetAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Mark TOS acceptance.</summary>
    Task AcceptTosAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Mark onboarding complete.</summary>
    Task CompleteOnboardingAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Current onboarding flags for the user.</summary>
    Task<(bool NeedsOnboarding, bool TosAccepted)?> OnboardingStatusAsync(
        Guid userId, CancellationToken cancellationToken = default);
}
