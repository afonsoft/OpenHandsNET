using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Sandcastle.Application.Conversations;
using Sandcastle.Application.Events;
using Sandcastle.Application.Git;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Sandbox;
using Sandcastle.Application.Scm;
using Sandcastle.Domain.Conversations;
using Sandcastle.Domain.Events;
using Sandcastle.Domain.Sandbox;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Scm;

using Shouldly;

using Xunit;

using SandboxModel = Sandcastle.Domain.Sandbox.Sandbox;

namespace Sandcastle.UnitTests;

/// <summary>
/// SPEC-146 — multi-repo conversations: contract normalization (max 4,
/// primary-first, dedup), workspace layout, parallel clone orchestration
/// (primary required / secondary tolerated), persisted JSON aggregates and
/// per-repo PR creation.
/// </summary>
public sealed class Spec146MultiRepoTests : IAsyncDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly Guid _orgId = Guid.NewGuid();

    public Spec146MultiRepoTests()
    {
        _conn.Open();
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_conn).UseSnakeCaseNamingConvention().Options);
        _db.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    // ---- RF-001: RepositorySelection.Normalize -------------------------

    [Fact]
    public void Normalize_legacy_selected_repository_only()
    {
        var repos = RepositorySelection.Normalize(
            "owner/repo", null, MultiRepoOptions.HardMaxRepos);
        repos.ShouldBe(["owner/repo"]);
    }

    [Fact]
    public void Normalize_both_fields_matching_primary()
    {
        var repos = RepositorySelection.Normalize(
            "owner/a", ["owner/a", "owner/b"],
            MultiRepoOptions.HardMaxRepos);
        repos.ShouldBe(["owner/a", "owner/b"]);
    }

    [Fact]
    public void Normalize_selected_repository_must_be_first()
    {
        var ex = Should.Throw<RepositorySelectionException>(() =>
            RepositorySelection.Normalize(
                "owner/b", ["owner/a", "owner/b"],
                MultiRepoOptions.HardMaxRepos));
        ex.Message.ShouldContain("first entry");
    }

    [Fact]
    public void Normalize_rejects_more_than_hard_max()
    {
        var five = Enumerable.Range(1, 5).Select(i => $"o/r{i}").ToList();
        Should.Throw<RepositorySelectionException>(() =>
            RepositorySelection.Normalize(null, five,
                MultiRepoOptions.HardMaxRepos));
    }

    [Fact]
    public void Normalize_accepts_four_repos()
    {
        var four = Enumerable.Range(1, 4).Select(i => $"o/r{i}").ToList();
        RepositorySelection.Normalize(null, four,
            MultiRepoOptions.HardMaxRepos).Count.ShouldBe(4);
    }

    [Fact]
    public void Normalize_configured_max_cannot_exceed_hard_cap()
    {
        var opts = new MultiRepoOptions { MaxRepos = 99 };
        opts.EffectiveMaxRepos.ShouldBe(MultiRepoOptions.HardMaxRepos);
        var five = Enumerable.Range(1, 5).Select(i => $"o/r{i}").ToList();
        Should.Throw<RepositorySelectionException>(() =>
            RepositorySelection.Normalize(null, five, opts.EffectiveMaxRepos));
    }

    [Fact]
    public void Normalize_config_can_lower_max()
    {
        var opts = new MultiRepoOptions { MaxRepos = 2 };
        opts.EffectiveMaxRepos.ShouldBe(2);
        Should.Throw<RepositorySelectionException>(() =>
            RepositorySelection.Normalize(null, ["a/1", "a/2", "a/3"],
                opts.EffectiveMaxRepos));
    }

    [Fact]
    public void Normalize_trims_and_rejects_empty_entries()
    {
        RepositorySelection.Normalize(null, [" owner/a "],
            MultiRepoOptions.HardMaxRepos).ShouldBe(["owner/a"]);
        Should.Throw<RepositorySelectionException>(() =>
            RepositorySelection.Normalize(null, ["owner/a", "  "],
                MultiRepoOptions.HardMaxRepos));
    }

    [Fact]
    public void Normalize_rejects_case_insensitive_duplicates()
    {
        Should.Throw<RepositorySelectionException>(() =>
            RepositorySelection.Normalize(null, ["Owner/A", "owner/a"],
                MultiRepoOptions.HardMaxRepos));
    }

    [Fact]
    public void Normalize_empty_selection_returns_empty()
    {
        RepositorySelection.Normalize(null, null,
            MultiRepoOptions.HardMaxRepos).ShouldBeEmpty();
        RepositorySelection.Normalize(null, [],
            MultiRepoOptions.HardMaxRepos).ShouldBeEmpty();
    }

    // ---- RF-002: MultiRepoLayout --------------------------------------

    [Fact]
    public void ShortName_extracts_last_segment()
    {
        MultiRepoLayout.ShortName("octocat/hello").ShouldBe("hello");
        MultiRepoLayout.ShortName(
            "https://github.com/octocat/hello.git").ShouldBe("hello");
        MultiRepoLayout.ShortName(
            "git@gitlab.com:grp/sub/proj.git").ShouldBe("proj");
        MultiRepoLayout.ShortName("weird name!").ShouldBe("weird-name-");
    }

    [Fact]
    public void DirNames_disambiguates_collisions_by_position()
    {
        var dirs = MultiRepoLayout.DirNames(["a/lib", "b/lib", "a/core"]);
        dirs.ShouldBe(["lib", "lib-2", "core"]);
    }

    [Fact]
    public void PathFor_single_repo_keeps_legacy_path()
    {
        MultiRepoLayout.PathFor(["o/a"], "o/a")
            .ShouldBe(MultiRepoLayout.LegacyRepoPath);
    }

    [Fact]
    public void PathFor_multi_repo_uses_repos_dir()
    {
        var repos = new List<string> { "o/primary", "o/secondary" };
        MultiRepoLayout.PathFor(repos, "o/primary")
            .ShouldBe("/workspace/repos/primary");
        MultiRepoLayout.PathFor(repos, "o/secondary")
            .ShouldBe("/workspace/repos/secondary");
        MultiRepoLayout.PathFor(repos, "o/unknown").ShouldBeNull();
    }

    [Fact]
    public void ResolveRepo_defaults_to_primary_and_matches_short_name()
    {
        IReadOnlyList<string> repos = ["o/primary", "o/secondary"];
        MultiRepoLayout.ResolveRepo(repos, null).ShouldBe("o/primary");
        MultiRepoLayout.ResolveRepo(repos, "").ShouldBe("o/primary");
        MultiRepoLayout.ResolveRepo(repos, "secondary")
            .ShouldBe("o/secondary");
        MultiRepoLayout.ResolveRepo(repos, "O/SECONDARY")
            .ShouldBe("o/secondary");
        MultiRepoLayout.ResolveRepo(repos, "o/missing").ShouldBeNull();
    }

    [Fact]
    public void TryRepoHost_maps_shorthand_to_github()
    {
        MultiRepoLayout.TryRepoHost("o/a").ShouldBe("github.com");
        MultiRepoLayout.TryRepoHost("https://gitlab.com/g/r")
            .ShouldBe("gitlab.com");
        MultiRepoLayout.TryRepoHost("/tmp/x").ShouldBeNull();
        MultiRepoLayout.TryRepoHost(null).ShouldBeNull();
    }

    // ---- RF-002: MultiRepoCloner --------------------------------------

    [Fact]
    public async Task Cloner_clones_all_in_parallel_with_mapped_paths()
    {
        var repos = new List<string> { "o/api", "o/web", "o/docs" };
        var cloned = new List<(string Repo, string Path)>();
        var gate = new TaskCompletionSource();

        // Gate segura todos os clones até que todos tenham iniciado —
        // prova de paralelismo (3 in-flight simultâneos).
        var started = 0;
        var outcomes = await MultiRepoCloner.CloneAsync(repos,
            async (repo, path, ct) =>
            {
                cloned.Add((repo, path));
                if (Interlocked.Increment(ref started) == repos.Count)
                {
                    gate.TrySetResult();
                }
                await gate.Task.WaitAsync(ct);
            },
            NullLogger.Instance, CancellationToken.None);

        outcomes.Count.ShouldBe(3);
        outcomes.ShouldAllBe(o => o.Ok);
        outcomes.Select(o => o.Path).ShouldBe(
            ["/workspace/repos/api", "/workspace/repos/web",
                "/workspace/repos/docs"]);
        cloned.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Cloner_primary_failure_aborts()
    {
        var repos = new List<string> { "o/api", "o/web" };
        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            MultiRepoCloner.CloneAsync(repos,
                (repo, _, ct) => repo == "o/api"
                    ? throw new InvalidOperationException("boom")
                    : Task.CompletedTask,
                NullLogger.Instance, CancellationToken.None));
        ex.Message.ShouldContain("o/api");
    }

    [Fact]
    public async Task Cloner_secondary_failure_is_tolerated()
    {
        var repos = new List<string> { "o/api", "o/web", "o/docs" };
        var outcomes = await MultiRepoCloner.CloneAsync(repos,
            (repo, _, ct) => repo == "o/web"
                ? throw new InvalidOperationException("denied")
                : Task.CompletedTask,
            NullLogger.Instance, CancellationToken.None);

        outcomes[0].Ok.ShouldBeTrue();
        outcomes[1].Ok.ShouldBeFalse();
        outcomes[1].Error.ShouldBe("denied");
        outcomes[2].Ok.ShouldBeTrue();
    }

    [Fact]
    public void Cloner_state_value_marks_primary_and_failures()
    {
        var outcomes = new List<MultiRepoCloner.RepoCloneOutcome>
        {
            new("o/api", "/workspace/repos/api", true, null),
            new("o/web", "/workspace/repos/web", false, "denied"),
        };
        var json = System.Text.Json.JsonSerializer.Serialize(
            MultiRepoCloner.ToStateValue(outcomes));
        json.ShouldContain("\"primary\":true");
        json.ShouldContain("\"status\":\"failed\"");
        json.ShouldContain("denied");
    }

    // ---- RF-001/RF-004: persisted aggregates ---------------------------

    [Fact]
    public void ConversationRecord_repositories_json_roundtrips()
    {
        var rec = new ConversationRecord
        {
            Id = Guid.NewGuid(),
            SelectedRepository = "o/api",
            ExecutionStatus = "Running",
            RepositoriesJson = ConversationRecord.RepositoriesToJson(
                ["o/api", "o/web"]),
        };
        rec.GetRepositories().ShouldBe(["o/api", "o/web"]);
        rec.ToDomain().Repositories.ShouldBe(["o/api", "o/web"]);
        rec.ToDomain().EffectiveRepositories[0].ShouldBe("o/api");
    }

    [Fact]
    public void ConversationRecord_legacy_row_falls_back()
    {
        var rec = new ConversationRecord
        {
            Id = Guid.NewGuid(),
            SelectedRepository = "o/legacy",
            ExecutionStatus = "Stopped",
        };
        rec.GetRepositories().ShouldBe(["o/legacy"]);
        rec.ToDomain().Repositories.ShouldBeNull();
        rec.ToDomain().EffectiveRepositories.ShouldBe(["o/legacy"]);
    }

    [Fact]
    public void ConversationRecord_pull_requests_json_aggregates()
    {
        var rec = new ConversationRecord
        {
            Id = Guid.NewGuid(),
            SelectedRepository = "o/api",
            ExecutionStatus = "Running",
            PullRequestsJson = ConversationRecord.PullRequestsToJson(
            [
                new ConversationPullRequest("o/api", "u1", 1, "open", "s1"),
                new ConversationPullRequest("o/web", "u2", 2, "open", "s2"),
            ]),
        };
        var prs = rec.GetPullRequests();
        prs.Count.ShouldBe(2);
        prs[1].Repository.ShouldBe("o/web");
        rec.ToDomain().PullRequests!.Count.ShouldBe(2);
    }

    [Fact]
    public void ConversationRecord_legacy_pull_request_fields_fallback()
    {
        var rec = new ConversationRecord
        {
            Id = Guid.NewGuid(),
            SelectedRepository = "o/legacy",
            ExecutionStatus = "Finished",
            PullRequestUrl = "https://x/pr/9",
            PullRequestNumber = 9,
            PullRequestState = "open",
            PullRequestHeadSha = "abc",
        };
        var prs = rec.GetPullRequests();
        prs.ShouldHaveSingleItem();
        prs[0].Repository.ShouldBe("o/legacy");
        prs[0].Number.ShouldBe(9);
    }

    // ---- RF-003: tool repo routing -------------------------------------

    private ConversationRecord NewConversation(
        IReadOnlyList<string> repos)
    {
        var rec = new ConversationRecord
        {
            Id = Guid.NewGuid(),
            OrgId = _orgId,
            SandboxId = Guid.NewGuid(),
            SelectedRepository = repos[0],
            CreatedByUserId = Guid.NewGuid().ToString(),
            ExecutionStatus =
                ConversationExecutionStatus.Running.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            RepositoriesJson = ConversationRecord.RepositoriesToJson(repos),
        };
        _db.Conversations.Add(rec);
        _db.SaveChanges();
        return rec;
    }

    private IServiceProvider Services() =>
        new ServiceCollection().AddSingleton(_db).BuildServiceProvider();

    [Fact]
    public async Task ResolveRepoPath_routes_short_name_to_repos_dir()
    {
        var conv = NewConversation(["o/api", "o/web"]);
        var (path, err) = await Infrastructure.Agent.Tools.BuiltinTools
            .ResolveRepoPathAsync(Services(), conv.Id, "web",
                CancellationToken.None);
        err.ShouldBeNull();
        path.ShouldBe("/workspace/repos/web");
    }

    [Fact]
    public async Task ResolveRepoPath_rejects_unknown_repo()
    {
        var conv = NewConversation(["o/api", "o/web"]);
        var (path, err) = await Infrastructure.Agent.Tools.BuiltinTools
            .ResolveRepoPathAsync(Services(), conv.Id, "../evil",
                CancellationToken.None);
        path.ShouldBeNull();
        err.ShouldNotBeNull();
        err.ShouldContain("unknown repo");
    }

    [Fact]
    public async Task ResolveRepoPath_null_arg_defaults_primary()
    {
        var conv = NewConversation(["o/api", "o/web"]);
        var (path, err) = await Infrastructure.Agent.Tools.BuiltinTools
            .ResolveRepoPathAsync(Services(), conv.Id, null,
                CancellationToken.None);
        err.ShouldBeNull();
        path.ShouldBeNull(); // null → GitService usa o default legado
    }

    // ---- RF-003/004: ScmPullRequestService por repo --------------------

    private ScmPullRequestService NewPrService(
        RecordingGit git, RecordingEvents events,
        RecordingSandboxes sandboxes)
    {
        var secrets = new FakeSecrets();
        var conns = new ScmConnectionService(_db, secrets,
            new CapturingAudit(), new FakeHttpFactory(),
            new ScmProviderRegistry([new FakeProvider()]),
            NullLogger<ScmConnectionService>.Instance);
        return new ScmPullRequestService(_db, conns,
            new ScmProviderRegistry([new FakeProvider()]),
            sandboxes, git, events, new CapturingAudit(),
            NullLogger<ScmPullRequestService>.Instance);
    }

    private void SeedConnection(params string[] repos)
    {
        _db.ScmConnections.Add(new ScmConnectionRecord
        {
            Id = Guid.NewGuid(),
            OrgId = _orgId,
            Kind = "github",
            InstallationId = "1",
            PemSecretRef = "p",
            WebhookSecretRef = "w",
            ReposJson = System.Text.Json.JsonSerializer.Serialize(repos),
            CreatedByUserId = "u",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Pr_create_on_secondary_repo_routes_and_aggregates()
    {
        var conv = NewConversation(["o/api", "o/web"]);
        SeedConnection("o/api", "o/web");
        var git = new RecordingGit();
        var events = new RecordingEvents();
        var svc = NewPrService(git, events,
            new RecordingSandboxes(conv.SandboxId!.Value));

        var first = await svc.CreateAsync(conv.Id,
            new ScmPrCreateRequest("t", "b", null, "", false, null),
            null, CancellationToken.None);
        first.Error.ShouldBeNull();

        var second = await svc.CreateAsync(conv.Id,
            new ScmPrCreateRequest("t2", "b2", null, "", false, null,
                Repository: "o/web"),
            null, CancellationToken.None);
        second.Error.ShouldBeNull();

        // push do PR secundário foi roteado ao mount do repo web.
        git.Pushes.ShouldContain(p => p.RepoPath == "/workspace/repos/web");

        var row = await _db.Conversations.IgnoreQueryFilters()
            .FirstAsync(c => c.Id == conv.Id);
        var prs = row.GetPullRequests();
        prs.Count.ShouldBe(2);
        prs.Select(p => p.Repository)
            .ShouldBe(["o/api", "o/web"]);
        // legado espelha APENAS o primário.
        row.PullRequestNumber.ShouldBe(7);
        row.SelectedRepository.ShouldBe("o/api");
        events.Appended.OfType<PullRequestCreatedEvent>()
            .Count().ShouldBe(2);
    }

    [Fact]
    public async Task Pr_duplicate_detected_per_repository()
    {
        var conv = NewConversation(["o/api", "o/web"]);
        SeedConnection("o/api", "o/web");
        var svc = NewPrService(new RecordingGit(), new RecordingEvents(),
            new RecordingSandboxes(conv.SandboxId!.Value));

        await svc.CreateAsync(conv.Id,
            new ScmPrCreateRequest("t", "b", null, "", false, null,
                Repository: "o/web"),
            null, CancellationToken.None);
        // duplicata só no repo alvo → AlreadyExists…
        var dup = await svc.CreateAsync(conv.Id,
            new ScmPrCreateRequest("t", "b", null, "", false, null,
                Repository: "web"),
            null, CancellationToken.None);
        dup.Error.ShouldBe(ScmPrError.AlreadyExists);
        // …mas o primário continua livre.
        var ok = await svc.CreateAsync(conv.Id,
            new ScmPrCreateRequest("t", "b", null, "", false, null),
            null, CancellationToken.None);
        ok.Error.ShouldBeNull();
    }

    [Fact]
    public async Task Pr_invalid_repository_rejected()
    {
        var conv = NewConversation(["o/api", "o/web"]);
        var svc = NewPrService(new RecordingGit(), new RecordingEvents(),
            new RecordingSandboxes(conv.SandboxId!.Value));
        var result = await svc.CreateAsync(conv.Id,
            new ScmPrCreateRequest("t", "b", null, "", false, null,
                Repository: "o/unknown"),
            null, CancellationToken.None);
        result.Error.ShouldBe(ScmPrError.InvalidRepository);
    }

    [Fact]
    public async Task Pr_secondary_without_connection_forbidden()
    {
        var conv = NewConversation(["o/api", "o/web"]);
        SeedConnection("o/api"); // só o primário tem conexão
        var svc = NewPrService(new RecordingGit(), new RecordingEvents(),
            new RecordingSandboxes(conv.SandboxId!.Value));
        var result = await svc.CreateAsync(conv.Id,
            new ScmPrCreateRequest("t", "b", null, "", false, null,
                Repository: "o/web"),
            null, CancellationToken.None);
        result.Error.ShouldBe(ScmPrError.Forbidden);
    }

    [Fact]
    public async Task Pr_primary_without_connection_keeps_404()
    {
        var conv = NewConversation(["o/api", "o/web"]);
        SeedConnection("o/web"); // primário sem conexão
        var svc = NewPrService(new RecordingGit(), new RecordingEvents(),
            new RecordingSandboxes(conv.SandboxId!.Value));
        var result = await svc.CreateAsync(conv.Id,
            new ScmPrCreateRequest("t", "b", null, "", false, null),
            null, CancellationToken.None);
        result.Error.ShouldBe(ScmPrError.NoConnection);
    }

    // ---- fakes ---------------------------------------------------------

    private sealed class RecordingGit : IGitService
    {
        public List<(string Head, string? RepoPath)> Pushes { get; } = [];

        public Task<GitPushResult> PushAsync(SandboxModel s, string head,
            string? rb, bool fwl, string? o, CancellationToken ct,
            string? repoPath = null)
        {
            Pushes.Add((head, repoPath));
            return Task.FromResult(new GitPushResult(true, "sha-head", null));
        }

        public Task<string?> GetCurrentBranchAsync(SandboxModel s,
            CancellationToken ct, string? repoPath = null) =>
            Task.FromResult<string?>("work");
        public Task<bool> HasRepositoryAsync(SandboxModel s,
            CancellationToken ct, string? repoPath = null) =>
            Task.FromResult(true);
        public Task CloneAsync(SandboxModel s, string u, string b,
            string? o, CancellationToken ct, string? repoPath = null,
            string? credentialToken = null) => Task.CompletedTask;
        public Task<bool> ProvisionGhAuthAsync(SandboxModel s, string? o,
            CancellationToken ct) => Task.FromResult(false);
        public Task<bool> ProvisionTokenAuthAsync(SandboxModel s, string h,
            string t, CancellationToken ct) => Task.FromResult(true);
        public Task<GitChanges> GetChangesAsync(SandboxModel s,
            CancellationToken ct, string? repoPath = null) =>
            throw new NotImplementedException();
        public Task<GitDiffReport?> GetDiffAsync(SandboxModel s, string? p,
            CancellationToken ct, string? repoPath = null) =>
            throw new NotImplementedException();
        public Task<string?> GetRawDiffAsync(SandboxModel s, string? p,
            CancellationToken ct, string? repoPath = null) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<GitLogEntry>> GetLogAsync(SandboxModel s,
            int l, CancellationToken ct, string? repoPath = null) =>
            throw new NotImplementedException();
        public Task<string?> GetFileAtRefAsync(SandboxModel s, string p,
            string r, CancellationToken ct, string? repoPath = null) =>
            throw new NotImplementedException();
        public Task<GitCommitResult> CommitAsync(SandboxModel s, string m,
            IReadOnlyList<string>? f, CancellationToken ct,
            string? repoPath = null) =>
            throw new NotImplementedException();
    }

    private sealed class RecordingEvents : IEventService
    {
        public List<EventBase> Appended { get; } = [];

        public ValueTask<EventBase> AppendAsync(Guid conversationId,
            EventBase e, CancellationToken ct)
        {
            Appended.Add(e);
            return ValueTask.FromResult(e);
        }

        public Task<IReadOnlyList<EventBase>> GetBatchAsync(
            Guid conversationId, IReadOnlyCollection<string> ids,
            CancellationToken ct) => throw new NotImplementedException();
        public Task<EventPage> SearchAsync(Guid conversationId,
            EventQuery query, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<long> CountAsync(Guid conversationId, EventQuery q,
            CancellationToken ct) => Task.FromResult(0L);
        public async IAsyncEnumerable<EventBase> SubscribeAsync(
            Guid conversationId,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RecordingSandboxes(Guid id) : ISandboxService
    {
        private SandboxModel Sbx() => new(id, Guid.NewGuid(), null, null,
            SandboxStatus.Running, null, DateTimeOffset.UtcNow, null, null);
        public Task<SandboxModel> StartAsync(Guid specId,
            Guid? conversationId, CancellationToken ct) =>
            Task.FromResult(Sbx());
        public Task<SandboxModel> PauseAsync(Guid sandboxId,
            CancellationToken ct) => Task.FromResult(Sbx());
        public Task<SandboxModel> StopAsync(Guid sandboxId,
            CancellationToken ct) => Task.FromResult(Sbx());
        public Task<SandboxModel> ResumeAsync(Guid sandboxId,
            CancellationToken ct) => Task.FromResult(Sbx());
        public Task DeleteAsync(Guid sandboxId, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<SandboxModel?> GetAsync(Guid sandboxId,
            CancellationToken ct) => Task.FromResult<SandboxModel?>(Sbx());
        public Task<IReadOnlyList<SandboxModel>> SearchAsync(
            SandboxStatus? status, Guid? conversationId,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SandboxModel>>([Sbx()]);
        public Task<SandboxSpec> UpsertSpecAsync(SandboxSpec spec,
            CancellationToken ct) => Task.FromResult(spec);
        public Task<SandboxSpec?> GetSpecAsync(Guid specId,
            CancellationToken ct) =>
            Task.FromResult<SandboxSpec?>(null);
        public Task SweepOrphansAsync(CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class FakeProvider : IScmProvider
    {
        public ScmKind Kind => ScmKind.GitHub;
        public Task<ScmPullRequest> CreatePullRequestAsync(
            ScmConnection connection, string repository,
            ScmPrCreateRequest request, CancellationToken ct) =>
            Task.FromResult(new ScmPullRequest(
                $"https://github.com/{repository}/pull/7", 7, "open",
                "sha-head", request.Title));
        public Task<ScmPullRequest?> GetPullRequestAsync(
            ScmConnection connection, string repository, int number,
            CancellationToken ct) =>
            Task.FromResult<ScmPullRequest?>(null);
        public Task<long> CreateIssueCommentAsync(
            ScmConnection connection, string repository, int issueNumber,
            string body, CancellationToken ct) => Task.FromResult(1L);
        public Task<ScmConnectionDiagnostic> TestConnectionAsync(
            ScmConnection connection, CancellationToken ct) =>
            Task.FromResult(new ScmConnectionDiagnostic(
                true, "fake", "1.0", 1, "ok"));
        public Task<long> ReplyToReviewCommentAsync(ScmConnection connection,
            string repository, int pullRequestNumber,
            long inReplyToCommentId, string body, CancellationToken ct) =>
            Task.FromResult(1L);
        public Task SetCommitStatusAsync(ScmConnection connection,
            string repository, string sha, string state, string context,
            string? targetUrl, string? description,
            CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ScmPullRequestFile>>
            GetPullRequestFilesAsync(ScmConnection connection,
                string repository, int pullRequestNumber,
                CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ScmPullRequestFile>>([]);
        public Task<string?> GetFileContentAsync(ScmConnection connection,
            string repository, string path, string? reference,
            CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> ListDirectoryAsync(
            ScmConnection connection, string repository,
            string directoryPath, string? reference,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<ScmReviewResult?> CreateReviewAsync(
            ScmConnection connection, string repository,
            int pullRequestNumber, string headSha, string body,
            IReadOnlyList<ScmReviewComment> comments,
            CancellationToken ct) =>
            Task.FromResult<ScmReviewResult?>(null);
        public Task<IReadOnlyList<ScmCheckRun>> GetCheckRunsAsync(
            ScmConnection connection, string repository, string reference,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ScmCheckRun>>([]);
        public Task<string?> GetCheckRunLogsAsync(
            ScmConnection connection, string repository, long checkRunId,
            CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<string?> GetWorkflowRunLogsAsync(
            ScmConnection connection, string repository, long workflowRunId,
            CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<ScmCommitPullRequest>>
            GetPullRequestsForCommitAsync(ScmConnection connection,
                string repository, string sha, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ScmCommitPullRequest>>([]);
    }

    private sealed class FakeSecrets : Application.Contracts.ISecretStore
    {
        public Task<string?> ResolveAsync(string scope, string ownerId,
            string name, CancellationToken ct) =>
            Task.FromResult<string?>("s");
        public Task<IReadOnlyList<string>> ListNamesAsync(string scope,
            string ownerId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task SetAsync(string scope, string ownerId, string name,
            string plaintext, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> DeleteAsync(string scope, string ownerId,
            string name, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class CapturingAudit : IAuditService
    {
        public List<string> Actions { get; } = [];
        public Task RecordAsync(string action, string? actorUserId = null,
            Guid? orgId = null, string? targetType = null,
            string? targetId = null, string? ip = null,
            string? userAgent = null, object? metadata = null,
            CancellationToken cancellationToken = default)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }
        public Task<(IReadOnlyList<AuditEntry> Items, int Total)> QueryAsync(
            string? userId, string? action, DateTimeOffset? from,
            DateTimeOffset? to, int page, int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<AuditEntry>, int)>(([], 0));
    }

    private sealed class FakeHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}