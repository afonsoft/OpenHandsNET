using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Application.BrowserProfiles;
using OpenHandsNET.Infrastructure.BrowserProfiles;
using OpenHandsNET.Infrastructure.Persistence;

using Shouldly;

using Xunit;

namespace OpenHandsNET.UnitTests.BrowserProfiles;

/// <summary>SPEC-64: profiles criptografados + domínio + TOTP.</summary>
public sealed class Spec64BrowserProfilesTests : IDisposable
{
    private readonly SqliteConnection _conn = new(
        "Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly BrowserProfileService _svc;

    public Spec64BrowserProfilesTests()
    {
        _conn.Open();
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_conn).UseSnakeCaseNamingConvention().Options);
        _db.Database.EnsureCreated();
        var dp = new ServiceCollection().AddDataProtection()
            .Services.BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>();
        _svc = new BrowserProfileService(_db, dp);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task Save_ThenResolve_RoundTripsState()
    {
        var info = await _svc.SaveAsync(
            "u1", "app.Example.COM", "ex",
            """{"cookies":[{"name":"s","domain":".example.com"}]}""",
            null, default);
        info.Domain.ShouldBe("example.com");
        // ciphertext-only: coluna não contém o state em claro.
        var row = await _db.BrowserProfiles.SingleAsync();
        row.Ciphertext.ShouldNotContain("example.com");
        var state = await _svc.ResolveStateAsync(
            info.Id, "u1", false, default);
        state.ShouldContain("example.com");
    }

    [Fact]
    public async Task Save_SameOwnerDomain_Upserts()
    {
        var a = await _svc.SaveAsync(
            "u1", "example.com", "n1", "{}", null, default);
        var b = await _svc.SaveAsync(
            "u1", "EXAMPLE.com", "n2", "{}", null, default);
        b.Id.ShouldBe(a.Id);
        (await _db.BrowserProfiles.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task ExpiredProfile_ResolveThrows()
    {
        var p = await _svc.SaveAsync(
            "u1", "example.com", "x", "{}",
            DateTimeOffset.UtcNow.AddHours(-1), default);
        await Should.ThrowAsync<BrowserProfileExpiredException>(
            () => _svc.ResolveStateAsync(p.Id, "u1", false, default));
    }

    [Fact]
    public async Task OtherOwner_CannotResolveOrDelete()
    {
        var p = await _svc.SaveAsync(
            "u1", "example.com", "x", "{}", null, default);
        await Should.ThrowAsync<KeyNotFoundException>(
            () => _svc.ResolveStateAsync(p.Id, "u2", false, default));
        (await _svc.DeleteAsync(p.Id, "u2", false, default))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task Admin_CanDeleteAny()
    {
        var p = await _svc.SaveAsync(
            "u1", "example.com", "x", "{}", null, default);
        (await _svc.DeleteAsync(p.Id, "admin", true, default))
            .ShouldBeTrue();
    }

    [Fact]
    public void NormalizeDomain_KeepsEldPlus1()
    {
        IBrowserProfileService.NormalizeDomain("app.foo.example.com")
            .ShouldBe("example.com");
        IBrowserProfileService.NormalizeDomain("example.com")
            .ShouldBe("example.com");
        IBrowserProfileService.NormalizeDomain("localhost")
            .ShouldBe("localhost");
    }

    [Fact]
    public void Totp_Rfc6238_Vector()
    {
        // RFC 6238 test key (SHA-1, 8 dígitos, T=59 → 94287082).
        var key = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
        TotpGenerator.Generate(
                key, DateTimeOffset.FromUnixTimeSeconds(59),
                stepSeconds: 30, digits: 8)
            .ShouldBe("94287082");
        TotpGenerator.Generate(
                key, DateTimeOffset.FromUnixTimeSeconds(1111111109),
                stepSeconds: 30, digits: 8)
            .ShouldBe("07081804");
    }
}