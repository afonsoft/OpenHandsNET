using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Infrastructure.Agent.Routing;

/// <summary>
/// Model catalog backed by the gateway's OpenAI-compatible
/// <c>GET {BaseUrl}/models</c> endpoint (SPEC-23 RF-001). Parses OmniRoute
/// extensions (<c>context_window</c>, <c>provider</c>, <c>pricing</c>) when
/// present. Fail-open: a failed discovery serves the last cache snapshot and,
/// with none, degrades to the configured model alone — never blocks a run.
/// </summary>
public sealed class OmniRouteModelCatalog(
    IHttpClientFactory httpFactory,
    IMemoryCache cache,
    IOptions<AgentLlmOptions> options,
    ILogger<OmniRouteModelCatalog> logger) : IModelCatalog
{
    private const string CacheKey = "omniroute:model-catalog";

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelInfo>> ListAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<ModelInfo>? hit)
            && hit is not null)
        {
            return hit;
        }

        var discovered = await DiscoverAsync(ct);
        var effective = ApplyOverrides(discovered);
        cache.Set(CacheKey, effective,
            TimeSpan.FromSeconds(options.Value.CatalogCacheSeconds));
        return effective;
    }

    /// <inheritdoc />
    public async Task<ModelInfo?> GetAsync(string modelId, CancellationToken ct) =>
        (await ListAsync(ct)).FirstOrDefault(m =>
            string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public ModelInfo? TryGetCached(string modelId)
    {
        if (!cache.TryGetValue(CacheKey, out IReadOnlyList<ModelInfo>? hit)
            || hit is null)
        {
            return null;
        }

        return ApplyOverrides(hit).FirstOrDefault(m =>
            string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<ModelInfo>> DiscoverAsync(
        CancellationToken ct)
    {
        var cfg = options.Value;
        if (string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            return [new ModelInfo(cfg.Model, "configured")];
        }

        try
        {
            var client = httpFactory.CreateClient("agent-llm");
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{cfg.BaseUrl.TrimEnd('/')}/models");
            if (!string.IsNullOrEmpty(cfg.ApiKey))
            {
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
            }

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Model catalog → HTTP {Status}",
                    (int)response.StatusCode);
                return Fallback();
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream,
                cancellationToken: ct);
            var models = ParseModels(doc.RootElement);
            return models.Count > 0 ? models : Fallback();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Model catalog discovery failed — fail-open");
            return Fallback();
        }
    }

    private IReadOnlyList<ModelInfo> Fallback()
    {
        // Último snapshot conhecido; sem ele, só o modelo configurado.
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<ModelInfo>? stale)
            && stale is { Count: > 0 })
        {
            return stale;
        }

        return [new ModelInfo(options.Value.Model, "configured")];
    }

    private IReadOnlyList<ModelInfo> ApplyOverrides(IReadOnlyList<ModelInfo> models)
    {
        var overrides = options.Value.ContextWindowOverrides;
        if (overrides.Count == 0)
        {
            return models;
        }

        return models.Select(m =>
            overrides.TryGetValue(m.Id, out var win)
                ? m with { ContextWindow = win }
                : m).ToList();
    }

    private static List<ModelInfo> ParseModels(JsonElement root)
    {
        var list = new List<ModelInfo>();
        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idEl)
                || idEl.GetString() is not { Length: > 0 } id)
            {
                continue;
            }

            var provider = GetStr(item, "provider")
                ?? GetStr(item, "owned_by") ?? "gateway";
            var window = GetInt(item, "context_window")
                ?? GetInt(item, "context_length")
                ?? GetInt(item, "max_input_tokens");
            ModelPricing? pricing = null;
            if (item.TryGetProperty("pricing", out var pr)
                && pr.ValueKind == JsonValueKind.Object)
            {
                pricing = new ModelPricing(
                    GetDec(pr, "prompt") ?? GetDec(pr, "input"),
                    GetDec(pr, "completion") ?? GetDec(pr, "output"));
            }

            list.Add(new ModelInfo(id, provider, window, pricing));
        }

        return list;
    }

    private static string? GetStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static int? GetInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : null;

    private static decimal? GetDec(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDecimal() : null;
}
