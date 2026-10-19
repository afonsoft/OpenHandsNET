using System.Net;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace OpenHandsNET.IntegrationTests;

/// <summary>
/// SPEC-35 AC: runtime-api gate — sem/​​com X-Runtime-Key inválido → 401;
/// /healthz aberto; fail-closed quando a key não está configurada.
/// Nenhum endpoint toca o daemon aqui (auth bloqueia antes do handler).
/// </summary>
public sealed class RuntimeApiAuthTests
{
    private static WebApplicationFactory<
        OpenHandsNET.RuntimeApi.RuntimeApiProgram> Factory(
        string? key = "test-key") =>
        new WebApplicationFactory<OpenHandsNET.RuntimeApi.RuntimeApiProgram>()
            .WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Sandbox:RemoteApiKey"] = key,
                    ["Sandbox:WarmPoolSize"] = "0",
                })));

    [Fact]
    public async Task Healthz_Open_Without_Key()
    {
        await using var app = Factory();
        var res = await app.CreateClient().GetAsync("/healthz");
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Runtime_Endpoints_Reject_Missing_Or_Wrong_Key()
    {
        await using var app = Factory();
        var http = app.CreateClient();
        (await http.GetAsync("/runtime/v1/sandboxes")).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);

        using var req = new HttpRequestMessage(
            HttpMethod.Get, "/runtime/v1/sandboxes");
        req.Headers.Add("X-Runtime-Key", "wrong");
        (await http.SendAsync(req)).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Fail_Closed_When_Key_Not_Configured()
    {
        await using var app = Factory(key: null);
        var http = app.CreateClient();
        using var req = new HttpRequestMessage(
            HttpMethod.Get, "/runtime/v1/sandboxes");
        req.Headers.Add("X-Runtime-Key", "anything");
        (await http.SendAsync(req)).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }
}