namespace OpenHandsNET.Application.Agent;

/// <summary>
/// SPEC-134 RF-001: BYOK por usuário — chave de provider que o dono da
/// conversa cola no Settings. Persistida só como ciphertext
/// (DataProtection, mesmo padrão de <c>SecretRecord</c>/<c>ByorKeyRecord</c>);
/// serialização sempre mascarada (<c>sk-…{last4}</c>).
/// </summary>
public sealed record UserLlmKeyInfo(
    string Provider, string MaskedKey, string? BaseUrl,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Per-call routing override resolvido do BYOK do usuário: aponta a chamada
/// OpenAI-compatible direto ao provider com a key do dono — em memória só,
/// nunca logada ou persistida em claro.
/// </summary>
public sealed record UserLlmKeyCallContext(
    string Provider, string BaseUrl, string Key);

/// <summary>Catálogo estático de providers BYOK suportados.</summary>
public sealed record LlmKeyProviderDescriptor(
    string Id, string Label, string DefaultBaseUrl, bool RequiresBaseUrl);

/// <summary>
/// SPEC-134 RF-002/RF-003: ciclo de vida das keys por usuário — CRUD
/// owner-scoped + resolução por run com precedência user → BYOR → global.
/// </summary>
public interface IUserLlmKeyService
{
    /// <summary>Catálogo estático (openai, anthropic, gemini, openai_compat).</summary>
    IReadOnlyList<LlmKeyProviderDescriptor> Providers { get; }

    /// <summary>Keys configuradas do usuário — sempre mascaradas.</summary>
    Task<IReadOnlyList<UserLlmKeyInfo>> ListAsync(
        string userId, CancellationToken ct);

    /// <summary>
    /// Upsert: valida provider/base_url e persiste ciphertext.
    /// Retorna a visão mascarada.
    /// </summary>
    Task<UserLlmKeyInfo> UpsertAsync(
        string userId, string provider, string key, string? baseUrl,
        CancellationToken ct);

    /// <summary>Remove a key do provider do usuário.</summary>
    Task<bool> DeleteAsync(string userId, string provider, CancellationToken ct);

    /// <summary>
    /// Resolve a key do dono para o provider do modelo pedido —
    /// <c>null</c> quando o usuário não tem key para ele (cai para
    /// BYOR/global). Decripta só para a chamada em voo.
    /// </summary>
    Task<UserLlmKeyCallContext?> ResolveAsync(
        string userId, string? modelProvider, CancellationToken ct);

    /// <summary>
    /// RF-004: ping real com a key salva (<c>GET {base}/models</c>,
    /// timeout 10s, rate-limited). Retorna ok + detalhe sanitizado.
    /// </summary>
    Task<(bool Ok, string? Detail)> TestAsync(
        string userId, string provider, CancellationToken ct);
}

/// <summary>Validação e catálogo do SPEC-134 — compartilhado API/service.</summary>
public static class LlmKeyProviders
{
    private static readonly LlmKeyProviderDescriptor[] All =
    [
        new("openai", "OpenAI", "https://api.openai.com/v1", false),
        new("anthropic", "Anthropic", "https://api.anthropic.com/v1", false),
        new("gemini", "Google Gemini",
            "https://generativelanguage.googleapis.com/v1beta/openai", false),
        new("openai_compat", "OpenAI-compatible", "", true),
    ];

    /// <summary>Catálogo estático (GET /keys/llm/providers).</summary>
    public static IReadOnlyList<LlmKeyProviderDescriptor> Catalog => All;

    /// <summary>Normaliza e valida o id de provider; null = inválido.</summary>
    public static LlmKeyProviderDescriptor? Find(string? provider) =>
        All.FirstOrDefault(p => string.Equals(
            p.Id, provider?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// base_url: https obrigatório; http só em localhost/127.0.0.1/[::1]
    /// (self-hosted dev). Null/empty é ok para providers com default.
    /// </summary>
    public static bool IsValidBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return true;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        return uri.Scheme == Uri.UriSchemeHttp
            && uri.Host is "localhost" or "127.0.0.1" or "::1";
    }
}

/// <summary>Erro de validação do CRUD BYOK (mapeado p/ 400 na API).</summary>
public sealed class LlmKeyValidationException(string code)
    : Exception(code)
{
    /// <summary>Código estável para o response.</summary>
    public string Code => Message;
}