using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.IntegrationTests.Api;

/// <summary>
/// SPEC-18 RF-005/006: <c>switch_profile</c> e <c>switch_acp_model</c> —
/// 404/400/409/200 conforme estado da conversa; recriação de sandbox via
/// start-task sem Docker quando o registro não existe.
/// </summary>
public sealed class SwitchEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-switch-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public SwitchEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath}");
            builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
            builder.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}");
            builder.UseSetting("Sandbox:WorkspaceRoot",
                Path.Combine(Path.GetTempPath(), $"ohn-switch-ws-{Guid.NewGuid():N}"));
        });
        _http = _factory.CreateClient();
    }

    public void Dispose()
    {
        _http.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private async Task<Guid> SeedConversationAsync(
        string status = "Finished", Guid? sandboxId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conv = new OpenHandsNET.Infrastructure.Conversations.ConversationRecord
        {
            Id = Guid.NewGuid(), SandboxId = sandboxId,
            ExecutionStatus = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();
        return conv.Id;
    }

    [Fact]
    public async Task SwitchProfile_NotFound_And_Archived()
    {
        var missing = await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{Guid.NewGuid()}/switch_profile",
            new { profile = "small" });
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var archived = await SeedConversationAsync("Archived");
        var gone = await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{archived}/switch_profile",
            new { profile = "small" });
        gone.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SwitchProfile_Invalid_Name_Returns_400()
    {
        var id = await SeedConversationAsync();
        var resp = await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{id}/switch_profile",
            new { profile = "colossal" });
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SwitchProfile_Starting_Returns_409()
    {
        var id = await SeedConversationAsync("Starting");
        var resp = await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{id}/switch_profile",
            new { profile = "large" });
        resp.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task SwitchProfile_Without_Sandbox_Persists_Profile()
    {
        var id = await SeedConversationAsync();
        var resp = await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{id}/switch_profile",
            new { profile = "large" });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<JsonElement>();
        dto.GetProperty("sandbox_profile").GetString().ShouldBe("large");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Conversations.FindAsync(id))!.SandboxProfile.ShouldBe("large");
    }

    [Fact]
    public async Task SwitchProfile_With_Sandbox_Enqueues_Recreation()
    {
        // SandboxId apontando para registro inexistente: DeleteAsync no-op,
        // start-task WORKING é criado e a conversa volta a Starting.
        var id = await SeedConversationAsync(sandboxId: Guid.NewGuid());
        var resp = await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{id}/switch_profile",
            new { profile = "small" });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conv = await db.Conversations.FindAsync(id);
        conv!.SandboxProfile.ShouldBe("small");
        conv.ExecutionStatus.ShouldBe("Starting");
        conv.SandboxId.ShouldBeNull();
    }

    [Fact]
    public async Task SwitchModel_NotFound_And_Empty()
    {
        var missing = await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{Guid.NewGuid()}/switch_acp_model",
            new { model = "gpt-5" });
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var id = await SeedConversationAsync();
        var empty = await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{id}/switch_acp_model",
            new { model = "  " });
        empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SwitchModel_Persists_For_Next_Run()
    {
        var id = await SeedConversationAsync();
        var resp = await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{id}/switch_acp_model",
            new { model = "claude-sonnet-4" });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<JsonElement>();
        dto.GetProperty("llm_model").GetString().ShouldBe("claude-sonnet-4");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Conversations.FindAsync(id))!.LlmModel
            .ShouldBe("claude-sonnet-4");
    }
}
