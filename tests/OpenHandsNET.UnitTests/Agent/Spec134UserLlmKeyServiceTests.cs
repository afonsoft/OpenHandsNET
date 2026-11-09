using System.Net;
using System.Text;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using OpenHandsNET.Application.Agent;
using OpenHandsNET.Infrastructure.Agent.Routing;
using OpenHandsNET.Infrastructure.Persistence;

using Shouldly;

using Xunit;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>
/// SPEC-134 RF-001/RF-002/RF-003/RF-004: ciclo de vida das BYOK user keys —
/// upsert persiste só ciphertext e devolve masked; validação de provider e
/// base_url (https; http só localhost); resolução por match de provider com
/// isolamento por user_id; test endpoint com rate-limit.
/// </summary>
public sealed class Spec134UserLlmKeyServiceTests
{
    private static async Task<AppDbContext> NewDbAsync(SqliteConnection conn)
    {
        var sc = new ServiceCollection();
        sc.AddDbContext<AppDbContext>(o => o.UseSqlite(conn)
            .UseSnakeCaseNamingConvention());
        var sp = sc.BuildServiceProvider();
        var db = sp.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static UserLlmKeyService NewService(AppDbContext db,
        IHttpClientFactory? http = null, IMemoryCache? cache = null) =>
        new(db, new EphemeralDataProtectionProvider(),
            http ?? new StubFactory(new RecordingHandler(HttpStatusCode.OK, "{}")),
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            NullLogger<UserLlmKeyService>.Instance);

    [Fact]
    public async Task Upsert_Stores_Ciphertext_And_Returns_Masked()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = await NewDbAsync(conn);
        var svc = NewService(db);

        var info = await svc.UpsertAsync(
            "u1", "openai", "sk-live-abcdef1234", null, CancellationToken.None);

        info.Provider.ShouldBe("openai");
        info.MaskedKey.ShouldBe("sk-…1234");
        info.MaskedKey.ShouldNotContain("abcdef");
        var row = db.UserLlmKeys.Single();
        row.KeyCiphertext.ShouldNotBe("sk-live-abcdef1234"); // nunca plaintext
        row.BaseUrl.ShouldBeNull(); // openai usa default do catálogo
    }

    [Fact]
    public async Task Upsert_Second_Time_Rotates_Without_Duplicate()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = await NewDbAsync(conn);
        var svc = NewService(db);

        await svc.UpsertAsync("u1", "openai", "sk-old-0000", null,
            CancellationToken.None);
        var info = await svc.UpsertAsync(
            "u1", "openai", "sk-new-9999", null, CancellationToken.None);

