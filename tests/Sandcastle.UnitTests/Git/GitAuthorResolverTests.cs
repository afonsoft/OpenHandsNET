using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Sandbox;
using Sandcastle.Domain.Sandbox;
using Sandcastle.Infrastructure.Git;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.UnitTests.Agent;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.Git;

/// <summary>
/// SPEC-147 RF-002 — cadeia user→org→global de autoria +
/// trailer <c>Co-authored-by</c> com <c>Git:CoAuthorUser</c>.
/// </summary>
public sealed class GitAuthorResolverTests : IAsyncDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly AppDbContext _db;

    public GitAuthorResolverTests()
    {
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_conn).UseSnakeCaseNamingConvention().Options);
        _db.Database.EnsureCreated();
    }

    // ---------- núcleo estático (cadeia pura) ----------

    [Fact]
    public void Resolve_GlobalDefault_WhenNoSettings()
    {
        var id = GitAuthorResolver.Resolve(null, null, new GitOptions(),
            "Ana", "ana@x.io", userEmailVerified: true);

        id.AuthorName.ShouldBe("sandcastle-agent");
        id.AuthorEmail.ShouldBe("agent@sandcastle.local");
        id.CoAuthorTrailer.ShouldBe(
            "Co-authored-by: Ana <ana@x.io>");
    }

    [Fact]
    public void Resolve_OrgSettings_BeatGlobal()
    {
        var org = new Dictionary<string, string>
        {
            ["git_author_name"] = "acme-bot",
            ["git_author_email"] = "bot@acme.io",
        };
        var id = GitAuthorResolver.Resolve(null, org, new GitOptions(),
            "Ana", "ana@x.io", userEmailVerified: true);

        id.AuthorName.ShouldBe("acme-bot");
        id.AuthorEmail.ShouldBe("bot@acme.io");
    }

    [Fact]
    public void Resolve_UserPrefs_BeatOrgAndGlobal()
    {
        var user = new Dictionary<string, string>
        {
            ["git_author_name"] = "ana-dev",
            ["git_author_email"] = "ana@self.io",
        };
        var org = new Dictionary<string, string>
        {
            ["git_author_name"] = "acme-bot",
            ["git_author_email"] = "bot@acme.io",
        };
        var id = GitAuthorResolver.Resolve(user, org, new GitOptions(),
            "Ana", "ana@x.io", userEmailVerified: true);

        id.AuthorName.ShouldBe("ana-dev");
        id.AuthorEmail.ShouldBe("ana@self.io");
    }

    [Fact]
    public void Resolve_CoAuthor_DisabledByOrg()
    {
        var org = new Dictionary<string, string>
        {
            ["coauthor_enabled"] = "false",
        };
        var id = GitAuthorResolver.Resolve(null, org, new GitOptions(),
            "Ana", "ana@x.io", userEmailVerified: true);

        id.CoAuthorTrailer.ShouldBeNull();
    }

    [Fact]
    public void Resolve_CoAuthor_DisabledGlobally()
    {
        var id = GitAuthorResolver.Resolve(null, null,
            new GitOptions { CoAuthorUser = false },
            "Ana", "ana@x.io", userEmailVerified: true);

        id.CoAuthorTrailer.ShouldBeNull();
    }

    [Fact]
    public void Resolve_CoAuthor_UserOptOut_Wins()
    {
        var user = new Dictionary<string, string>
        {
            ["git_coauthor"] = "false",
        };
        var id = GitAuthorResolver.Resolve(user, null, new GitOptions(),
            "Ana", "ana@x.io", userEmailVerified: true);

        id.CoAuthorTrailer.ShouldBeNull();
    }

    [Fact]
    public void Resolve_CoAuthor_UserOptIn_BeatsOrgOptOut()
    {
        var user = new Dictionary<string, string>
        {
            ["git_coauthor"] = "true",
        };
        var org = new Dictionary<string, string>
        {
            ["coauthor_enabled"] = "false",
        };
        var id = GitAuthorResolver.Resolve(user, org, new GitOptions(),
            "Ana", "ana@x.io", userEmailVerified: true);

        id.CoAuthorTrailer.ShouldBe(
            "Co-authored-by: Ana <ana@x.io>");
    }

    [Fact]
    public void Resolve_CoAuthor_RequiresVerifiedEmail()
    {
        var id = GitAuthorResolver.Resolve(null, null, new GitOptions(),
            "Ana", "ana@x.io", userEmailVerified: false);

        id.CoAuthorTrailer.ShouldBeNull();
    }

    [Fact]
    public void Resolve_CoAuthor_FallsBackToEmail_WhenNoDisplayName()
    {
        var id = GitAuthorResolver.Resolve(null, null, new GitOptions(),
            null, "ana@x.io", userEmailVerified: true);

        id.CoAuthorTrailer.ShouldBe(
            "Co-authored-by: ana@x.io <ana@x.io>");
    }

    // ---------- nível CommitAsync (DB + comando gerado) ----------

    [Fact]
    public async Task CommitAsync_UsesResolvedAuthor_AndTrailer()
    {
        var (git, executor) = Service();
        var (convId, userId) = SeedConversation(
            prefs: """{"git_author_name":"ana-dev","git_author_email":"ana@self.io"}""",
            orgSettings: """{"git_author_name":"acme-bot"}""",
            verified: true);
        var sandbox = SandboxFor(convId);

        await git.CommitAsync(sandbox, "fix bug", null, CancellationToken.None);

        var cmd = executor.Commands.ShouldHaveSingleItem();
        cmd.ShouldContain("-c user.name='ana-dev'");
        cmd.ShouldContain("-c user.email='ana@self.io'");
        DecodeMessage(cmd).ShouldBe(
            "fix bug\n\nCo-authored-by: Ana Silva <ana@x.io>");
    }

    [Fact]
    public async Task CommitAsync_OrgAuthor_WhenUserHasNoPrefs()
    {
        var (git, executor) = Service();
        var convId = SeedConversation(
            prefs: null,
            orgSettings: """{"git_author_name":"acme-bot","git_author_email":"bot@acme.io","coauthor_enabled":"false"}""",
            verified: true).ConvId;
        var sandbox = SandboxFor(convId);

        await git.CommitAsync(sandbox, "msg", null, CancellationToken.None);

        var cmd = executor.Commands.ShouldHaveSingleItem();
        cmd.ShouldContain("-c user.name='acme-bot'");
        cmd.ShouldContain("-c user.email='bot@acme.io'");
        DecodeMessage(cmd).ShouldBe("msg"); // org desligou trailer
    }

    [Fact]
    public async Task CommitAsync_NoConversation_GlobalAuthorNoTrailer()
    {
        var (git, executor) = Service();
        var sandbox = SandboxFor(null);

        await git.CommitAsync(sandbox, "msg", null, CancellationToken.None);

        var cmd = executor.Commands.ShouldHaveSingleItem();
        cmd.ShouldContain("-c user.name='sandcastle-agent'");
        cmd.ShouldContain("-c user.email='agent@sandcastle.local'");
        DecodeMessage(cmd).ShouldBe("msg");
    }

    // ---------- helpers ----------

    private (GitService Git, FakeSandboxExecutor Exec) Service()
    {
        var exec = new FakeSandboxExecutor
        {
            ExecHandler = _ => new CommandResult(0, "abc123\n", "", false),
        };
        var opts = Options.Create(new GitOptions());
        return (new GitService(exec,
            new GitCredentialProvider(_db,
                new EphemeralDataProtectionProvider(),
                NullLogger<GitCredentialProvider>.Instance),
            opts, new GitAuthorResolver(_db, opts),
            NullLogger<GitService>.Instance), exec);
    }

    private static Domain.Sandbox.Sandbox SandboxFor(Guid? convId) => new(
        Guid.NewGuid(), Guid.Empty, convId, "fake",
        SandboxStatus.Running, null, DateTimeOffset.UtcNow, null, null);

    private (Guid ConvId, Guid UserId) SeedConversation(
        string? prefs, string? orgSettings, bool verified)
    {
        var orgId = Guid.NewGuid();
        _db.Organizations.Add(new OrganizationRecord
        {
            Id = orgId,
            Slug = "o",
            Name = "O",
            SettingsJson = orgSettings,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var userId = Guid.NewGuid();
        _db.Users.Add(new UserRecord
        {
            Id = userId,
            Email = "ana@x.io",
            DisplayName = "Ana Silva",
            EmailVerified = verified,
            PreferencesJson = prefs,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var convId = Guid.NewGuid();
        _db.Conversations.Add(new Infrastructure.Conversations
            .ConversationRecord
        {
            Id = convId,
            ExecutionStatus = "RUNNING",
            OrgId = orgId,
            CreatedByUserId = userId.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
        return (convId, userId);
    }

    /// <summary>Extrai e decodifica o <c>-m</c> base64 do comando.</summary>
    private static string DecodeMessage(string cmd)
    {
        const string marker = "echo '";
        var start = cmd.IndexOf(marker, StringComparison.Ordinal)
            + marker.Length;
        var end = cmd.IndexOf('\'', start);
        var b64 = cmd[start..end];
        return System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(b64));
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }
}