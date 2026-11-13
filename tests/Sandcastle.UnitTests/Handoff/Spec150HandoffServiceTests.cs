using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Conversations;
using Sandcastle.Application.Events;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Sandbox;
using Sandcastle.Domain.Conversations;
using Sandcastle.Domain.Events;
using Sandcastle.Domain.Sandbox;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Git;
using Sandcastle.Infrastructure.Handoff;
using Sandcastle.Infrastructure.Persistence;

using Shouldly;

namespace Sandcastle.UnitTests.Handoff;

/// <summary>
/// SPEC-150 — HandoffDiffUtil (numstat/binário/blocklist/new-file) +
/// HandoffApplier (--3way → --reject com hunks reportados) +
/// HandoffService (validação create, package TTL/snapshot/authz).
/// </summary>
public sealed class Spec150HandoffServiceTests
{
    private const string Patch =
        "diff --git a/a.txt b/a.txt\n"
        + "index 1111111..2222222 100644\n"
        + "--- a/a.txt\n"
        + "+++ b/a.txt\n"
        + "@@ -1,2 +1,3 @@\n"
        + " keep\n"
        + "-old\n"
        + "+new\n"
        + "+more\n";

    // -------------------------------------------------------- diff util

    [Fact]
    public void Numstat_Counts_Files_And_Lines()
    {
        var (files, adds, dels) = HandoffDiffUtil.Numstat(Patch);
        files.ShouldBe(1);
        adds.ShouldBe(2);
        dels.ShouldBe(1);
    }

    [Fact]
    public void BinaryFiles_Detected_In_Patch()
    {
        var bin = "diff --git a/i.png b/i.png\n"
            + "index 111..222 100644\n"
            + "Binary files a/i.png and b/i.png differ\n";
        HandoffDiffUtil.BinaryFilesOf(Patch + bin)
            .ShouldBe(["i.png"]);
    }

    [Theory]
    [InlineData(".env", true)]
    [InlineData("config/.env.local", true)]
    [InlineData("certs/tls.pem", true)]
    [InlineData(".ssh/id_rsa", true)]
    [InlineData("src/Program.cs", false)]
    [InlineData("env/readme.md", false)]
    public void Secret_Blocklist_Matches(string path, bool expected) =>
        HandoffDiffUtil.IsSecretPath(path).ShouldBe(expected);

    [Fact]
    public void NewFilePatch_Synthesizes_Valid_Diff()
    {
        var patch = HandoffDiffUtil.NewFilePatch("n.txt", "hi\nthere\n");
        patch.ShouldContain("new file mode");
        patch.ShouldContain("@@ -0,0 +1,2 @@");
        patch.ShouldContain("+hi");
    }

    [Fact]
    public void LooksLikeUnifiedDiff_Guards_Garbage()
    {
        HandoffDiffUtil.LooksLikeUnifiedDiff(Patch).ShouldBeTrue();
        HandoffDiffUtil.LooksLikeUnifiedDiff("hello").ShouldBeFalse();
        HandoffDiffUtil.LooksLikeUnifiedDiff("").ShouldBeFalse();
    }

    // ---------------------------------------------------------- applier

    private static Sandbox FakeSandbox() => new(
        Guid.NewGuid(), Guid.NewGuid(), null, "c1",
        SandboxStatus.Running, null, DateTimeOffset.UtcNow,
        null, null);

    private class FakeExecutor : ISandboxExecutor
    {
        public List<string> Commands { get; } = [];
        public Dictionary<string, string> Files { get; } = new();
        public bool ThreeWayFails { get; set; }
        public bool RejectFails { get; set; } = true;
        public string? RejFile { get; set; }
        public string? RejContent { get; set; }

