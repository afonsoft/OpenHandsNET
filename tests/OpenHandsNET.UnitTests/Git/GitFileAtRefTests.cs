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
/// SPEC-21 RF-006: <c>GetFileAtRefAsync</c> — <c>git show ref:path</c> no
/// sandbox, path confinado ao repo, strip do prefixo /workspace/repo.
/// </summary>
public sealed class GitFileAtRefTests : IAsyncDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly FakeSandboxExecutor _executor = new();
    private readonly GitService _git;

    public GitFileAtRefTests()
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

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    private static Domain.Sandbox.Sandbox Sandbox() => new(
        Guid.NewGuid(), Guid.Empty, null, "fake", SandboxStatus.Running,
        null, DateTimeOffset.UtcNow, null, null);

    [Fact]
    public async Task Returns_Content_And_Strips_Workspace_Prefix()
    {
        _executor.ExecHandler = cmd => cmd.Contains("git -C") && cmd.Contains("show")
            ? new CommandResult(0, "file body\n", "", false)
            : new CommandResult(1, "", "", false);

        var content = await _git.GetFileAtRefAsync(
            Sandbox(), "/workspace/repo/src/Foo.cs", "HEAD",
            CancellationToken.None);

        content.ShouldBe("file body\n");
        _executor.Commands.ShouldContain(
            c => c.Contains("show 'HEAD:src/Foo.cs'"));
    }

    [Fact]
    public async Task Accepts_Repo_Relative_Path()
    {
        _executor.ExecHandler = cmd => new CommandResult(0, "x", "", false);
        await _git.GetFileAtRefAsync(
            Sandbox(), "src/Foo.cs", "HEAD", CancellationToken.None);
        _executor.Commands.ShouldContain(
            c => c.Contains("show 'HEAD:src/Foo.cs'"));
    }

    [Fact]
    public async Task Nonexistent_File_Returns_Null()
    {
        _executor.ExecHandler = _ =>
            new CommandResult(128, "", "fatal: path not found", false);
        (await _git.GetFileAtRefAsync(
            Sandbox(), "gone.cs", "HEAD", CancellationToken.None))
            .ShouldBeNull();
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("")]
    [InlineData("/workspace/repo/../../etc/passwd")]
    public async Task Traversal_Returns_Null_Without_Exec(string path)
    {
        (await _git.GetFileAtRefAsync(
            Sandbox(), path, "HEAD", CancellationToken.None)).ShouldBeNull();
        _executor.Commands.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ref_With_Quote_Is_Rejected()
    {
        (await _git.GetFileAtRefAsync(
            Sandbox(), "a.cs", "HEAD'--", CancellationToken.None))
            .ShouldBeNull();
        _executor.Commands.ShouldBeEmpty();
    }
}