        info.MaskedKey.ShouldBe("sk-…9999");
        db.UserLlmKeys.Count().ShouldBe(1);
        (await svc.ResolveAsync("u1", "openai", CancellationToken.None))!
            .Key.ShouldBe("sk-new-9999");
    }

    [Theory]
    [InlineData("bogus", "sk-x", null, "unknown_provider")]
    [InlineData("openai", "", null, "key_required")]
    [InlineData("openai", "   ", null, "key_required")]
    [InlineData("openai_compat", "sk-x", null, "base_url_required")]
    [InlineData("openai_compat", "sk-x", "http://evil.example.com",
        "base_url_invalid")]
    [InlineData("openai_compat", "sk-x", "notaurl", "base_url_invalid")]
    public async Task Upsert_Validates(
        string provider, string key, string? baseUrl, string code)
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = await NewDbAsync(conn);
        var svc = NewService(db);

        var ex = await Should.ThrowAsync<LlmKeyValidationException>(() =>
            svc.UpsertAsync("u1", provider, key, baseUrl,
                CancellationToken.None));
        ex.Code.ShouldBe(code);
    }

    [Theory]
    [InlineData("https://my-llm.example.com/v1", true)]
    [InlineData("http://localhost:4000", true)]
    [InlineData("http://127.0.0.1:4000", true)]
    [InlineData("http://192.168.1.5:4000", false)]
    public void BaseUrl_Rule(string url, bool ok) =>
        LlmKeyProviders.IsValidBaseUrl(url).ShouldBe(ok);

    [Fact]
    public async Task Resolve_Matches_Model_Provider()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = await NewDbAsync(conn);
        var svc = NewService(db);
        await svc.UpsertAsync("u1", "openai", "sk-user-aaaa", null,
            CancellationToken.None);
        await svc.UpsertAsync("u1", "openai_compat", "sk-user-bbbb",
            "https://my-llm.example.com/v1", CancellationToken.None);

        var openai = await svc.ResolveAsync("u1", "openai",
            CancellationToken.None);
        openai.ShouldNotBeNull();
        openai.Key.ShouldBe("sk-user-aaaa");
        openai.BaseUrl.ShouldBe("https://api.openai.com/v1");

        var compat = await svc.ResolveAsync("u1", "openai_compat",
            CancellationToken.None);
        compat!.BaseUrl.ShouldBe("https://my-llm.example.com/v1");

        // Provider sem key do usuário / provider desconhecido → null.
        (await svc.ResolveAsync("u1", "anthropic", CancellationToken.None))
            .ShouldBeNull();
        (await svc.ResolveAsync("u1", null, CancellationToken.None))
            .ShouldBeNull();
        (await svc.ResolveAsync("u1", "configured", CancellationToken.None))
            .ShouldBeNull();
    }

    [Fact]
    public async Task Keys_Are_Isolated_Per_User()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = await NewDbAsync(conn);
        var svc = NewService(db);
        await svc.UpsertAsync("u1", "openai", "sk-userA-aaaa", null,
            CancellationToken.None);

        (await svc.ListAsync("u2", CancellationToken.None)).ShouldBeEmpty();
        (await svc.ResolveAsync("u2", "openai", CancellationToken.None))
            .ShouldBeNull();
        (await svc.DeleteAsync("u2", "openai", CancellationToken.None))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task Test_Pings_Models_With_Saved_Key()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = await NewDbAsync(conn);
        var handler = new RecordingHandler(HttpStatusCode.OK,
            """{"data":[]}""");
        var svc = NewService(db, new StubFactory(handler));
        await svc.UpsertAsync("u1", "openai", "sk-live-wxyz", null,
            CancellationToken.None);

        var (ok, detail) = await svc.TestAsync("u1", "openai",
            CancellationToken.None);

        ok.ShouldBeTrue();
        detail.ShouldBeNull();
        handler.LastPath.ShouldBe("/v1/models");
        handler.LastAuth.ShouldBe("Bearer sk-live-wxyz");
    }

    [Fact]
    public async Task Test_Unreachable_And_Missing_Key()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = await NewDbAsync(conn);
        var svc = NewService(db, new StubFactory(
            new RecordingHandler(new HttpRequestException("down"))));

        (await svc.TestAsync("u1", "openai", CancellationToken.None))
            .ShouldBe((false, "key_not_configured"));

        await svc.UpsertAsync("u1", "openai", "sk-x-1111", null,
            CancellationToken.None);
        (await svc.TestAsync("u1", "openai", CancellationToken.None))
            .ShouldBe((false, "unreachable"));
    }

    [Fact]
    public async Task Test_Is_Rate_Limited()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = await NewDbAsync(conn);
        var svc = NewService(db, new StubFactory(
            new RecordingHandler(HttpStatusCode.OK, "{}")));
        await svc.UpsertAsync("u1", "openai", "sk-x-2222", null,
            CancellationToken.None);

        for (var i = 0; i < 5; i++)
        {
            (await svc.TestAsync("u1", "openai", CancellationToken.None))
                .Ok.ShouldBeTrue();
        }

        (await svc.TestAsync("u1", "openai", CancellationToken.None))
            .ShouldBe((false, "rate_limited"));
    }

    [Theory]
    [InlineData("sk-live-abcdef1234", "sk-…1234")]
    [InlineData("x", "sk-…x")]
    public void Mask_Format(string key, string masked) =>
        UserLlmKeyService.Mask(key).ShouldBe(masked);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode? _status;
        private readonly string? _body;
        private readonly Exception? _error;
        public string? LastPath;
        public string? LastAuth;

        public RecordingHandler(HttpStatusCode status, string body)
        { _status = status; _body = body; }

        public RecordingHandler(Exception error) => _error = error;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            LastAuth = request.Headers.Authorization?.ToString();
            if (_error is not null) throw _error;
            return Task.FromResult(new HttpResponseMessage(_status!.Value)
            {
                Content = new StringContent(_body ?? "", Encoding.UTF8,
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