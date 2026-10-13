using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace OpenHandsNET.Cli.Services;

/// <summary>
/// REST client tipado do servidor (V1). Resolução de URL/API key:
/// <c>--server-url</c> > OPENHANDSNET_SERVER_URL > OPENHANDSNET_CLOUD_URL >
/// credencial salva > default local.
/// </summary>
public sealed class CloudApiClient
{
    private const string DefaultUrl = "http://localhost:8080";
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public CloudApiClient(HttpClient http, string baseUrl, string? apiKey = null)
    {
        _http = http;
        BaseUrl = baseUrl.TrimEnd('/');
        if (apiKey is not null)
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    public string BaseUrl { get; }

    /// <summary>Resolve o servidor efetivo (flag > env > store > default).</summary>
    public static async Task<CloudApiClient> CreateAsync(
        string? serverUrl, CredentialStore store,
        HttpClient? http = null, CancellationToken ct = default)
    {
        var url = serverUrl
            ?? Environment.GetEnvironmentVariable("OPENHANDSNET_SERVER_URL")
            ?? Environment.GetEnvironmentVariable("OPENHANDSNET_CLOUD_URL")
            ?? DefaultUrl;
        var saved = await store.GetAsync(url, ct);
        var key = Environment.GetEnvironmentVariable("OPENHANDSNET_API_KEY")
            ?? saved?.ApiKey ?? saved?.AccessToken;
        return new CloudApiClient(http ?? new HttpClient(), url, key);
    }

    /// <summary>POST /app-conversations → start-task.</summary>
    public async Task<JsonElement> CreateConversationAsync(
        string? initialMessage, string? repository, bool run = true,
        bool confirmationMode = false, CancellationToken ct = default)
    {
        object? initial = initialMessage is null ? null : new
        {
            role = "user",
            content = new[] { new { type = "text", text = initialMessage } },
            run,
        };
        var resp = await _http.PostAsJsonAsync(
            $"{BaseUrl}/api/v1/app-conversations", new
            {
                initial_message = initial,
                selected_repository = repository,
                confirmation_mode = confirmationMode,
            }, ct);
        await EnsureAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    public async Task<JsonElement?> GetStartTaskAsync(
        string taskId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(
            $"{BaseUrl}/api/v1/app-conversations/start-tasks/{taskId}", ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<JsonElement>(ct) : null;
    }

    public async Task<JsonElement> ListConversationsAsync(
        CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<JsonElement>(
            $"{BaseUrl}/api/v1/app-conversations/search?limit=50", ct);

    public async Task<JsonElement?> GetConversationAsync(
        string id, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(
            $"{BaseUrl}/api/v1/app-conversations/{id}", ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<JsonElement>(ct) : null;
    }

    public async Task<JsonElement> GetEventsAsync(
        string conversationId, string? pageId = null,
        CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<JsonElement>(
            $"{BaseUrl}/api/v1/conversation/{conversationId}/events/search"
            + $"?limit=100{(pageId is null ? "" : $"&page_id={pageId}")}", ct);

    public async Task SendMessageAsync(
        string conversationId, string text, CancellationToken ct = default) =>
        await EnsureAsync(await _http.PostAsJsonAsync(
            $"{BaseUrl}/api/v1/app-conversations/{conversationId}/send-message",
            new
            {
                role = "user",
                content = new[] { new { type = "text", text } },
                run = true,
            }, ct), ct);

    public async Task DeleteConversationAsync(
        string id, CancellationToken ct = default) =>
        await EnsureAsync(await _http.DeleteAsync(
            $"{BaseUrl}/api/v1/app-conversations/{id}", ct), ct);

    private static async Task EnsureAsync(
        HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
        {
            return;
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"HTTP {(int)resp.StatusCode}: {body[..Math.Min(300, body.Length)]}");
    }
}
