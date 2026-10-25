using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Domain.Sandbox;

namespace OpenHandsNET.IntegrationTests.Api;

/// <summary>
/// API V1 contract tests: async start-task flow, send-message error codes,
/// events search pagination (SPEC-04 AC).
/// </summary>
public sealed class AppConversationApiTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-api-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public AppConversationApiTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath};Default Timeout=10");
            builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
            builder.UseSetting("Sandbox:DefaultImage", "bash:5");
            builder.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}");
            builder.UseSetting("Sandbox:WorkspaceRoot",
                Path.Combine(Path.GetTempPath(), $"ohn-api-ws-{Guid.NewGuid():N}"));
        });
        _http = TestAuth.Login(_factory);
    }

    public void Dispose()
    {
        _http.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static bool DockerUp()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("docker", "info")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                })!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // AC: POST → task WORKING; polling → READY com app_conversation_id.
    [Fact]
    public async Task Create_Task_Flows_To_Ready()
    {
        if (!DockerUp()) return;
        var resp = await _http.PostAsJsonAsync("/api/v1/app-conversations", new
        {
            initial_message = new
            {
                role = "user",
                content = new[] { new { type = "text", text = "hello" } },
            },
        });
        resp.StatusCode.ShouldBe(HttpStatusCode.Created);
        var task = await resp.Content.ReadFromJsonAsync<JsonElement>();
        task.GetProperty("status").GetString().ShouldBe("WORKING");
        var taskId = task.GetProperty("id").GetString()!;
        task.GetProperty("app_conversation_id").GetString().ShouldNotBeNullOrEmpty();

        // Poll até READY (worker real cria container bash:5).
        var deadline = DateTime.UtcNow.AddSeconds(60);
        JsonElement current = task;
        while (DateTime.UtcNow < deadline)
        {
            var polled = await _http.GetFromJsonAsync<JsonElement>(
                $"/api/v1/app-conversations/start-tasks/{taskId}");
            current = polled;
            var status = polled.GetProperty("status").GetString();
            if (status is "READY" or "ERROR")
            {
                break;
            }

            await Task.Delay(1000);
        }

        current.GetProperty("status").GetString().ShouldBe("READY",
            $"task detail: {current.GetProperty("detail").GetString()}");
        current.GetProperty("sandbox_id").GetString().ShouldNotBeNullOrEmpty();
    }

    // AC: send-message com sandbox ≠ RUNNING → 409 com detail upstream.
    [Fact]
    public async Task SendMessage_On_Missing_Sandbox_Returns_409()
    {
        // Conversa criada direto no banco sem sandbox → 409.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<OpenHandsNET.Infrastructure.Persistence.AppDbContext>();
        var conv = new OpenHandsNET.Infrastructure.Conversations.ConversationRecord
        {
            Id = Guid.NewGuid(),
            ExecutionStatus = "Running",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();

        var resp = await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{conv.Id}/send-message",
            new { role = "user", content = new[] { new { type = "text", text = "hi" } } });
        resp.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("detail").GetString()!
            .ShouldContain("Sandbox is not running");
    }

    [Fact]
    public async Task SendMessage_NotFound_And_Gone()
    {
        var missing = await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{Guid.NewGuid()}/send-message",
            new { role = "user", content = Array.Empty<object>() });
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<OpenHandsNET.Infrastructure.Persistence.AppDbContext>();
        var conv = new OpenHandsNET.Infrastructure.Conversations.ConversationRecord
        {
            Id = Guid.NewGuid(),
            ExecutionStatus = "Archived",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();

        var gone = await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{conv.Id}/send-message",
            new { role = "user", content = Array.Empty<object>() });
        gone.StatusCode.ShouldBe(HttpStatusCode.Gone);
    }

    // AC: events/search pagina via next_page_id.
    [Fact]
    public async Task Events_Search_Paginates_Via_Next_Page_Id()
    {
        using var scope = _factory.Services.CreateScope();
        var events = scope.ServiceProvider
            .GetRequiredService<OpenHandsNET.Application.Events.IEventService>();
        var convId = Guid.NewGuid();
        var t0 = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < 15; i++)
        {
            await events.AppendAsync(convId, new OpenHandsNET.Domain.Events.MessageEvent
            {
                Source = OpenHandsNET.Domain.Events.EventSource.User,
                Timestamp = t0.AddSeconds(i),
                LlmMessage = JsonSerializer.SerializeToElement(
                    new { role = "user", content = new[] { new { type = "text", text = $"m{i}" } } }),
            }, CancellationToken.None);
        }

        var page1 = await _http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/conversation/{convId}/events/search?kind__eq=MessageEvent&limit=10");
        page1.GetProperty("items").GetArrayLength().ShouldBe(10);
        var cursor = page1.GetProperty("next_page_id").GetString();
        cursor.ShouldNotBeNullOrEmpty();

        var page2 = await _http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/conversation/{convId}/events/search?kind__eq=MessageEvent&limit=10&page_id={cursor}");
        page2.GetProperty("items").GetArrayLength().ShouldBe(5);
        page2.GetProperty("next_page_id").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // AC: batch ?ids= retorna array.
    [Fact]
    public async Task Batch_Get_By_Ids_Returns_Array()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<OpenHandsNET.Infrastructure.Persistence.AppDbContext>();
        var convs = Enumerable.Range(0, 3).Select(_ =>
            new OpenHandsNET.Infrastructure.Conversations.ConversationRecord
            {
                Id = Guid.NewGuid(),
                ExecutionStatus = "Running",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            }).ToList();
        db.Conversations.AddRange(convs);
        await db.SaveChangesAsync();

        var ids = string.Join(',', convs.Select(c => c.Id));
        var resp = await _http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/app-conversations?ids={ids}");
        resp.GetArrayLength().ShouldBe(3);
    }
}