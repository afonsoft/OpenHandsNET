using Microsoft.EntityFrameworkCore;

using OpenHandsNET.Application.Identity;
using OpenHandsNET.Domain.Identity;
using OpenHandsNET.Infrastructure.Identity;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.UnitTests.Identity;

/// <summary>
/// OrgService.CreateAsync — resolução de slug (S8969 cleanup: `slug!`
/// removido; o else-branch já é não-nulo por flow analysis).
/// </summary>
public sealed class OrgServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDbContext _db;

    public OrgServiceTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(), $"ohn-org-{Guid.NewGuid():N}.db");
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .UseSnakeCaseNamingConvention().Options);
        _db.Database.Migrate();
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private OrgService Sut() => new(_db, new FakeAudit());

    private static AuthUser User() => new(
        Guid.NewGuid(), "u@t.com", null, false, null, AuthType.Bearer);

    [Fact]
    public async Task Create_Slug_Null_Slugifica_Nome()
    {
        var org = await Sut().CreateAsync(
            User(), "Minha Org", null, CancellationToken.None);

        org.Slug.ShouldBe("minha-org");
        org.Name.ShouldBe("Minha Org");
    }

    [Fact]
    public async Task Create_Slug_Explicito_Usa_Literal()
    {
        var org = await Sut().CreateAsync(
            User(), "Minha Org", "custom-Slug", CancellationToken.None);

        org.Slug.ShouldBe("custom-Slug");
    }

    private sealed class FakeAudit : IAuditService
    {
        public Task RecordAsync(string action, string? actorUserId = null,
            Guid? orgId = null, string? targetType = null,
            string? targetId = null, string? ip = null,
            string? userAgent = null, object? metadata = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<(IReadOnlyList<AuditEntry> Items, int Total)> QueryAsync(
            string? u, string? a, DateTimeOffset? f, DateTimeOffset? t,
            int p, int ps, CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<AuditEntry>, int)>(([], 0));
    }
}