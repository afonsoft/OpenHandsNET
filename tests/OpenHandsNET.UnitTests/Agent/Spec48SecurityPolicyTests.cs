using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Application.Agent;
using OpenHandsNET.Infrastructure.Persistence;
using OpenHandsNET.Infrastructure.Security;

using Shouldly;

using Xunit;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>
/// SPEC-48 RF-001: resolução de presets/overrides e persistência +
/// cache do <see cref="SecurityPolicyService"/>.
/// </summary>
public sealed class Spec48SecurityPolicyTests
{
    // ---------- presets ----------

    [Fact]
    public void Balanced_Is_Default_And_Matches_Status_Quo()
    {
        var p = SecurityPolicyPresets.Resolve(null, null);
        p.Preset.ShouldBe("balanced");
        p.AskMcpTools.ShouldBeFalse();
        p.MinConfirmRisk.ShouldBe(SecurityRisk.High);
        p.ResumeStrict.ShouldBeNull();
        p.AnalyzerOverride.ShouldBeNull(); // herda global (pattern default)
    }

    [Fact]
    public void Strict_Asks_Mcp_Confirms_Medium_And_ResumeStrict()
    {
        var p = SecurityPolicyPresets.Resolve("strict", null);
        p.Preset.ShouldBe("strict");
        p.AskMcpTools.ShouldBeTrue();
        p.MinConfirmRisk.ShouldBe(SecurityRisk.Medium);
        p.ResumeStrict.ShouldBe(true);
        p.AnalyzerOverride.ShouldBe("llm"); // prefer-llm (fallback no runner)
    }

    [Fact]
    public void Explicit_Overrides_Beat_Preset_Defaults()
    {
        var p = SecurityPolicyPresets.Resolve("strict",
            new SecurityPolicyUpdate(null, "pattern", false, "high", false));
        p.AnalyzerOverride.ShouldBe("pattern");
        p.AskMcpTools.ShouldBeFalse();
        p.MinConfirmRisk.ShouldBe(SecurityRisk.High);
        p.ResumeStrict.ShouldBe(false);
    }

    // ---------- service ----------

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

    [Fact]
    public async Task Get_No_Row_Or_No_Org_Returns_Balanced()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = await NewDbAsync(conn);
        var svc = new SecurityPolicyService(db);

        (await svc.GetAsync(null, CancellationToken.None)).Preset
            .ShouldBe("balanced");
        (await svc.GetAsync(Guid.NewGuid(), CancellationToken.None)).Preset
            .ShouldBe("balanced");
    }

    [Fact]
    public async Task Set_Persists_And_Get_Resolves_Effective()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = await NewDbAsync(conn);
        var svc = new SecurityPolicyService(db);
        var org = Guid.NewGuid();

        var saved = await svc.SetAsync(org,
            new SecurityPolicyUpdate("strict", null, null, null, null),
            Guid.NewGuid(), CancellationToken.None);
        saved.Preset.ShouldBe("strict");
        saved.AskMcpTools.ShouldBeTrue();

        var fetched = await svc.GetAsync(org, CancellationToken.None);
        fetched.MinConfirmRisk.ShouldBe(SecurityRisk.Medium);
        db.SecurityPolicies.Count(r => r.OrgId == org).ShouldBe(1);
    }

    [Fact]
    public async Task Set_Null_Update_Resets_To_Balanced()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = await NewDbAsync(conn);
        var svc = new SecurityPolicyService(db);
        var org = Guid.NewGuid();

        await svc.SetAsync(org,
            new SecurityPolicyUpdate("strict", null, null, null, null),
            null, CancellationToken.None);
        var reset = await svc.SetAsync(org, null, null, CancellationToken.None);

        reset.Preset.ShouldBe("balanced");
        reset.AskMcpTools.ShouldBeFalse();
        reset.ResumeStrict.ShouldBeNull();
    }

    [Fact]
    public async Task Set_Invalid_Values_Throw()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = await NewDbAsync(conn);
        var svc = new SecurityPolicyService(db);

        await Should.ThrowAsync<ArgumentException>(() => svc.SetAsync(
            Guid.NewGuid(), new SecurityPolicyUpdate("nope", null, null, null, null),
            null, CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() => svc.SetAsync(
            Guid.NewGuid(), new SecurityPolicyUpdate(null, "bogus", null, null, null),
            null, CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() => svc.SetAsync(
            Guid.NewGuid(), new SecurityPolicyUpdate(null, null, null, "bogus", null),
            null, CancellationToken.None));
    }

    [Fact]
    public async Task Cache_Hit_Skips_Db_And_Set_Invalidates()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = await NewDbAsync(conn);
        var cache = new MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(
                new MemoryDistributedCacheOptions()));
        var svc = new SecurityPolicyService(db, cache);
        var org = Guid.NewGuid();

        await svc.SetAsync(org,
            new SecurityPolicyUpdate("strict", null, null, null, null),
            null, CancellationToken.None);
        (await svc.GetAsync(org, CancellationToken.None)).Preset
            .ShouldBe("strict");

        // Cache stale por design (TTL) — o que importa é invalidar no write:
        (await cache.GetStringAsync($"ohn:sec-policy:{org:N}"))
            .ShouldNotBeNull();

        await svc.SetAsync(org,
            new SecurityPolicyUpdate("permissive", null, null, null, null),
            null, CancellationToken.None);
        (await cache.GetStringAsync($"ohn:sec-policy:{org:N}"))
            .ShouldBeNull(); // invalidado
        (await svc.GetAsync(org, CancellationToken.None)).Preset
            .ShouldBe("permissive");
    }
}