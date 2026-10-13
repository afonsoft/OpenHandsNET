using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using OpenHandsNET.Domain.Events;

namespace OpenHandsNET.IntegrationTests.Api;

/// <summary>
/// ConversationHub tests: join streams backlog + live events
/// (SPEC-04 RF-005).
/// </summary>
public sealed class SignalRConversationHubTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-hub-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public SignalRConversationHubTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath}");
            builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
            builder.UseSetting("Sandbox:DefaultImage", "bash:5");
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private HubConnection Connect() =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/conversations"),
                o => o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler())
            .Build();

    [Fact]
    public async Task Join_Streams_Live_Events_In_Order()
    {
        await using var conn = Connect();
        await conn.StartAsync();
        var convId = Guid.NewGuid();

        var received = new List<string>();
        var tcs = new TaskCompletionSource();
        var stream = conn.StreamAsync<JsonElement>(
            "JoinConversation", convId, (string?)null);
        var reader = Task.Run(async () =>
        {
            await foreach (var e in stream)
            {
                received.Add(e.GetProperty("id").GetString()!);
                if (received.Count == 2) tcs.TrySetResult();
            }
        });

        using var scope = _factory.Services.CreateScope();
        var events = scope.ServiceProvider
            .GetRequiredService<OpenHandsNET.Application.Events.IEventService>();
        var ids = new List<string>();
        foreach (var text in new[] { "um", "dois" })
        {
            var e = await events.AppendAsync(convId, new MessageEvent
            {
                Source = EventSource.User,
                LlmMessage = JsonSerializer.SerializeToElement(
                    new { role = "user", content = new[] { new { type = "text", text } } }),
            }, CancellationToken.None);
            ids.Add(e.Id);
        }

        var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        done.ShouldBe(tcs.Task, "hub stream timed out");
        received.ShouldBe(ids);
        await conn.StopAsync();
    }
}
