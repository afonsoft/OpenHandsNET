using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using OpenHandsNET.Application.Sandbox;

namespace OpenHandsNET.Sandboxing;

/// <summary>
/// SPEC-35 RF-002: <see cref="ISandboxEngine"/> delegating to a remote
/// <c>openhandsnet-runtime-api</c> over internal REST (<c>/runtime/v1</c>).
/// Selected by <c>Sandbox:Provider=remote</c>. Auth: <c>X-Runtime-Key</c>
/// (internal network only — nunca exposto publicamente).
/// </summary>
public sealed class HttpSandboxEngine(
    IHttpClientFactory httpFactory,
    ILogger<HttpSandboxEngine> logger) : ISandboxEngine
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower),
        },
    };

    /// <inheritdoc />
    public bool IsRemote => true;

    /// <inheritdoc />
    public bool SupportsPause => true;

    private HttpClient Client()
    {
        var http = httpFactory.CreateClient("runtime-api");
        return http;
    }

    /// <inheritdoc />
    public async Task<EngineSandboxInfo> CreateAndStartAsync(
        EngineCreateRequest request, CancellationToken ct)
    {
        var res = await Client().PostAsJsonAsync(
            "/runtime/v1/sandboxes", request, Json, ct);
        await EnsureAsync(res, ct);
        return (await res.Content
            .ReadFromJsonAsync<EngineSandboxInfo>(Json, ct))!;
    }

    /// <inheritdoc />
    public async Task<EngineSandboxInfo?> InspectAsync(
        string containerId, CancellationToken ct)
    {
        var res = await Client().GetAsync(
            $"/runtime/v1/sandboxes/{containerId}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureAsync(res, ct);
        return await res.Content
            .ReadFromJsonAsync<EngineSandboxInfo>(Json, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EngineSandboxInfo>> ListManagedAsync(
        CancellationToken ct) =>
        await GetListAsync("?pool=false", ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<EngineSandboxInfo>> ListPoolAsync(
        CancellationToken ct) =>
        await GetListAsync("?pool=true", ct);

    /// <inheritdoc />
    public async Task<EngineWarmClaim?> ClaimWarmAsync(
        WarmClaimRequest request, CancellationToken ct)
    {
        var res = await Client().PostAsJsonAsync(
            "/runtime/v1/warm/claim", request, Json, ct);
        if (res.StatusCode == HttpStatusCode.NoContent
            || res.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureAsync(res, ct);
        return await res.Content
            .ReadFromJsonAsync<EngineWarmClaim>(Json, ct);
    }

    /// <inheritdoc />
    public Task PauseAsync(string containerId, CancellationToken ct) =>
        PostActionAsync(containerId, "pause", ct);

    /// <inheritdoc />
    public Task UnpauseAsync(string containerId, CancellationToken ct) =>
        PostActionAsync(containerId, "unpause", ct);

    /// <inheritdoc />
    public Task StartAsync(string containerId, CancellationToken ct) =>
        PostActionAsync(containerId, "start", ct);

    /// <inheritdoc />
    public Task StopAsync(string containerId, CancellationToken ct) =>
        PostActionAsync(containerId, "stop", ct);

    /// <inheritdoc />
    public async Task RemoveAsync(
        string containerId, CancellationToken ct,
        Guid? workspaceConversationId = null)
    {
        var uri = $"/runtime/v1/sandboxes/{containerId}" +
            (workspaceConversationId is { } w
                ? $"?workspace={w:N}" : "");
        var res = await Client().DeleteAsync(uri, ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            return; // idempotent delete
        }

        await EnsureAsync(res, ct);
    }

    private async Task<IReadOnlyList<EngineSandboxInfo>> GetListAsync(
        string query, CancellationToken ct)
    {
        var res = await Client().GetAsync($"/runtime/v1/sandboxes{query}", ct);
        await EnsureAsync(res, ct);
        return await res.Content
            .ReadFromJsonAsync<List<EngineSandboxInfo>>(Json, ct) ?? [];
    }

    private async Task PostActionAsync(
        string containerId, string action, CancellationToken ct)
    {
        var res = await Client().PostAsync(
            $"/runtime/v1/sandboxes/{containerId}/{action}", null, ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            throw new SandboxEngineException(
                $"container {containerId} not found on runtime-api",
                notFound: true);
        }

        await EnsureAsync(res, ct);
    }

    private async Task EnsureAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode)
        {
            return;
        }

        var body = await res.Content.ReadAsStringAsync(ct);
        logger.LogWarning(
            "runtime-api {Status}: {Body}", (int)res.StatusCode, body);
        throw new SandboxEngineException(
            $"runtime-api {(int)res.StatusCode}: {body[..Math.Min(200, body.Length)]}",
            notFound: res.StatusCode == HttpStatusCode.NotFound);
    }
}