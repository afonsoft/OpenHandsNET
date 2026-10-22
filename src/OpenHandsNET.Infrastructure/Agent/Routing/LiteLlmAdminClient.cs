using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Infrastructure.Agent.Routing;

/// <summary>
/// SPEC-50 RF-001: LiteLLM admin API — virtual keys under
/// <c>LlmProxy:BaseUrl</c>/<c>LlmProxy:MasterKey</c>. Any transport or
/// non-2xx failure surfaces as <see cref="LiteLlmUnavailableException"/>
/// so callers fail closed (503), never silently fall back.
/// </summary>
public sealed class LiteLlmAdminClient(
    IHttpClientFactory httpFactory,
    IOptions<LlmProxyOptions> options,
    ILogger<LiteLlmAdminClient> logger) : ILiteLlmAdminClient
{
    private static readonly JsonSerializerOptions Json = new(
        JsonSerializerOptions.Web);

    /// <inheritdoc />
    public async Task<LiteLlmVirtualKey> CreateVirtualKeyAsync(
        string userId, string? orgId, decimal budgetUsd,
        IReadOnlyList<string> models, CancellationToken ct)
    {
        var res = await SendAsync(HttpMethod.Post, "/key/generate", new
        {
            user_id = userId,
            team_id = orgId,
            max_budget = budgetUsd,
            models = models.Count > 0 ? models : null,
            metadata = new { source = "openhandsnet-byor" },
        }, ct);
        using var doc = JsonDocument.Parse(res);
        var root = doc.RootElement;
        var key = root.TryGetProperty("key", out var k) ? k.GetString() : null;
        string? keyId;
        if (root.TryGetProperty("key_id", out var id))
        {
            keyId = id.GetString();
        }
        else if (root.TryGetProperty("token_id", out var t))
        {
            keyId = t.GetString();
        }
        else
        {
            keyId = key;
        }
        var budgetId = root.TryGetProperty("budget_id", out var b)
            ? b.GetString() : null;
        if (string.IsNullOrEmpty(key))
        {
            throw new LiteLlmUnavailableException(
                "LiteLLM /key/generate returned no key");
        }

        return new LiteLlmVirtualKey(key, keyId ?? key[..16], budgetId);
    }

    /// <inheritdoc />
    public async Task DeleteKeyAsync(string keyId, CancellationToken ct) =>
        await SendAsync(HttpMethod.Post, "/key/delete",
            new { keys = new[] { keyId } }, ct);

    /// <inheritdoc />
    public async Task UpdateKeyBudgetAsync(
        string keyId, decimal budgetUsd, CancellationToken ct) =>
        await SendAsync(HttpMethod.Post, "/key/update",
            new { key = keyId, max_budget = budgetUsd }, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListModelsAsync(
        CancellationToken ct)
    {
        var res = await SendAsync(HttpMethod.Get, "/models", null, ct);
        using var doc = JsonDocument.Parse(res);
        var models = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            foreach (var m in data.EnumerateArray())
            {
                if (m.TryGetProperty("id", out var id)
                    && id.GetString() is { } mid)
                {
                    models.Add(mid);
                }
            }
        }

        return models;
    }

    private async Task<string> SendAsync(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var opt = options.Value;
        if (string.IsNullOrEmpty(opt.BaseUrl) || string.IsNullOrEmpty(opt.MasterKey))
        {
            throw new LiteLlmUnavailableException("LiteLLM proxy not configured");
        }

        var url = opt.BaseUrl.TrimEnd('/') + path;
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new("Bearer", opt.MasterKey);
        if (body is not null)
        {
            req.Content = JsonContent.Create(body, options: Json);
        }

        HttpResponseMessage res;
        try
        {
            res = await httpFactory.CreateClient("litellm").SendAsync(req, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException
            or TaskCanceledException or OperationCanceledException)
        {
            logger.LogWarning(ex, "LiteLLM unreachable: {Url}", url);
            throw new LiteLlmUnavailableException(
                "LiteLLM proxy unreachable", ex);
        }

        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "LiteLLM {Method} {Path} → {Status}", method, path, res.StatusCode);
            throw new LiteLlmUnavailableException(
                $"LiteLLM {(int)res.StatusCode}: {text[..Math.Min(200, text.Length)]}");
        }

        return text;
    }
}