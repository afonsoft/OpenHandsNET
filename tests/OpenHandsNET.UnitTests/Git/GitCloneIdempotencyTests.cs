using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Sandbox;
using OpenHandsNET.Infrastructure.Git;
using OpenHandsNET.Infrastructure.Persistence;
using OpenHandsNET.UnitTests.Agent;

namespace OpenHandsNET.UnitTests.Git;

/// <summary>
/// SPEC-18 RF-005: container-mode clone must be idempotent — recreating a
/// sandbox (switch_profile) reuses the host-mounted workspace where the repo
/// already exists, so <c>git clone</c> must be skipped and only the work
/// branch ensured.
/// </summary>
public sealed class GitCloneIdempotencyTests : IAsyncDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly FakeSandboxExecutor _executor = new();
    private readonly GitService _git;

    public GitCloneIdempotencyTests()
    {
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_conn).UseSnakeCaseNamingConvention().Options);
        _db.Database.EnsureCreated();
        _git = new GitService(_executor,
            new GitCredentialProvider(_db,
                new EphemeralDataProtectionProvider(),
                NullLogger<GitCredentialProvider>.Instance),
            Options.Create(new GitOptions()),
            NullLogger<GitService>.Instance);
    }

    private static Domain.Sandbox.Sandbox Sandbox() => new(
        Guid.NewGuid(), Guid.Empty, null, "fake", SandboxStatus.Running,
        null, DateTimeOffset.UtcNow, null, null);

    [Fact]
    public async Task Clone_Skips_Clone_When_Repo_Exists()
    {
        _executor.ExecHandler = cmd => cmd.Contains("rev-parse")
            ? new CommandResult(0, "true\n", "", false)
            : new CommandResult(0, "", "", false);

        await _git.CloneAsync(Sandbox(), "owner/repo", "ohn-work", "u1",
            CancellationToken.None);

        _executor.Commands.ShouldNotContain(c => c.Contains("git clone"));
        _executor.Commands.ShouldContain(c => c.Contains("checkout"));
    }

    [Fact]
    public async Task Clone_Runs_Clone_When_Repo_Missing()
    {
        _executor.ExecHandler = cmd => cmd.Contains("rev-parse")
            ? new CommandResult(1, "", "not a repo", false)
            : new CommandResult(0, "", "", false);

        await _git.CloneAsync(Sandbox(), "owner/repo", "ohn-work", "u1",
            CancellationToken.None);

        _executor.Commands.ShouldContain(c => c.Contains("git clone"));
        _executor.Commands.ShouldContain(c => c.Contains("checkout -b"));
    }

    [Fact]
    public async Task Clone_Throws_When_Checkout_Fails_On_Existing_Repo()
    {
        _executor.ExecHandler = cmd => cmd.Contains("rev-parse")
            ? new CommandResult(0, "true\n", "", false)
            : new CommandResult(1, "", "checkout failed", false);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            _git.CloneAsync(Sandbox(), "owner/repo", "ohn-work", "u1",
                CancellationToken.None));
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }
}