        public virtual Task<CommandResult> ExecAsync(
            Sandbox sandbox, string command, TimeSpan timeout,
            CancellationToken ct)
        {
            Commands.Add(command);
            if (command.Contains("rev-parse --is-inside-work-tree"))
            {
                return Task.FromResult(new CommandResult(
                    0, "true\n", "", false));
            }

            if (command.Contains("apply --3way"))
            {
                return Task.FromResult(new CommandResult(
                    ThreeWayFails ? 1 : 0, "",
                    ThreeWayFails ? "conflict" : "", false));
            }

            if (command.Contains("apply --reject"))
            {
                if (RejFile is { } f)
                {
                    Files[$"/workspace/repo/{f}"] = RejContent ?? "hunk";
                }

                return Task.FromResult(new CommandResult(
                    RejectFails ? 1 : 0, "", "", false));
            }

            if (command.Contains("find") && command.Contains("*.rej"))
            {
                return Task.FromResult(new CommandResult(
                    0, RejFile is { } f
                        ? $"/workspace/repo/{f}\n" : "", "", false));
            }

            return Task.FromResult(new CommandResult(0, "", "", false));
        }

        public Task<string> ReadFileAsync(
            Sandbox sandbox, string path, CancellationToken ct) =>
            Task.FromResult(Files.TryGetValue(path, out var c)
                ? c : throw new FileNotFoundException(path));

        public Task WriteFileAsync(
            Sandbox sandbox, string path, string content,
            CancellationToken ct)
        {
            Files[path] = content;
            return Task.CompletedTask;
        }

