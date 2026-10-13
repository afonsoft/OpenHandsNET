using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace OpenHandsNET.IntegrationTests.Api;

/// <summary>
/// SPEC-13: /metrics expõe contadores prometheus; /api/v1/readiness devolve
/// breakdown db/docker/llm.
/// </summary>
public sealed class ObservabilityTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-obs-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public ObservabilityTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath}");
            builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
            builder.UseSetting("Sandbox:WorkspaceRoot",
                Path.Combine(Path.GetTempPath(), $"ohn-obs-ws-{Guid.NewGuid():N}"));
            builder.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}");
        });
        _http = _factory.CreateClient();
    }

    public void Dispose()
    {
        _http.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task Metrics_Exposes_OpenHandsNet_Counters()
    {
        // Counters com label só aparecem após o primeiro Inc — cria eventos.
        await _http.PostAsJsonAsync("/api/v1/app-conversations", new
        {
            initial_message = new
            {
                role = "user",
                content = new[] { new { type = "text", text = "hi" } },
                run = false,
            },
        });

        var deadline = DateTime.UtcNow.AddSeconds(30);
        var body = "";
        while (DateTime.UtcNow < deadline)
        {
            var resp = await _http.GetAsync("/metrics");
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            body = await resp.Content.ReadAsStringAsync();
            if (body.Contains("openhandsnet_events_appended{"))
            {
                break;
            }

            await Task.Delay(500);
        }

        body.ShouldContain("openhandsnet_events_appended{kind=\"MessageEvent\"}");
    }

    [Fact]
    public async Task Readiness_Returns_Check_Breakdown()
    {
        var resp = await _http.GetAsync("/api/v1/readiness");
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var checks = doc.GetProperty("checks");
        checks.TryGetProperty("db", out _).ShouldBeTrue();
        checks.TryGetProperty("docker", out _).ShouldBeTrue();
        checks.TryGetProperty("llm", out _).ShouldBeTrue();
        ((int)resp.StatusCode is 200 or 503).ShouldBeTrue();
    }
}
