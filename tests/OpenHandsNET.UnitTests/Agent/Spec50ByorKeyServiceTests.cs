using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Agent;
using OpenHandsNET.Infrastructure.Agent.Routing;
using OpenHandsNET.Infrastructure.Persistence;

using Shouldly;

using Xunit;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>
/// SPEC-50 RF-002: lifecycle de virtual keys — refresh retorna plaintext
/// uma vez (persistido só ciphertext DataProtection), rotação revoga a
/// anterior no proxy, revoke local+remoto, permitted com cache 60s e
/// modo desligado falha fechado.
/// </summary>
public sealed class Spec50ByorKeyServiceTests
{
    private static LlmProxyOptions EnabledOptions => new()
    {
        BaseUrl = "http://litellm:4000",
        MasterKey = "mk-test",
        Mode = "hybrid",
        DefaultBudgetUsd = 25m,
    };

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

    private static ByorKeyService NewService(AppDbContext db,
        FakeLiteLlmAdmin admin, LlmProxyOptions? opt = null,
        IDistributedCache? cache = null) =>
        new(db, admin, Options.Create(opt ?? EnabledOptions),
            new EphemeralDataProtectionProvider(), cache);

    [Fact]
    public async Task Refresh_Returns_Plaintext_Once_And_Stores_Ciphertext()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = await NewDbAsync(conn);
        var admin = new FakeLiteLlmAdmin();
        var svc = NewService(db, admin);

        var (key, info) = await svc.RefreshAsync(
            "u1", "org-1", null, CancellationToken.None);

        key.ShouldBe("sk-litellm-newkey1");
        info.KeyPrefix.ShouldBe(key[..16]);
        info.BudgetUsd.ShouldBe(25m); // default do options
        var created = admin.Created.ShouldNotBeNull();
        created.UserId.ShouldBe("u1");
        created.OrgId.ShouldBe("org-1");
        created.Budget.ShouldBe(25m);

        var row = db.ByorKeys.Single();
        row.KeyCiphertext.ShouldNotBe(key); // nunca plaintext
        row.RevokedAt.ShouldBeNull();
        row.ModelsJson.ShouldBe(JsonSerializer.Serialize(admin.Models));
    }

    [Fact]
    public async Task Refresh_Rotates_Old_Key_Deleted_On_Proxy()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = await NewDbAsync(conn);
        var admin = new FakeLiteLlmAdmin();
        var svc = NewService(db, admin);

        var (first, _) = await svc.RefreshAsync(
            "u1", null, null, CancellationToken.None);
        var firstKeyId = db.ByorKeys.Single(k => k.RevokedAt == null).KeyId;

        var (second, _) = await svc.RefreshAsync(
            "u1", null, 99m, CancellationToken.None);

        second.ShouldNotBe(first);
        admin.Deleted.ShouldContain(firstKeyId);
        db.ByorKeys.Count(k => k.UserId == "u1").ShouldBe(2);
        db.ByorKeys.Count(k => k.RevokedAt == null).ShouldBe(1);
        db.ByorKeys.Single(k => k.RevokedAt == null).BudgetUsd.ShouldBe(99m);
    }

    [Fact]
    public async Task Revoke_Marks_Local_And_Deletes_Remote()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = await NewDbAsync(conn);
        var admin = new FakeLiteLlmAdmin();
        var svc = NewService(db, admin);

        (await svc.RevokeAsync("u1", CancellationToken.None)).ShouldBeFalse();

        await svc.RefreshAsync("u1", null, null, CancellationToken.None);
        var keyId = db.ByorKeys.Single().KeyId;
        (await svc.RevokeAsync("u1", CancellationToken.None)).ShouldBeTrue();

        admin.Deleted.ShouldContain(keyId);
        (await svc.GetActiveAsync("u1", CancellationToken.None)).ShouldBeNull();
        (await svc.ResolveCallContextAsync("u1", CancellationToken.None))
            .ShouldBeNull();
    }

    [Fact]
    public async Task ResolveCallContext_Decrypts_Virtual_Key()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = await NewDbAsync(conn);
        var svc = NewService(db, new FakeLiteLlmAdmin());

        var (key, info) = await svc.RefreshAsync(
            "u1", null, null, CancellationToken.None);
        var ctx = await svc.ResolveCallContextAsync("u1", CancellationToken.None);

        ctx.ShouldNotBeNull();
        ctx.BaseUrl.ShouldBe("http://litellm:4000");
        ctx.VirtualKey.ShouldBe(key);
        ctx.KeyId.ShouldBe(info.KeyId);
    }

    [Fact]
    public async Task PermittedModels_Caches_For_60s()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = await NewDbAsync(conn);
        var admin = new FakeLiteLlmAdmin();
        var cache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        var svc = NewService(db, admin, cache: cache);

        var first = await svc.PermittedModelsAsync(CancellationToken.None);
        var second = await svc.PermittedModelsAsync(CancellationToken.None);

        first.ShouldBe(["gpt-4o-mini", "claude-sonnet-4"]);
        second.ShouldBe(first);
        admin.ListCalls.ShouldBe(1); // segunda leitura saiu do cache
    }

    [Fact]
    public async Task Disabled_Mode_Fails_Closed()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = await NewDbAsync(conn);
        var svc = NewService(db, new FakeLiteLlmAdmin(),
            new LlmProxyOptions { Mode = "off" });

        svc.Enabled.ShouldBeFalse();
        await Should.ThrowAsync<LiteLlmUnavailableException>(() =>
            svc.RefreshAsync("u1", null, null, CancellationToken.None));
        (await svc.ResolveCallContextAsync("u1", CancellationToken.None))
            .ShouldBeNull();
    }

    [Fact]
    public async Task Proxy_Failure_Propagates_As_Unavailable()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = await NewDbAsync(conn);
        var svc = NewService(db, new FakeLiteLlmAdmin { Down = true });

        await Should.ThrowAsync<LiteLlmUnavailableException>(() =>
            svc.RefreshAsync("u1", null, null, CancellationToken.None));
        db.ByorKeys.ShouldBeEmpty(); // nada persistido em falha
    }

    /// <summary>LiteLLM admin fake — grava chamadas, serve /models fixo.</summary>
    private sealed class FakeLiteLlmAdmin : ILiteLlmAdminClient
    {
        public bool Down;
        public int CreatedSeq;
        public (string UserId, string? OrgId, decimal Budget)? Created;
        public List<string> Deleted { get; } = [];
        public int ListCalls;
        public IReadOnlyList<string> Models { get; } =
            ["gpt-4o-mini", "claude-sonnet-4"];

        public Task<LiteLlmVirtualKey> CreateVirtualKeyAsync(
            string userId, string? orgId, decimal budgetUsd,
            IReadOnlyList<string> models, CancellationToken ct)
        {
            if (Down) throw new LiteLlmUnavailableException("down");
            Created = (userId, orgId, budgetUsd);
            var seq = ++CreatedSeq;
            return Task.FromResult(new LiteLlmVirtualKey(
                $"sk-litellm-newkey{seq}", $"key-id-{seq}", $"bud-{seq}"));
        }

        public Task DeleteKeyAsync(string keyId, CancellationToken ct)
        {
            if (Down) throw new LiteLlmUnavailableException("down");
            Deleted.Add(keyId);
            return Task.CompletedTask;
        }

        public Task UpdateKeyBudgetAsync(string keyId, decimal budgetUsd,
            CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListModelsAsync(
            CancellationToken ct)
        {
            if (Down) throw new LiteLlmUnavailableException("down");
            ListCalls++;
            return Task.FromResult(Models);
        }
    }
}

