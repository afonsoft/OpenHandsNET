using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace OpenHandsNET.IntegrationTests.Api;

/// <summary>
/// SPEC-12 API contract: git credentials CRUD (metadados, nunca valores)
/// e validação de path traversal nos proxies de arquivo.
/// </summary>
public sealed class GitEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-gitapi-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public GitEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath}");
            builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
            builder.UseSetting("Sandbox:DefaultImage", "bash:5");
            builder.UseSetting("Sandbox:WorkspaceRoot",
                Path.Combine(Path.GetTempPath(), $"ohn-gitapi-ws-{Guid.NewGuid():N}"));
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

    [Fact]
    public async Task Credentials_RoundTrip_Metadata_Only()
    {
        var put = await _http.PutAsJsonAsync("/api/v1/git/credentials/github.com",
            new { kind = "token", token = "ghp_secret_token_value" });
        put.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var list = await _http.GetFromJsonAsync<JsonElement>(
            "/api/v1/git/credentials");
        var item = list.EnumerateArray()
            .Single(e => e.GetProperty("host").GetString() == "github.com");
        item.GetProperty("kind").GetString().ShouldBe("token");
        // Nunca expõe o valor.
        item.ToString().ShouldNotContain("ghp_secret_token_value");
        item.TryGetProperty("token", out _).ShouldBeFalse();
        item.TryGetProperty("value", out _).ShouldBeFalse();

        var del = await _http.DeleteAsync("/api/v1/git/credentials/github.com");
        del.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var after = await _http.GetFromJsonAsync<JsonElement>(
            "/api/v1/git/credentials");
        after.EnumerateArray()
            .ShouldNotContain(e => e.GetProperty("host").GetString() == "github.com");
    }

    [Fact]
    public async Task Credentials_Put_Without_Value_Returns_400()
    {
        var resp = await _http.PutAsJsonAsync("/api/v1/git/credentials/gitlab.com",
            new { kind = "token" });
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task File_Proxy_Rejects_Traversal_Or_Outside_Workspace()
    {
        if (!DockerUp())
        {
            return;
        }

        var convId = await CreateReadyConversationAsync();
        foreach (var path in new[] { "/etc/passwd", "/workspace/../etc/passwd" })
        {
            var resp = await _http.GetAsync(
                $"/api/v1/app-conversations/{convId}/file" +
                $"?path={Uri.EscapeDataString(path)}");
            resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"path={path}");
        }
    }

    [Fact]
    public async Task Git_Endpoints_409_Without_Running_Sandbox()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<OpenHandsNET.Infrastructure.Persistence.AppDbContext>();
        var conv = new OpenHandsNET.Infrastructure.Conversations.ConversationRecord
        {
            Id = Guid.NewGuid(), ExecutionStatus = "Running",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();

        var resp = await _http.GetAsync(
            $"/api/v1/app-conversations/{conv.Id}/git/changes");
        resp.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    private async Task<string> CreateReadyConversationAsync()
    {
        var resp = await _http.PostAsJsonAsync("/api/v1/app-conversations", new
        {
            initial_message = new
            {
                role = "user", content = new[] { new { type = "text", text = "hi" } },
            },
        });
        var task = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = task.GetProperty("id").GetString()!;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        JsonElement current = task;
        while (DateTime.UtcNow < deadline)
        {
            current = (await _http.GetFromJsonAsync<JsonElement>(
                $"/api/v1/app-conversations/start-tasks/{taskId}"))!;
            var status = current.GetProperty("status").GetString();
            if (status is "READY" or "ERROR")
            {
                break;
            }

            await Task.Delay(1000);
        }

        current.GetProperty("status").GetString().ShouldBe("READY",
            $"task detail: {current.GetProperty("detail").GetString()}");
        return current.GetProperty("app_conversation_id").GetString()!;
    }

    private static bool DockerUp()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("docker", "info")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true,
                })!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
