using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

using Sandcastle.Application.Agent;
using Sandcastle.Infrastructure.Agent.Routing;
using Sandcastle.Infrastructure.Persistence;

using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

/// <summary>
/// SPEC-134 RF-003/RF-004 + aceite: CRUD <c>/api/v1/keys/llm*</c>
/// owner-scoped (user A ≠ B), saída sempre mascarada (key crua nunca sai),
/// validação de provider/base_url, endpoint de test rate-limited, e a key
/// persiste criptografada no DB.
/// </summary>
public sealed class Spec134UserLlmKeyEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sc-spec134-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly RecordingHandler _handler = new(HttpStatusCode.OK, "{}");

    public Spec134UserLlmKeyEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Database:Provider", "Sqlite");
                b.UseSetting("Database:ConnectionString",
                    $"Data Source={_dbPath};Default Timeout=10");
                b.UseSetting("Observability:OtlpEndpoint", string.Empty);
                b.UseSetting("Sandbox:InstanceOwner",
                    $"test-{Guid.NewGuid():N}");
                b.UseSetting("Auth:Provider", "none");
                b.UseSetting("Auth:UserProvisioningEnabled", "true");
                b.ConfigureServices(services => services.Replace(
                    ServiceDescriptor.Scoped<IUserLlmKeyService>(sp =>
                        new UserLlmKeyService(
                            sp.GetRequiredService<AppDbContext>(),
                            sp.GetRequiredService<IDataProtectionProvider>(),
                            new StubFactory(_handler),
                            sp.GetRequiredService<IMemoryCache>(),
                            NullLogger<UserLlmKeyService>.Instance))));
            });
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (File.Exists(_dbPath)) { File.Delete(_dbPath); }
    }

    private async Task<HttpClient> LoginAsync(string email)
    {
        var http = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = true });
        var res = await http.PostAsJsonAsync("/api/v1/auth/dev-login",
            new { email, displayName = email.Split('@')[0] });
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await http.PostAsJsonAsync("/api/v1/authenticate", new { }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        return http;
    }

    [Fact]
    public async Task Crud_Roundtrip_Always_Masked()
    {
        var http = await LoginAsync("byok-a@example.com");

        var put = await http.PutAsJsonAsync("/api/v1/keys/llm/openai",
            new { key = "sk-abc123def" });
        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await put.Content.ReadAsStringAsync();
        body.ShouldContain("sk-…3def");
        body.ShouldNotContain("sk-abc123def"); // cru nunca sai

        var list = await http.GetFromJsonAsync<JsonElement[]>(
            "/api/v1/keys/llm");
        list!.Length.ShouldBe(1);
        list[0].GetProperty("provider").GetString().ShouldBe("openai");
        list[0].GetProperty("masked_key").GetString().ShouldBe("sk-…3def");

        // DB persiste ciphertext, não plaintext.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.UserLlmKeys.SingleAsync();
        row.KeyCiphertext.ShouldNotBe("sk-abc123def");
        row.KeyCiphertext.ShouldNotContain("sk-live");

        (await http.DeleteAsync("/api/v1/keys/llm/openai"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await http.GetFromJsonAsync<JsonElement[]>("/api/v1/keys/llm"))!
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task Keys_Are_Isolated_Between_Users()
    {
        var httpA = await LoginAsync("byok-iso-a@example.com");
        var httpB = await LoginAsync("byok-iso-b@example.com");

        (await httpA.PutAsJsonAsync("/api/v1/keys/llm/openai",
            new { key = "sk-userA-aaaa" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await httpB.GetFromJsonAsync<JsonElement[]>("/api/v1/keys/llm"))!
            .ShouldBeEmpty();
        (await httpB.DeleteAsync("/api/v1/keys/llm/openai"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Upsert_Validates_Provider_And_BaseUrl()
    {
        var http = await LoginAsync("byok-val@example.com");

        (await http.PutAsJsonAsync("/api/v1/keys/llm/bogus",
            new { key = "sk-x" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await http.PutAsJsonAsync("/api/v1/keys/llm/openai_compat",
            new { key = "sk-x" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await http.PutAsJsonAsync("/api/v1/keys/llm/openai_compat",
            new { key = "sk-x", base_url = "http://evil.example.com" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // http só para localhost — self-hosted dev OK.
        var ok = await http.PutAsJsonAsync("/api/v1/keys/llm/openai_compat",
            new { key = "sk-x", base_url = "http://localhost:4000/v1" });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ok.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("base_url").GetString()
            .ShouldBe("http://localhost:4000/v1");
    }

    [Fact]
    public async Task Providers_Catalog_And_Test_Endpoint()
    {
        var http = await LoginAsync("byok-test@example.com");

        var providers = await http.GetFromJsonAsync<JsonElement[]>(
            "/api/v1/keys/llm/providers");
        providers!.Select(p => p.GetProperty("id").GetString())
            .ShouldBe(["openai", "anthropic", "gemini", "openai_compat"]);

        // Sem key → ok:false, key_not_configured.
        var miss = await http.PostAsync("/api/v1/keys/llm/openai/test", null);
        (await miss.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("detail").GetString().ShouldBe("key_not_configured");

        await http.PutAsJsonAsync("/api/v1/keys/llm/openai",
            new { key = "sk-abc123def" });
        _handler.Status = HttpStatusCode.OK;
        var res = await http.PostAsync("/api/v1/keys/llm/openai/test", null);
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        doc.GetProperty("ok").GetBoolean().ShouldBeTrue();
        _handler.LastPath.ShouldBe("/v1/models");
        _handler.LastAuth.ShouldBe("Bearer sk-abc123def");

        // Rate limit: 5 chamadas/min por user+provider.
        _handler.Status = HttpStatusCode.OK;
        for (var i = 0; i < 4; i++)
            await http.PostAsync("/api/v1/keys/llm/openai/test", null);
        (await http.PostAsync("/api/v1/keys/llm/openai/test", null))
            .StatusCode.ShouldBe((HttpStatusCode)429);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _body;
        public HttpStatusCode Status;
        public string? LastPath;
        public string? LastAuth;

        public RecordingHandler(HttpStatusCode status, string body)
        { Status = status; _body = body; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            LastAuth = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(_body, Encoding.UTF8,
                    "application/json"),
            });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }
}