/// <summary>
/// SPEC-50 RF-001: shape HTTP do admin client — bearer master key,
/// <c>/key/generate</c>, <c>/key/delete</c>, <c>/models</c>; qualquer
/// falha de transporte/HTTP → <see cref="LiteLlmUnavailableException"/>.
/// </summary>
public sealed class Spec50LiteLlmAdminClientTests
{
    private static LiteLlmAdminClient New(HttpMessageHandler handler,
        LlmProxyOptions? opt = null) =>
        new(new StubFactory(handler), Options.Create(opt ?? new LlmProxyOptions
        {
            BaseUrl = "http://litellm:4000/",
            MasterKey = "mk-secret",
            Mode = "hybrid",
        }), NullLogger<LiteLlmAdminClient>.Instance);

    [Fact]
    public async Task Generate_Sends_MasterKey_And_Parses_Key()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """
            {"key":"sk-litellm-abc123","key_id":"kid-9","budget_id":"b-1"}
            """);
        var client = New(handler);

        var key = await client.CreateVirtualKeyAsync(
            "u1", "org-7", 42m, ["gpt-4o-mini"], CancellationToken.None);

        key.Key.ShouldBe("sk-litellm-abc123");
        key.KeyId.ShouldBe("kid-9");
        key.BudgetId.ShouldBe("b-1");
        handler.LastPath.ShouldBe("/key/generate");
        handler.LastAuth.ShouldBe("Bearer mk-secret");
    }

    [Fact]
    public async Task Delete_Posts_Key_Id()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        var client = New(handler);

        await client.DeleteKeyAsync("kid-9", CancellationToken.None);

        handler.LastPath.ShouldBe("/key/delete");
        handler.LastBody.ShouldNotBeNull().ShouldContain("kid-9");
    }

    [Fact]
    public async Task Models_Parses_OpenAI_Data_Ids()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """
            {"data":[{"id":"gpt-4o-mini"},{"id":"claude-sonnet-4"}]}
            """);
        var client = New(handler);

        var models = await client.ListModelsAsync(CancellationToken.None);

        models.ShouldBe(["gpt-4o-mini", "claude-sonnet-4"]);
        handler.LastPath.ShouldBe("/models");
    }

    [Fact]
    public async Task Non2xx_Throws_Unavailable()
    {
        var client = New(new RecordingHandler(
            HttpStatusCode.InternalServerError, "boom"));
        await Should.ThrowAsync<LiteLlmUnavailableException>(() =>
            client.ListModelsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Unreachable_Throws_Unavailable()
    {
        var client = New(new RecordingHandler(new HttpRequestException("down")));
        await Should.ThrowAsync<LiteLlmUnavailableException>(() =>
            client.ListModelsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Missing_Config_Throws_Unavailable()
    {
        var client = New(new RecordingHandler(HttpStatusCode.OK, "{}"),
            new LlmProxyOptions { Mode = "off" });
        await Should.ThrowAsync<LiteLlmUnavailableException>(() =>
            client.ListModelsAsync(CancellationToken.None));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode? _status;
        private readonly string? _body;
        private readonly Exception? _error;
        public string? LastPath;
        public string? LastAuth;
        public string? LastBody;

        public RecordingHandler(HttpStatusCode status, string body)
        { _status = status; _body = body; }

        public RecordingHandler(Exception error) => _error = error;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // O request é disposed pelo cliente — capturo o que importa.
            LastPath = request.RequestUri?.AbsolutePath;
            LastAuth = request.Headers.Authorization?.ToString();
            LastBody = request.Content is null ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            if (_error is not null) throw _error;
            return new HttpResponseMessage(_status!.Value)
            {
                Content = new StringContent(_body ?? "", Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }
}