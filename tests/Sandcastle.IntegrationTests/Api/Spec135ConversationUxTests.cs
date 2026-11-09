using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

/// <summary>
/// SPEC-135: contrato fork/undo/revert-hunks —
/// fork clona eventos e aponta <c>parent_conversation_id</c>; undo faz
/// soft-delete do último turno (idempotente, 409 com run ativa); o
/// endpoint de hunk revert exige sandbox running (409 sem ele).
/// </summary>
public sealed class Spec135ConversationUxTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sc-spec135-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public Spec135ConversationUxTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Database:Provider", "Sqlite");
            b.UseSetting("Database:ConnectionString", $"Data Source={_dbPath};Default Timeout=10");
            b.UseSetting("Observability:OtlpEndpoint", string.Empty);
            b.UseSetting("Sandbox:DefaultImage", "bash:5");
            b.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}");
            b.UseSetting("Sandbox:WorkspaceRoot",
                Path.Combine(Path.GetTempPath(), $"sc-spec135-ws-{Guid.NewGuid():N}"));
        });
        _http = TestAuth.Login(_factory);
    }

    public void Dispose()
    {
        _http.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath)) { File.Delete(_dbPath); }
    }

    private async Task<Guid> SeedConversationAsync(string status)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<Sandcastle.Infrastructure.Persistence.AppDbContext>();
        var conv = new Sandcastle.Infrastructure.Conversations.ConversationRecord
        {
            Id = Guid.NewGuid(),
            ExecutionStatus = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();
        return conv.Id;
    }

    private async Task<IReadOnlyList<string>> AppendUserTurnAsync(
        Guid convId, DateTimeOffset t0, int followups)
    {
        using var scope = _factory.Services.CreateScope();
        var events = scope.ServiceProvider
            .GetRequiredService<Sandcastle.Application.Events.IEventService>();
        var ids = new List<string>();
        var msg = await events.AppendAsync(convId,
            new Sandcastle.Domain.Events.MessageEvent
            {
                Source = Sandcastle.Domain.Events.EventSource.User,
                Timestamp = t0,
                LlmMessage = JsonSerializer.SerializeToElement(new
                {
                    role = "user",
                    content = new[] { new { type = "text", text = "hi" } },
                }),
            }, CancellationToken.None);
        ids.Add(msg.Id);
        for (var i = 1; i <= followups; i++)
        {
            var e = await events.AppendAsync(convId,
                new Sandcastle.Domain.Events.MessageEvent
                {
                    Source = Sandcastle.Domain.Events.EventSource.Agent,
                    Timestamp = t0.AddSeconds(i),
                    LlmMessage = JsonSerializer.SerializeToElement(new
                    {
                        role = "assistant",
                        content = new[] { new { type = "text", text = $"a{i}" } },
                    }),
                }, CancellationToken.None);
            ids.Add(e.Id);
        }

        return ids;
    }

    private async Task<int> EventCountAsync(Guid id)
    {
        var doc = await _http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/conversation/{id}/events/count");
        return doc.GetInt32();
    }

    // ---- fork ----------------------------------------------------------

    [Fact]
    public async Task Fork_Creates_Child_With_Cloned_Events()
    {
        var id = await SeedConversationAsync("Finished");
        var src = await AppendUserTurnAsync(id, DateTimeOffset.UnixEpoch, 2);

        var resp = await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{id}/fork",
            new { title = "child" });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var childId = Guid.Parse(body.GetProperty("id").GetString()!);
        body.GetProperty("parent_conversation_id").GetString()
            .ShouldBe(id.ToString());
        body.GetProperty("start_task_id").GetString().ShouldNotBeNullOrEmpty();

        // Filha visível com parent_conversation_id apontando a origem.
        var conv = await _http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/app-conversations/{childId}");
        conv.GetProperty("parent_conversation_id").GetString()
            .ShouldBe(id.ToString());
        conv.GetProperty("title").GetString().ShouldBe("child");

        // Eventos clonados: 3 msgs + marker conversation_state (forked_from).
        (await EventCountAsync(childId)).ShouldBe(src.Count + 1);
        var page = await _http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/conversation/{childId}/events/search?limit=10");
        var clonedIds = page.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString()!).ToList();
        clonedIds.ShouldNotContain(src[0]); // ULIDs novos
    }

    [Fact]
    public async Task Fork_At_Event_Id_Cuts_The_Clone()
    {
        var id = await SeedConversationAsync("Finished");
        var ids = await AppendUserTurnAsync(id, DateTimeOffset.UnixEpoch, 2);

        var resp = await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{id}/fork",
            new { at_event_id = ids[0] });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var childId = Guid.Parse(body.GetProperty("id").GetString()!);
        // Só o evento de corte + marker.
        (await EventCountAsync(childId)).ShouldBe(2);
    }

    [Fact]
    public async Task Fork_Errors()
    {
        // 404 — conversa inexistente.
        (await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{Guid.NewGuid()}/fork", new { }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // 409 — run ativa.
        var running = await SeedConversationAsync("Running");
        (await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{running}/fork", new { }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // 404 — at_event_id de outra conversa.
        var id = await SeedConversationAsync("Finished");
        var ids = await AppendUserTurnAsync(id, DateTimeOffset.UnixEpoch, 0);
        var other = await SeedConversationAsync("Finished");
        (await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{other}/fork",
            new { at_event_id = ids[0] }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- undo ----------------------------------------------------------

    [Fact]
    public async Task Undo_Truncates_Last_User_Turn_And_Is_Idempotent()
    {
        var id = await SeedConversationAsync("Finished");
        await AppendUserTurnAsync(id, DateTimeOffset.UnixEpoch, 1);
        await AppendUserTurnAsync(id, DateTimeOffset.UnixEpoch.AddSeconds(10), 1);
        (await EventCountAsync(id)).ShouldBe(4);

        var resp = await _http.PostAsync(
            $"/api/v1/app-conversations/{id}/undo", null);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("truncated_events").GetInt32().ShouldBe(2);

        // Stream ativo esconde os truncados (soft-delete); ledger fica.
        (await EventCountAsync(id)).ShouldBe(3); // 2 restantes + marker turn_undo

        // Segundo undo desfaz o turno anterior; terceiro → no_turn.
        (await _http.PostAsync(
            $"/api/v1/app-conversations/{id}/undo", null))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _http.PostAsync(
            $"/api/v1/app-conversations/{id}/undo", null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Undo_Errors()
    {
        (await _http.PostAsync(
            $"/api/v1/app-conversations/{Guid.NewGuid()}/undo", null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var running = await SeedConversationAsync("Running");
        (await _http.PostAsync(
            $"/api/v1/app-conversations/{running}/undo", null))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var empty = await SeedConversationAsync("Finished");
        (await _http.PostAsync(
            $"/api/v1/app-conversations/{empty}/undo", null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound); // no_turn
    }

    // ---- revert-hunks --------------------------------------------------

    [Fact]
    public async Task RevertHunks_Requires_Running_Sandbox()
    {
        var id = await SeedConversationAsync("Finished");
        (await _http.PostAsJsonAsync(
            $"/api/v1/app-conversations/{id}/files/revert-hunks",
            new { path = "a.cs", hunk_indexes = new[] { 0 } }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }
}