        public Task<IBashSession> OpenBashSessionAsync(
            Sandbox sandbox, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<FileEntry>> ListFilesAsync(
            Sandbox sandbox, string path, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FileEntry>>([]);

        public Task<GitDiff> GetGitDiffAsync(
            Sandbox sandbox, CancellationToken ct) =>
            Task.FromResult(new GitDiff([], "", 0, 0));
    }

    private static HandoffApplier NewApplier(FakeExecutor exec) => new(
        exec,
        Options.Create(new GitOptions()),
        Microsoft.Extensions.Logging.Abstractions
            .NullLogger<HandoffApplier>.Instance);

    private static HandoffMeta Meta(string? patch = Patch) => new()
    {
        Origin = "local",
        BaseSha = "abc1234",
        DiffPatch = patch,
    };

    [Fact]
    public async Task Applier_ThreeWay_Success_Marks_Applied()
    {
        var exec = new FakeExecutor();
        var stats = await NewApplier(exec).ApplyAsync(
            FakeSandbox(), Meta(), "work-x", default);
        stats.Applied.ShouldBeTrue();
        stats.Files.ShouldBe(1);
        stats.RejectedFiles.ShouldBeEmpty();
        exec.Commands.ShouldContain(c => c.Contains("apply --3way"));
    }

    [Fact]
    public async Task Applier_Conflict_Falls_Back_To_Reject_With_Hunks()
    {
        var exec = new FakeExecutor
        {
            ThreeWayFails = true,
            RejFile = "a.txt.rej",
            RejContent = "@@ -1,2 +1,3 @@\n keep\n-old\n",
        };
        var stats = await NewApplier(exec).ApplyAsync(
            FakeSandbox(), Meta(), "work-x", default);
        stats.Applied.ShouldBeFalse();
        stats.RejectedFiles.ShouldBe(["a.txt.rej"]);
        stats.RejectedHunks.Single().Content.ShouldContain("-old");
        exec.Commands.ShouldContain(c => c.Contains("apply --reject"));
    }

    [Fact]
    public async Task Applier_Empty_Patch_Is_Error()
    {
        var stats = await NewApplier(new FakeExecutor()).ApplyAsync(
            FakeSandbox(), Meta(null), "work-x", default);
        stats.Applied.ShouldBeFalse();
        stats.Error.ShouldBe("empty_patch");
    }

    // ---------------------------------------------------------- service

    private sealed class StubConversations : IAppConversationService
    {
        public CreateConversationRequest? LastRequest { get; private set; }

        public Task<AppConversationStartTask> CreateAsync(
            CreateConversationRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new AppConversationStartTask(
                Guid.NewGuid(),
                Domain.Conversations.StartTaskStatus.Working,
                Guid.NewGuid(), null, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

        public Task<AppConversationStartTask> CreateForUserAsync(
            Guid userId, Guid? orgId,
            CreateConversationRequest request,
            CancellationToken ct) => CreateAsync(request, ct);

        #region unused
        public Task<ConversationPage> SearchAsync(
            ConversationQuery q, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<long> CountAsync(
            ConversationQuery q, CancellationToken ct) =>
            Task.FromResult(0L);
        public Task<IReadOnlyList<AppConversation>> GetBatchAsync(
            IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AppConversation>>([]);
        public Task<AppConversation?> GetAsync(
            Guid id, CancellationToken ct) =>
            Task.FromResult<AppConversation?>(null);
        public Task<AppConversation?> UpdateAsync(
            Guid id, string? title, IReadOnlyList<string>? tags,
            bool? pinned, CancellationToken ct) =>
            Task.FromResult<AppConversation?>(null);
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(true);
        public Task<SendMessageError?> ValidateSendMessageAsync(
            Guid id, CancellationToken ct) =>
            Task.FromResult<SendMessageError?>(null);
        public Task<SwitchResult> SwitchProfileAsync(
            Guid id, string? profile, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<SwitchResult> SwitchModelAsync(
            Guid id, string model, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<AppConversation?> SetPermissionsAsync(
            Guid id,
            IReadOnlyList<Application.Agent.AgentPermissionRule>? rules,
            CancellationToken ct) =>
            Task.FromResult<AppConversation?>(null);
        public Task<AppConversation?> SetModeAsync(
            Guid id, string mode, CancellationToken ct) =>
            Task.FromResult<AppConversation?>(null);
        public Task<IReadOnlyList<AttentionItem>> ListAttentionAsync(
            int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AttentionItem>>([]);
        public Task<AttentionDismissResult> DismissAttentionAsync(
            Guid id, CancellationToken ct) =>
            Task.FromResult(AttentionDismissResult.Dismissed);
        #endregion
    }

    private sealed class StubSandboxService(
        Dictionary<Guid, Sandbox> sandboxes) : ISandboxService
    {
        public Task<Sandbox?> GetAsync(
            Guid sandboxId, CancellationToken ct) =>
            Task.FromResult(
                sandboxes.TryGetValue(sandboxId, out var s) ? s : null);

        public Task<Sandbox> StartAsync(
            Guid specId, Guid? convId, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<Sandbox> PauseAsync(Guid id, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<Sandbox> StopAsync(Guid id, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<Sandbox> ResumeAsync(Guid id, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task DeleteAsync(Guid id, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<Sandbox>> SearchAsync(
            SandboxStatus? status, Guid? convId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Sandbox>>([]);
        public Task<SandboxSpec> UpsertSpecAsync(
            SandboxSpec s, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<SandboxSpec?> GetSpecAsync(
            Guid id, CancellationToken ct) =>
            Task.FromResult<SandboxSpec?>(null);
        public Task SweepOrphansAsync(CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class StubEvents : IEventService
    {
        public IReadOnlyList<EventBase> Items { get; set; } = [];

        public ValueTask<EventBase> AppendAsync(
            Guid conversationId, EventBase e, CancellationToken ct) =>
            new(e);

        public Task<EventPage> SearchAsync(
            Guid conversationId, EventQuery q, CancellationToken ct) =>
            Task.FromResult(new EventPage(Items, null));

        public Task<long> CountAsync(
            Guid conversationId, EventQuery q, CancellationToken ct) =>
            Task.FromResult(0L);

        public Task<IReadOnlyList<EventBase>> GetBatchAsync(
            Guid conversationId, IReadOnlyCollection<string> ids,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<EventBase>>([]);

        public async IAsyncEnumerable<EventBase> SubscribeAsync(
            Guid conversationId,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class StubUser(AuthUser? user) : ICurrentUserAccessor
    {
        public AuthUser? User { get; } = user;
        public Guid RequireUserId() =>
            User?.UserId ?? throw new InvalidOperationException();
    }

    private static async Task<(SqliteConnection, AppDbContext)> NewDbAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(conn).UseSnakeCaseNamingConvention().Options);
        await db.Database.EnsureCreatedAsync();
        return (conn, db);
    }

    private static HandoffService NewService(
        AppDbContext db, IAppConversationService? convs = null,
        ISandboxService? sandboxes = null, ISandboxExecutor? exec = null,
        IEventService? events = null, ICurrentUserAccessor? user = null,
        int maxKb = 512) => new(
        db, convs ?? new StubConversations(),
        sandboxes ?? new StubSandboxService([]),
        exec ?? new FakeExecutor(),
        events ?? new StubEvents(),
        Options.Create(new HandoffOptions { MaxDiffKb = maxKb }),
        Options.Create(new GitOptions()), user);

    private static HandoffRequest Req(string? repo = "o/r",
        string? sha = "abc1234", string? patch = Patch,
        string? message = "continue") => new()
        {
            Repository = repo ?? "",
            BaseSha = sha ?? "",
            DiffPatch = patch ?? "",
            Message = message,
        };

    [Fact]
    public async Task Create_Validates_Required_Fields()
    {
        var (conn, db) = await NewDbAsync();
        await using var _ = conn;
        var svc = NewService(db);

        (await svc.CreateAsync(Req(repo: null), default)).Error
            .ShouldBe(HandoffCreateError.RepositoryRequired);
        (await svc.CreateAsync(Req(sha: "not-sha!"), default)).Error
            .ShouldBe(HandoffCreateError.InvalidBaseSha);
        (await svc.CreateAsync(Req(patch: "garbage"), default)).Error
            .ShouldBe(HandoffCreateError.InvalidPatch);
    }

    [Fact]
    public async Task Create_Rejects_Oversize_Patch()
    {
        var (conn, db) = await NewDbAsync();
        await using var _ = conn;
        var svc = NewService(db, maxKb: 1);
        var big = Patch + new string('x', 2048);
        (await svc.CreateAsync(Req(patch: big), default)).Error
            .ShouldBe(HandoffCreateError.PatchTooLarge);
    }

    [Fact]
    public async Task Create_Persists_Sanitized_Meta_And_Envelope()
    {
        var (conn, db) = await NewDbAsync();
        await using var _ = conn;
        var convs = new StubConversations();
        var svc = NewService(db, convs);
        var patch = Patch + "+token = '{{secret:API_KEY}}'\n";
        var r = await svc.CreateAsync(Req(patch: patch), default);

        r.Task.ShouldNotBeNull();
        var meta = HandoffService.Deserialize<HandoffMeta>(
            convs.LastRequest!.HandoffMetaJson)!;
        meta.Origin.ShouldBe("local");
        meta.BaseSha.ShouldBe("abc1234");
        meta.DiffPatch!.ShouldNotContain("{{secret:API_KEY}}");
        meta.DiffPatch!.ShouldContain("***"); // SecretRedactor rodou
        var prompt = (string)convs.LastRequest!
            .InitialMessage!.Content[0];
        prompt.ShouldContain("untrusted", Case.Insensitive);
        prompt.ShouldContain("continue");
    }

    // ------------------------------------------------ package auth/cache

    private async Task<(SqliteConnection, AppDbContext, ConversationRecord)>
        SeedConvAsync(
            Guid? owner = null, Guid? sandboxId = null,
            string? snapshot = null)
    {
        var (conn, db) = await NewDbAsync();
        var conv = new ConversationRecord
        {
            Id = Guid.NewGuid(),
            CreatedByUserId = owner?.ToString(),
            SandboxId = sandboxId,
            SelectedRepository = "o/r",
            ExecutionStatus = "FINISHED",
            HandoffPackageJson = snapshot,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();
        return (conn, db, conv);
    }

    private static AuthUser User(Guid? id = null) => new(
        id ?? Guid.NewGuid(), "u@x", "u", false, null,
        AuthType.Cookie);

    [Fact]
    public async Task Package_NotFound_And_Forbidden()
    {
        var (conn, db, conv) = await SeedConvAsync(
            owner: Guid.NewGuid());
        await using var _ = conn;

        (await NewService(db, user: new StubUser(User()))
            .GetPackageAsync(Guid.NewGuid(), false, default)).Error
            .ShouldBe(HandoffPackageError.NotFound);

        // dono diferente → 403
        (await NewService(db, user: new StubUser(User(Guid.NewGuid())))
            .GetPackageAsync(conv.Id, false, default)).Error
            .ShouldBe(HandoffPackageError.Forbidden);
    }

    [Fact]
    public async Task Package_DeadSandbox_Serves_Snapshot_Or_409()
    {
        var owner = Guid.NewGuid();
        var snapshot = HandoffService.Serialize(new HandoffPackage
        {
            Repo = "o/r",
            DiffPatch = Patch,
            HeadSha = "h1",
            GeneratedAt = DateTimeOffset.UtcNow.AddHours(-2),
        });
        var (conn, db, conv) = await SeedConvAsync(
            owner: owner, snapshot: snapshot);
        await using var _ = conn;
        var svc = NewService(db, user: new StubUser(User(owner)));

        var dead = await svc.GetPackageAsync(conv.Id, false, default);
        dead.Package.ShouldNotBeNull();
        dead.Package!.HeadSha.ShouldBe("h1");
        dead.Cached.ShouldBeTrue();

        // sem snapshot → NoLiveSandbox (409)
        var (conn2, db2, conv2) = await SeedConvAsync(owner: owner);
        await using var _2 = conn2;
        (await NewService(db2, user: new StubUser(User(owner)))
            .GetPackageAsync(conv2.Id, false, default)).Error
            .ShouldBe(HandoffPackageError.NoLiveSandbox);
    }

    private sealed class PackageExecutor : FakeExecutor
    {
        public string Head { get; set; } = "head1";
        public string Diff { get; set; } = Patch;

        public override Task<CommandResult> ExecAsync(
            Sandbox s, string cmd, TimeSpan t, CancellationToken ct)
        {
            if (cmd.Contains("rev-parse HEAD"))
            {
                return Task.FromResult(
                    new CommandResult(0, Head + "\n", "", false));
            }

            if (cmd.Contains("rev-parse --abbrev-ref"))
            {
                return Task.FromResult(
                    new CommandResult(0, "work-x\n", "", false));
            }

            if (cmd.Contains("git -C") && cmd.Contains(" diff "))
            {
                return Task.FromResult(
                    new CommandResult(0, Diff, "", false));
            }

            if (cmd.Contains("ls-files"))
            {
                return Task.FromResult(
                    new CommandResult(0, "", "", false));
            }

            return Task.FromResult(new CommandResult(0, "", "", false));
        }
    }

    [Fact]
    public async Task Package_LiveSandbox_Generates_And_Caches_Ttl()
    {
        var owner = Guid.NewGuid();
        var sbId = Guid.NewGuid();
        var (conn, db, conv) = await SeedConvAsync(
            owner: owner, sandboxId: sbId);
        await using var _ = conn;
        var exec = new PackageExecutor();
        var sb = FakeSandbox() with { Id = sbId };
        var svc = NewService(db,
            sandboxes: new StubSandboxService(
                new Dictionary<Guid, Sandbox> { [sbId] = sb }),
            exec: exec, user: new StubUser(User(owner)));

        var first = await svc.GetPackageAsync(conv.Id, false, default);
        first.Package.ShouldNotBeNull();
        first.Package!.HeadSha.ShouldBe("head1");
        first.Package.DiffPatch.ShouldBe(Patch);
        first.Cached.ShouldBeFalse();

        // snapshot persistido
        var cached = HandoffService.Deserialize<HandoffPackage>(
            (await db.Conversations.FindAsync(conv.Id))!
            .HandoffPackageJson);
        cached.ShouldNotBeNull();

        // segunda chamada dentro do TTL → cache
        var second = await svc.GetPackageAsync(conv.Id, false, default);
        second.Cached.ShouldBeTrue();

        // regenerate=1 ignora o TTL
        var regen = await svc.GetPackageAsync(conv.Id, true, default);
        regen.Cached.ShouldBeFalse();
    }

    [Fact]
    public async Task Package_Oversize_Sandbox_Diff_Is_422()
    {
        var owner = Guid.NewGuid();
        var sbId = Guid.NewGuid();
        var (conn, db, conv) = await SeedConvAsync(
            owner: owner, sandboxId: sbId);
        await using var _ = conn;
        var exec = new PackageExecutor
        { Diff = Patch + new string('x', 4096) };
        var sb = FakeSandbox() with { Id = sbId };
        var svc = NewService(db,
            sandboxes: new StubSandboxService(
                new Dictionary<Guid, Sandbox> { [sbId] = sb }),
            exec: exec, user: new StubUser(User(owner)), maxKb: 1);

        (await svc.GetPackageAsync(conv.Id, false, default)).Error
            .ShouldBe(HandoffPackageError.PackageTooLarge);
    }
}