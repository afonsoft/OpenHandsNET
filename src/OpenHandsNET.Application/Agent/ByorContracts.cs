namespace OpenHandsNET.Application.Agent;

/// <summary>
/// SPEC-50 RF-001: <c>LlmProxy:*</c> options — LiteLLM admin API for
/// BYOR (bring-your-own-router) virtual keys. Master key comes from
/// env/secret, never committed config. <see cref="Mode"/>:
/// <c>off</c> (default, zero behavior change) | <c>hybrid</c>
/// (local catalog stays, proxy models allowed) | <c>byor</c>
/// (only proxy-served models).
/// </summary>
public sealed class LlmProxyOptions
{
    /// <summary>LiteLLM proxy base URL (null/empty = BYOR disabled).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Admin API key — bind via env, never appsettings.</summary>
    public string? MasterKey { get; set; }

    /// <summary><c>off</c> | <c>hybrid</c> | <c>byor</c>.</summary>
    public string Mode { get; set; } = "off";

    /// <summary>Default USD budget for new virtual keys.</summary>
    public decimal DefaultBudgetUsd { get; set; } = 50m;

    /// <summary>Proxy configured + mode != off.</summary>
    public bool Enabled =>
        !string.IsNullOrEmpty(BaseUrl)
        && !string.IsNullOrEmpty(MasterKey)
        && Mode is "hybrid" or "byor";
}

/// <summary>Created LiteLLM virtual key (plaintext, once).</summary>
public sealed record LiteLlmVirtualKey(string Key, string KeyId,
    string? BudgetId);

/// <summary>
/// SPEC-50 RF-001: typed client for the LiteLLM admin API
/// (<c>/key/generate</c>, <c>/key/delete</c>, <c>/models</c>).
/// </summary>
public interface ILiteLlmAdminClient
{
    /// <summary>Creates a virtual key scoped to user/org/models/budget.</summary>
    Task<LiteLlmVirtualKey> CreateVirtualKeyAsync(
        string userId, string? orgId, decimal budgetUsd,
        IReadOnlyList<string> models, CancellationToken ct);

    /// <summary>Revokes a virtual key by id.</summary>
    Task DeleteKeyAsync(string keyId, CancellationToken ct);

    /// <summary>Updates the max budget of an existing key.</summary>
    Task UpdateKeyBudgetAsync(string keyId, decimal budgetUsd,
        CancellationToken ct);

    /// <summary>Models the proxy serves (OpenAI <c>/models</c> ids).</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);
}

/// <summary>Proxy unreachable/misconfigured — maps to 503.</summary>
public sealed class LiteLlmUnavailableException : Exception
{
    /// <summary>Creates the failure.</summary>
    public LiteLlmUnavailableException(string message,
        Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Resolved per-call LLM routing override: point the OpenAI-compatible
/// request at the proxy with the owner's virtual key, plus the
/// <c>X-LiteLLM-Key</c> tracking id (key_id — never the key itself).
/// </summary>
public sealed record ByorCallContext(
    string BaseUrl, string VirtualKey, string KeyId);

/// <summary>Active BYOR key metadata (never the secret).</summary>
public sealed record ByorKeyInfo(
    Guid Id, string KeyId, string KeyPrefix, decimal BudgetUsd,
    IReadOnlyList<string> Models, DateTimeOffset CreatedAt);

/// <summary>
/// SPEC-50 RF-002/003: BYOR key lifecycle — one active virtual key per
/// user; plaintext persists only encrypted (DataProtection), leaves the
/// API once at refresh.
/// </summary>
public interface IByorKeyService
{
    /// <summary>Proxy enabled (<see cref="LlmProxyOptions.Enabled"/>).</summary>
    bool Enabled { get; }

    /// <summary>Active key metadata of the user (null = not configured).</summary>
    Task<ByorKeyInfo?> GetActiveAsync(string userId, CancellationToken ct);

    /// <summary>
    /// Rotates: revokes the previous key (best-effort delete on the
    /// proxy), creates a fresh one, returns the plaintext **once**.
    /// </summary>
    Task<(string Key, ByorKeyInfo Info)> RefreshAsync(
        string userId, string? orgId, decimal? budgetUsd,
        CancellationToken ct);

    /// <summary>Revokes the active key (proxy delete + local flag).</summary>
    Task<bool> RevokeAsync(string userId, CancellationToken ct);

    /// <summary>
    /// Call routing context for <paramref name="userId"/>: decrypts the
    /// stored virtual key for the single in-flight call (never logged,
    /// never persisted in cleartext).
    /// </summary>
    Task<ByorCallContext?> ResolveCallContextAsync(
        string userId, CancellationToken ct);

    /// <summary>
    /// Model ids the proxy serves — the <c>permitted</c> list enforcing
    /// selections in <c>byor</c> mode (cached 60s).
    /// </summary>
    Task<IReadOnlyList<string>> PermittedModelsAsync(CancellationToken ct);
}
