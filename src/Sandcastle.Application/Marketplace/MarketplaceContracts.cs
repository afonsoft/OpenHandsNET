using Sandcastle.Domain.Marketplace;

namespace Sandcastle.Application.Marketplace;

/// <summary>
/// SPEC-148 RF-001: loads the marketplace registry manifest. Default
/// source is the bundled <c>marketplace.json</c> shipped with the app
/// (offline-capable); <c>Marketplace:RegistryUrl</c> allows a remote
/// registry (HTTPS + <c>Marketplace:AllowedHosts</c> only).
/// </summary>
public interface IMarketplaceRegistry
{
    /// <summary>All entries of the effective registry (bundled or remote).</summary>
    Task<IReadOnlyList<MarketplaceEntry>> ListEntriesAsync(CancellationToken ct);

    /// <summary>Single entry by id, or null.</summary>
    Task<MarketplaceEntry?> GetEntryAsync(string id, CancellationToken ct);

    /// <summary>
    /// Resolves a file's content — inline for embedded entries, fetched
    /// (HTTPS + allowlist + sha256) for remote ones. Throws
    /// <see cref="MarketplaceRegistryException"/> on policy/sha violations.
    /// </summary>
    Task<string> ResolveFileContentAsync(
        MarketplaceEntry entry, MarketplaceFile file, CancellationToken ct);
}

/// <summary>
/// Registry-level failures — <see cref="MarketplaceRegistryException.Code"/>
/// maps to the API contract (502 registry unreachable, 422 violations).
/// </summary>
public sealed class MarketplaceRegistryException : Exception
{
    /// <summary>Machine-readable reason.</summary>
    public string Code { get; }

    /// <inheritdoc />
    public MarketplaceRegistryException(string code, string message)
        : base(message) => Code = code;
}

/// <summary>
/// SPEC-148 RF-002/RF-003: install pipeline + installed inventory with
/// user→org→system precedence.
/// </summary>
public interface IMarketplaceService
{
    /// <summary>Install an entry into a scope (validates + materializes + records).</summary>
    Task<MarketplaceInstallResult> InstallAsync(
        MarketplaceInstallRequest request, CancellationToken ct);

    /// <summary>List installed rows visible to the caller (optionally scope-filtered).</summary>
    Task<IReadOnlyList<InstalledSkill>> ListInstalledAsync(
        string? userId, Guid? orgId, InstallScope? scope, CancellationToken ct);

    /// <summary>
    /// Effective entries for a user — same entry installed at multiple
    /// scopes resolves by precedence <b>user &gt; org &gt; system</b>
    /// (SPEC-147 settings-chain parity).
    /// </summary>
    Task<IReadOnlyList<InstalledSkill>> ResolveEffectiveAsync(
        string userId, Guid? orgId, CancellationToken ct);

    /// <summary>Enable/disable a row the caller may manage.</summary>
    Task<(InstalledSkill? Row, string? Error)> SetEnabledAsync(
        Guid id, bool enabled, CancellationToken ct);

    /// <summary>Remove a row + dematerialize its assets.</summary>
    Task<bool> RemoveAsync(Guid id, CancellationToken ct);

    /// <summary>Re-resolve the manifest and bump the row to the registry version.</summary>
    Task<(InstalledSkill? Row, string? Error)> UpgradeAsync(
        Guid id, CancellationToken ct);

    /// <summary>Lookup for scope checks in the endpoint layer.</summary>
    Task<InstalledSkill?> GetInstalledAsync(Guid id, CancellationToken ct);
}

/// <summary>Install request (endpoint → service).</summary>
public sealed record MarketplaceInstallRequest(
    string EntryId,
    string? Version,
    InstallScope Scope,
    string? ScopeId,
    string InstalledBy,
    bool AcceptPermissions,
    bool IsMemberOfScopeOrg,
    bool IsSuperAdmin);

/// <summary>Install outcome — <see cref="Error"/> maps to the API contract.</summary>
public sealed record MarketplaceInstallResult(
    InstalledSkill? Installed,
    string? Error,
    IReadOnlyList<InstalledSkill>? BundleItems = null)
{
    /// <summary>Success.</summary>
    public static MarketplaceInstallResult Ok(InstalledSkill row) => new(row, null);

    /// <summary>Failure — <paramref name="code"/> is a wire error key.</summary>
    public static MarketplaceInstallResult Fail(string code) => new(null, code);
}