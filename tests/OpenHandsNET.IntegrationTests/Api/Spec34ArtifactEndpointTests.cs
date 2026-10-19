using System.Net;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Application.Artifacts;

using Shouldly;

namespace OpenHandsNET.IntegrationTests.Api;

/// <summary>
/// SPEC-34 AC: GET /app-conversations/{id}/artifacts/{ref} — ref opaco
/// ws:, auth de dono (404 para conversa inexistente), Range 206 para
/// previews, traversal/outra conversa rejeitados.
/// </summary>
public sealed class Spec34ArtifactEndpointTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-artapi-{Guid.NewGuid():N}.db");
    private readonly string _artRoot = Path.Combine(
        Path.GetTempPath(), $"ohn-artapi-{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public Spec34ArtifactEndpointTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Database:Provider", "Sqlite");
                builder.UseSetting(
                    "Database:ConnectionString", $"Data Source={_dbPath}");
                builder.UseSetting("Observability:OtlpEndpoint",
                    string.Empty);
                builder.UseSetting("Sandbox:InstanceOwner",
                    $"test-{Guid.NewGuid():N}");
                builder.UseSetting("Artifacts:WorkspaceRoot", _artRoot);
            });
        _http = _factory.CreateClient();
    }

    public void Dispose()
    {
        _http.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private async Task<Guid> SeedConversationAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<
            Infrastructure.Persistence.AppDbContext>();
        var conv = new Infrastructure.Conversations.ConversationRecord
        {
            Id = Guid.NewGuid(),
            ExecutionStatus = "Running",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();
        return conv.Id;
    }

    private async Task<string> PutArtifactAsync(Guid conv, string text)
    {
        var store = _factory.Services
            .GetRequiredService<IArtifactStore>();
        return await store.PutAsync(conv, "obs.txt",
            System.Text.Encoding.UTF8.GetBytes(text),
            CancellationToken.None);
    }

    [Fact]
    public async Task Artifact_Get_Returns_Content_And_Range()
    {
        var conv = await SeedConversationAsync();
        var artifactRef = await PutArtifactAsync(conv, "0123456789abcdef");

        var full = await _http.GetAsync(
            $"/api/v1/app-conversations/{conv}/artifacts/{artifactRef}");
        full.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await full.Content.ReadAsStringAsync())
            .ShouldBe("0123456789abcdef");

        // Range bytes=0-9 → 206 + 10 bytes (previews do client).
        var req = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/app-conversations/{conv}/artifacts/{artifactRef}");
        req.Headers.Range = new System.Net.Http.Headers
            .RangeHeaderValue(0, 9);
        var ranged = await _http.SendAsync(req);
        ranged.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        (await ranged.Content.ReadAsStringAsync())
            .ShouldBe("0123456789");
    }

    [Fact]
    public async Task Artifact_Unknown_Conversation_Or_Ref_Is_404()
    {
        var conv = await SeedConversationAsync();
        var artifactRef = await PutArtifactAsync(conv, "secret-data");

        // Conversa inexistente → 404 (a checagem de dono é o GetAsync).
        (await _http.GetAsync(
            $"/api/v1/app-conversations/{Guid.NewGuid()}/artifacts/{artifactRef}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Ref de outra conversa → 404 (não vaza cross-conversation).
        var other = await SeedConversationAsync();
        (await _http.GetAsync(
            $"/api/v1/app-conversations/{other}/artifacts/{artifactRef}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Traversal no ref → 404 (não 400/500 — o store rejeita).
        (await _http.GetAsync(
            $"/api/v1/app-conversations/{conv}/artifacts/ws:{conv}/..%2Fescape"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Ref inexistente na conversa certa → 404.
        (await _http.GetAsync(
            $"/api/v1/app-conversations/{conv}/artifacts/ws:{conv}/nope.txt"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}