namespace OpenHandsNET.Infrastructure.Identity.Provisioning;

/// <summary>
/// Broker-side user administration (Keycloak Admin REST or equivalent IdP).
/// Implementations must support delete for compensating rollback
/// (SPEC-08 RF-007).
/// </summary>
public interface IBrokerUserAdmin
{
    /// <summary>Create an email-verified user; returns the broker user id.</summary>
    Task<string> CreateUserAsync(
        string email, string? name, string? password,
        CancellationToken cancellationToken = default);

    /// <summary>Delete a broker user (rollback compensation).</summary>
    Task DeleteUserAsync(string brokerUserId, CancellationToken cancellationToken = default);
}

/// <summary>Thrown when a broker operation fails (triggers rollback).</summary>
public sealed class BrokerAdminException(string message) : InvalidOperationException(message);
