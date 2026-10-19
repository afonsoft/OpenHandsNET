namespace OpenHandsNET.Application.Contracts;

/// <summary>
/// Encrypted user/org secret storage (SPEC-08) + by-ref resolution for
/// sandbox env injection (SPEC-33 RF-003). Names flow through the API and
/// event stream; values are resolved only at the provisioning edge and
/// never serialized into events.
/// </summary>
public interface ISecretStore
{
    /// <summary>Decrypts one secret value (null = not found).</summary>
    Task<string?> ResolveAsync(
        string scope, string ownerId, string name, CancellationToken ct);

    /// <summary>Names only — listing never exposes values.</summary>
    Task<IReadOnlyList<string>> ListNamesAsync(
        string scope, string ownerId, CancellationToken ct);

    /// <summary>Encrypts and upserts <paramref name="plaintext"/>.</summary>
    Task SetAsync(string scope, string ownerId, string name,
        string plaintext, CancellationToken ct);

    /// <summary>Deletes a secret (false = not found).</summary>
    Task<bool> DeleteAsync(
        string scope, string ownerId, string name, CancellationToken ct);
}