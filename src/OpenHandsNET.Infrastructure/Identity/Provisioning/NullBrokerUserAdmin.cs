namespace OpenHandsNET.Infrastructure.Identity.Provisioning;

/// <summary>
/// No-op broker admin for <c>Auth:Provider=none</c> — the local user row is
/// the only identity. Returns a deterministic pseudo broker id.
/// </summary>
public sealed class NullBrokerUserAdmin : IBrokerUserAdmin
{
    /// <inheritdoc />
    public Task<string> CreateUserAsync(
        string email, string? name, string? password,
        CancellationToken cancellationToken = default) =>
        Task.FromResult("local:" + email.ToLowerInvariant());

    /// <inheritdoc />
    public Task DeleteUserAsync(
        string brokerUserId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}