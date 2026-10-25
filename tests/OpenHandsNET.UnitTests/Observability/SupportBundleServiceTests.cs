using System.IO.Compression;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using OpenHandsNET.Infrastructure.Observability;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.UnitTests.Observability;

/// <summary>
/// SPEC-108: o ct agora propaga para os writes do zip (S8949) —
/// geração produz um bundle válido e cancelamento aborta.
/// </summary>
public sealed class SupportBundleServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDbContext _db;
    private readonly SupportBundleService _svc;

    public SupportBundleServiceTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(), $"ohn-bundle-{Guid.NewGuid():N}.db");
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .UseSnakeCaseNamingConvention().Options);
        _db.Database.EnsureCreated();

        var health = new StubHealthCheckService();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DeploymentMode"] = "oss",
            }).Build();
        _svc = new SupportBundleService(_db, health, config);
    }

    [Fact]
    public async Task GenerateAsync_Produces_Zip_With_Core_Entries()
    {
        var bytes = await _svc.GenerateAsync(CancellationToken.None);

        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.FullName).ToList();

        names.ShouldContain("readiness.json");
        names.ShouldContain("versions.json");
        names.ShouldContain("counts.json");
        names.ShouldContain("config.json");
    }

    [Fact]
    public async Task GenerateAsync_Propagates_Cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => _svc.GenerateAsync(cts.Token));
    }

    private sealed class StubHealthCheckService : HealthCheckService
    {
        public override Task<HealthReport> CheckHealthAsync(
            Func<HealthCheckRegistration, bool>? predicate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HealthReport(
                new Dictionary<string, HealthReportEntry>(),
                TimeSpan.Zero));

    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { /* best-effort */ }
    }
}