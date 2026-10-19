using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Infrastructure.Conversations;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.IntegrationTests.Api;

/// <summary>
/// SPEC-44: contrato dos endpoints de attention — inbox
/// (<c>GET /app-conversations/attention</c>), filtro
/// <c>?attention=1</c>, dismiss com guard de confirmation e exclusão de
/// arquivadas.
/// </summary>
public sealed class Spec44AttentionEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-attn-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public Spec44AttentionEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Database:Provider", "Sqlite");
                builder.UseSetting("Database:ConnectionString",
                    $"Data Source={_dbPath}");
                builder.UseSetting("Observability:OtlpEndpoint",
                    string.Empty);
                builder.UseSetting("Sandbox:InstanceOwner",
                    $"test-{Guid.NewGuid():N}");
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

    private async Task<ConversationRecord> SeedAsync(
        string? kind, string status = "Finished",
        long? atTicks = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conv = new ConversationRecord
        {
            Id = Guid.NewGuid(),
            Title = $"conv-{kind ?? "none"}",
            ExecutionStatus = status,
            NeedsAttentionKind = kind,
            NeedsAttentionAtTicks = atTicks
                ?? (kind is null ? null : DateTimeOffset.UtcNow.Ticks),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();
        return conv;
    }

    // AC: GET /attention retorna count + items com kind/excerpt.
    [Fact]
    public async Task Attention_List_Returns_Items_With_Kind()
    {
        var a = await SeedAsync("confirmation");
        await SeedAsync("question");
        await SeedAsync(null); // sem attention — fora do inbox

        var dto = await _http.GetFromJsonAsync<JsonElement>(
            "/api/v1/app-conversations/attention");
        dto.GetProperty("count").GetInt32().ShouldBe(2);
        var items = dto.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("conversation_id").GetString())
            .ShouldContain(a.Id.ToString());
        items.Select(i => i.GetProperty("kind").GetString())
            .ShouldBe(new[] { "confirmation", "question" },
                ignoreOrder: true);
    }

    // AC: arquivadas nunca aparecem no inbox.
    [Fact]
    public async Task Attention_Excludes_Archived()
    {
        await SeedAsync("failed", status: "Archived");
        var dto = await _http.GetFromJsonAsync<JsonElement>(
            "/api/v1/app-conversations/attention");
        dto.GetProperty("count").GetInt32().ShouldBe(0);
    }

    // AC: ?attention=1 no /search filtra; item DTO expõe needs_attention.
    [Fact]
    public async Task Search_Attention_Filter_And_Dto_Flag()
    {
        var flagged = await SeedAsync("failed");
        await SeedAsync(null);

        var page = await _http.GetFromJsonAsync<JsonElement>(
            "/api/v1/app-conversations/search?attention=1&limit=50");
        var items = page.GetProperty("items").EnumerateArray().ToList();
        items.ShouldHaveSingleItem()
            .GetProperty("id").GetString().ShouldBe(flagged.Id.ToString());
        items[0].GetProperty("needs_attention").GetString()
            .ShouldBe("failed");
    }

    // AC: dismiss em confirmation → 409; failed/question → 204 + limpa.
    [Fact]
    public async Task Dismiss_Confirmation_Conflicts_Others_Clear()
    {
        var conf = await SeedAsync("confirmation");
        var resp = await _http.PostAsync(
            $"/api/v1/app-conversations/{conf.Id}/attention/dismiss", null);
        resp.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var failed = await SeedAsync("failed");
        var ok = await _http.PostAsync(
            $"/api/v1/app-conversations/{failed.Id}/attention/dismiss", null);
        ok.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Limpou a projeção — sai do inbox.
        var dto = await _http.GetFromJsonAsync<JsonElement>(
            "/api/v1/app-conversations/attention");
        dto.GetProperty("count").GetInt32().ShouldBe(1); // só o confirmation
    }

    // AC: dismiss de conversa inexistente → 404.
    [Fact]
    public async Task Dismiss_Missing_Returns_404()
    {
        var resp = await _http.PostAsync(
            $"/api/v1/app-conversations/{Guid.NewGuid()}/attention/dismiss",
            null);
        resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}