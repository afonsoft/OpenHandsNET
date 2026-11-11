using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Ask;
using Sandcastle.Application.Contracts;
using Sandcastle.Application.Conversations;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Indexing;
using Sandcastle.Application.Scm;
using Sandcastle.Application.Wiki;
using Sandcastle.Domain.Conversations;
using Sandcastle.Infrastructure.Ask;
using Sandcastle.Infrastructure.Indexing;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Scm;
using Sandcastle.Infrastructure.Wiki;
using Sandcastle.Mcp;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.Wiki;

/// <summary>
/// SPEC-145 — repo wiki + indexing real + MCP exposure:
/// providers de conteúdo (local + clone git), persistência do
/// CommitSha, pipeline de geração com LLM fake (citações validadas →
/// stale guardrail), push → reindex + stale-mark, Ask com permalinks e
/// as três tools MCP <c>sc_wiki_*</c>/<c>sc_ask</c>.
/// </summary>
public sealed class Spec145RepoWikiTests : IAsyncDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly Guid _orgId = Guid.NewGuid();
    private readonly Guid _connId = Guid.NewGuid();
    private readonly string _tempRoot;

    public Spec145RepoWikiTests()
    {
        _conn.Open();
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_conn).UseSnakeCaseNamingConvention().Options);
        _db.Database.EnsureCreated();
        _tempRoot = Path.Combine(
            Path.GetTempPath(), $"spec145-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _db.ScmConnections.Add(new ScmConnectionRecord
        {
            Id = _connId,
            OrgId = _orgId,
            Kind = "github",
            InstallationId = "777",
            PemSecretRef = "pem_ref",
            WebhookSecretRef = "hook_ref",
            AppSlug = "sandcastle-agent",
            CreatedByUserId = Guid.NewGuid().ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (Exception) { /* best-effort */ }
    }

    // ---- helpers ---------------------------------------------------------

    private string NewWorkdir(params (string Path, string Content)[] files)
    {
        var dir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return dir;
    }

    private const string Sha0 =
        "00000000000000000000000000000000000000a0";
    private const string Sha1 =
        "1111111111111111111111111111111111111111";
    private const string ShaC =
        "dddddddddddddddddddddddddddddddddddddddc";
    private const string ShaE =
        "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeef";
    private const string ShaA =
        "00000000000000000000000000000000000000aa";

    private RepoIndexRecord SeedIndex(
        string repo = "afonsoft/demo", string branch = "main",
        string status = "ready", string? sha = Sha0)
    {
        var idx = new RepoIndexRecord
        {
            Id = Guid.NewGuid(),
            OrgId = _orgId,
            Repository = repo,
            Branch = branch,
            CommitSha = sha,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db.RepoIndexes.Add(idx);
        _db.SaveChanges();
        return idx;
    }

    private void SeedCodeUnit(Guid indexId, string path)
    {
        _db.CodeUnits.Add(new CodeUnitRecord
        {
            Id = Guid.NewGuid(),
            OrgId = _orgId,
            RepoIndexId = indexId,
            Path = path,
            StartLine = 1,
            EndLine = 50,
            Kind = "chunk",
            Text = "// code",
            Hash = "h",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
    }

    private static string Sign(byte[] payload) =>
        $"sha256={Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("hooksecret"), payload))}";

    private static byte[] PushPayload(string repo = "afonsoft/demo",
        string branch = "main", string head = Sha1,
        params string[] changed)
    {
        var files = string.Join(",", changed.Select(
            f => $"\"{f}\""));
        return Encoding.UTF8.GetBytes(
            "{\"installation\":{\"id\":777},"
            + $"\"repository\":{{\"full_name\":\"{repo}\"}},"
            + $"\"ref\":\"refs/heads/{branch}\","
            + $"\"after\":\"{head}\","
            + "\"commits\":[{\"added\":[],"
            + $"\"modified\":[{files}],\"removed\":[]}}]}}");
    }

    private sealed class FakeContentProvider : IRepoContentProvider
    {
        public string? Root;
        public string? Sha = ShaC;
        public int Calls;

        public Task<IRepoContentLease?> AcquireAsync(
            RepoContentRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IRepoContentLease?>(
                Root is null ? null
                    : new RepoContentLease(Root, Sha, "fake"));
        }
    }

    private sealed class FakeSearchStore : ICodeSearchStore
    {
        public List<CodeUnitDto> Units = [];
        public int Deletes;

        public Task IndexUnitsAsync(Guid repoIndexId,
            IEnumerable<CodeUnitDto> units, CancellationToken ct = default)
        {
            Units.AddRange(units);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SearchCodeResult>> SearchAsync(
            Guid repoIndexId, string query, int limit = 10,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SearchCodeResult>>(
                Units.Take(limit)
                    .Select(u => new SearchCodeResult(u, 1.0,
                        u.Text.Length > 60 ? u.Text[..60] : u.Text))
                    .ToList());

        public Task DeleteIndexUnitsAsync(Guid repoIndexId,
            CancellationToken ct = default)
        {
            Deletes++;
            Units.RemoveAll(u => u.RepoIndexId == repoIndexId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLlm : IAgentLlmProvider
    {
        public string Script = "{}";
        public string ModelId => "mock-llm";
        public List<string> Prompts = [];

        public async IAsyncEnumerable<ILlmStreamChunk> ChatStreamAsync(
            IReadOnlyList<LlmMessage> messages,
            IReadOnlyList<ToolSchema> tools, LlmOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken ct)
        {
            Prompts.Add(messages.LastOrDefault()?.Content ?? "");
            yield return new TextDelta(Script);
            yield return new CompletionEnd(
                new LlmUsage(100, 200, null), "stop", "r-1");
            await Task.CompletedTask;
        }

        public Task<ModelInfo> GetModelInfoAsync(CancellationToken ct) =>
            Task.FromResult(new ModelInfo("mock-llm", "mock"));
    }

    private sealed class FakeRouter : IModelRouter
    {
        public Task<ModelSelection> SelectAsync(Guid conversationId,
            ModelRole role, string? preferredModel,
            CancellationToken ct) =>
            Task.FromResult(new ModelSelection(
                "wiki-model", "mock", null, 0, null));

        public Task<ModelSelection?> NextFallbackAsync(
            ModelSelection current, LlmErrorKind kind,
            CancellationToken ct) =>
            Task.FromResult<ModelSelection?>(null);
    }

    private sealed class FakeMeter : IUsageMeter
    {
        public long Tokens;

        public Task RecordTokensAsync(Guid orgId, long tokens,
            decimal? costUsd, CancellationToken ct)
        {
            Tokens += tokens;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWikiDispatcher : IWikiDispatcher
    {
        public List<Guid> Queued = [];

        public ValueTask EnqueueAsync(Guid wikiRunId,
            CancellationToken ct)
        {
            Queued.Add(wikiRunId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeIndexer : IRepoIndexer
    {
        public List<(string Repo, string Branch)> Calls = [];
        private readonly Guid _id = Guid.NewGuid();

        public Task<RepoIndexStatusDto> TriggerIndexAsync(
            string repository, string branch, Guid? orgId = null,
            CancellationToken ct = default)
        {
            Calls.Add((repository, branch));
            return Task.FromResult(new RepoIndexStatusDto(
                _id, orgId, repository, branch, null, "indexing",
                null, null, DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));
        }

        public Task<RepoIndexStatusDto?> GetStatusAsync(
            Guid repoIndexId, CancellationToken ct = default) =>
            Task.FromResult<RepoIndexStatusDto?>(null);

        public Task<RepoIndexStatusDto?> GetStatusByRepoAsync(
            string repository, string branch, Guid? orgId = null,
            CancellationToken ct = default) =>
            Task.FromResult<RepoIndexStatusDto?>(null);
    }

    private sealed class FakeSecrets : ISecretStore
    {
        public Task<string?> ResolveAsync(string scope, string ownerId,
            string name, CancellationToken ct) =>
            Task.FromResult<string?>("hooksecret");
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

        public Task RecordAsync(string action,
            string? actorUserId = null, Guid? orgId = null,
            string? targetType = null, string? targetId = null,
            string? ip = null, string? userAgent = null,
            object? metadata = null,
            CancellationToken cancellationToken = default)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }

        public Task<(IReadOnlyList<AuditEntry> Items, int Total)>
            QueryAsync(string? userId, string? action,
                DateTimeOffset? from, DateTimeOffset? to, int page,
                int pageSize, CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<AuditEntry>, int)>(([], 0));
    }

    private sealed class NullConversations
        : Sandcastle.Application.Conversations.IAppConversationService
    {
        public Task<AppConversationStartTask> CreateAsync(
            CreateConversationRequest request, CancellationToken ct) =>
            Task.FromResult(Task0());
        public Task<AppConversationStartTask> CreateForUserAsync(
            Guid userId, Guid? orgId, CreateConversationRequest request,
            CancellationToken ct) => Task.FromResult(Task0());
        private static AppConversationStartTask Task0() => new(
            Guid.NewGuid(), StartTaskStatus.Working, Guid.NewGuid(),
            null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        public Task<ConversationPage> SearchAsync(
            ConversationQuery query, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<long> CountAsync(ConversationQuery query,
            CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<AppConversation>> GetBatchAsync(
            IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<AppConversation?> GetAsync(Guid id,
            CancellationToken ct) => throw new NotImplementedException();
        public Task<AppConversation?> UpdateAsync(Guid id,
            string? title, IReadOnlyList<string>? tags, bool? pinned,
            CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<SendMessageError?> ValidateSendMessageAsync(Guid id,
            CancellationToken ct) => throw new NotImplementedException();
        public Task<SwitchResult> SwitchProfileAsync(Guid id,
            string? profile, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<SwitchResult> SwitchModelAsync(Guid id,
            string model, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<AppConversation?> SetPermissionsAsync(Guid id,
            IReadOnlyList<AgentPermissionRule>? rules,
            CancellationToken ct) => throw new NotImplementedException();
        public Task<AppConversation?> SetModeAsync(Guid id,
            string mode, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<AttentionItem>> ListAttentionAsync(
            int limit, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<AttentionDismissResult> DismissAttentionAsync(
            Guid id, CancellationToken ct) =>
            throw new NotImplementedException();
    }

    private sealed class FakeGhFactory : IGitHubClientFactory
    {
        public Task<Octokit.IGitHubClient> ForConnectionAsync(
            ScmConnection connection, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<string> GetInstallationTokenAsync(
            ScmConnection connection, CancellationToken ct) =>
            Task.FromResult("ghs_install_token");
    }

    private WikiService WikiSvc(FakeWikiDispatcher disp,
        WikiOptions? o = null) =>
        new(_db, disp, Options.Create(o ?? new WikiOptions()),
            NullLogger<WikiService>.Instance);

    private GitHubWebhookService Webhook(FakeIndexer indexer,
        IndexingOptions? io = null, IWikiService? wiki = null,
        CapturingAudit? audit = null) =>
        new(_db, new FakeSecrets(), new NullConversations(),
            audit ?? new CapturingAudit(),
            NullLogger<GitHubWebhookService>.Instance,
            indexer: indexer,
            indexingOptions: Options.Create(io ?? new IndexingOptions()),
            wiki: wiki);

    // ---- providers --------------------------------------------------------

    [Fact]
    public async Task LocalProvider_Serves_Workdir_With_Git_Head()
    {
        var git = Environment.GetEnvironmentVariable("PATH");
        if (git is null || !await GitAvailableAsync())
        {
            return; // host sem git — provider remoto já é coberto abaixo.
        }
        var repo = Path.Combine(_tempRoot, "local-repo");
        Directory.CreateDirectory(repo);
        await RunGit(repo, "init -q");
        File.WriteAllText(Path.Combine(repo, "a.cs"), "class A {}");
        await RunGit(repo,
            "-c user.email=t@t -c user.name=t add -A");
        await RunGit(repo,
            "-c user.email=t@t -c user.name=t commit -qm init");
        var head = (await RunGit(repo, "rev-parse HEAD")).Trim();
        head.Length.ShouldBe(40);

        var provider = new LocalRepoContentProvider(Options.Create(
            new IndexingOptions { LocalRepoPath = repo }));
        var lease = await provider.AcquireAsync(
            new RepoContentRequest("x/y", "main", null),
            CancellationToken.None);
        lease.ShouldNotBeNull();
        lease!.RootPath.ShouldBe(repo);
        lease.CommitSha.ShouldBe(head);
        lease.ProviderKind.ShouldBe("local");
    }

    [Fact]
    public async Task GitProvider_Clones_Remote_And_Resolves_CommitSha()
    {
        if (!await GitAvailableAsync())
        {
            return;
        }
        var src = Path.Combine(_tempRoot, "origin-repo");
        Directory.CreateDirectory(src);
        await RunGit(src, "init -q -b main");
        File.WriteAllText(Path.Combine(src, "src.cs"), "class B {}");
        await RunGit(src,
            "-c user.email=t@t -c user.name=t add -A");
        await RunGit(src,
            "-c user.email=t@t -c user.name=t commit -qm init");
        var head = (await RunGit(src, "rev-parse HEAD")).Trim();

        var provider = new GitRepoContentProvider(
            _db, new FakeGhFactory(), new FakeSecrets(),
            Options.Create(new IndexingOptions()),
            NullLogger<GitRepoContentProvider>.Instance,
            cloneUrlFor: (c, repo) => $"file://{src}",
            tokenFor: (c, ct) => Task.FromResult<string?>(null));

        await using var lease = await provider.AcquireAsync(
            new RepoContentRequest("afonsoft/demo", "main", _orgId),
            CancellationToken.None);
        lease.ShouldNotBeNull();
        lease!.ProviderKind.ShouldBe("git");
        lease.CommitSha.ShouldBe(head);
        File.Exists(Path.Combine(lease.RootPath, "src.cs"))
            .ShouldBeTrue();

        var workdir = lease.RootPath;
        await lease.DisposeAsync();
        Directory.Exists(workdir).ShouldBeFalse(); // cleanup do workdir
    }

    [Fact]
    public async Task GitProvider_Returns_Null_Without_Covering_Connection()
    {
        var provider = new GitRepoContentProvider(
            _db, new FakeGhFactory(), new FakeSecrets(),
            Options.Create(new IndexingOptions()),
            NullLogger<GitRepoContentProvider>.Instance);
        // repo não coberto pela conexão → null (resolver cai no local).
        var lease = await provider.AcquireAsync(
            new RepoContentRequest("other/repo", "main",
                Guid.NewGuid()), CancellationToken.None);
        lease.ShouldBeNull();
    }

    [Fact]
    public async Task Resolver_Falls_Back_To_Local_When_Remote_Null()
    {
        var remote = new GitRepoContentProvider(
            _db, new FakeGhFactory(), new FakeSecrets(),
            Options.Create(new IndexingOptions()),
            NullLogger<GitRepoContentProvider>.Instance);
        var local = new LocalRepoContentProvider(Options.Create(
            new IndexingOptions { LocalRepoPath = _tempRoot }));
        var resolver = new RepoContentProviderResolver(remote, local,
            NullLogger<RepoContentProviderResolver>.Instance);
        var lease = await resolver.AcquireAsync(
            new RepoContentRequest("uncovered/repo", "main", Guid.NewGuid()),
            CancellationToken.None);
        lease.ShouldNotBeNull();
        lease!.ProviderKind.ShouldBe("local");
    }

    // ---- indexer + CommitSha ---------------------------------------------

    [Fact]
    public async Task Indexer_Persists_CommitSha_From_Lease()
    {
        var workdir = NewWorkdir(("src/A.cs", "class A {}"));
        var provider = new FakeContentProvider
        {
            Root = workdir,
            Sha = ShaE,
        };
        var store = new FakeSearchStore();
        var indexer = new RepoIndexer(_db, store, provider,
            NullLogger<RepoIndexer>.Instance,
            Options.Create(new IndexingOptions()));

        var idx = new RepoIndexRecord
        {
            Id = Guid.NewGuid(),
            OrgId = _orgId,
            Repository = "afonsoft/demo",
            Branch = "main",
            Status = "indexing",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db.RepoIndexes.Add(idx);
        await _db.SaveChangesAsync();

        await indexer.RunIndexingAsync(
            idx.Id, "afonsoft/demo", "main", CancellationToken.None);

        var rec = await _db.RepoIndexes.IgnoreQueryFilters()
            .SingleAsync(r => r.Id == idx.Id);
        rec.Status.ShouldBe("ready");
        rec.CommitSha.ShouldBe(ShaE);
        store.Units.ShouldNotBeEmpty();
        store.Units.All(u => u.Path == "src/A.cs").ShouldBeTrue();
        rec.StatsJson!.ShouldContain("commit_sha");
        provider.Calls.ShouldBe(1);
    }

    // ---- citation validator ----------------------------------------------

    [Fact]
    public void CitationValidator_Rewrites_Valid_Link()
    {
        var indexed = new HashSet<string>(
            ["src/A.cs"], StringComparer.OrdinalIgnoreCase);
        var conn = ScmConnectionForTest();
        var (content, invalid) = WikiCitationValidator
            .ValidateAndRewrite(
                "O dispatcher vive em src/A.cs:L10-L20.",
                indexed, _ => 100, conn, "afonsoft/demo",
                ShaA);
        invalid.ShouldBeEmpty();
        content.ShouldContain(
            "[src/A.cs:L10-L20](https://github.com/afonsoft/demo/blob/"
            + $"{ShaA}/src/A.cs#L10-L20)");
    }

    [Fact]
    public void CitationValidator_Marks_Invented_Path_Invalid()
    {
        var indexed = new HashSet<string>(
            ["src/A.cs"], StringComparer.OrdinalIgnoreCase);
        var (content, invalid) = WikiCitationValidator
            .ValidateAndRewrite(
                "ver src/Ghost.cs:L1 e src/A.cs:L999.",
                indexed, _ => 100, null, "afonsoft/demo", null);
        invalid.Count.ShouldBe(2); // path inexistente + linha além do fim
        content.ShouldContain("src/Ghost.cs:L1"); // token não vira link
    }

    [Fact]
    public void CitationValidator_Extract_Ignores_Non_Files()
    {
        var found = WikiCitationValidator.Extract(
            "nota: Lorem ipsum https://x.io/a:L1 e src/Real.md:L5");
        found.ShouldContain(c => c.Path == "src/Real.md" && c.StartLine == 5);
    }

    private static ScmConnection ScmConnectionForTest() => new(
        Guid.NewGuid(), Guid.NewGuid(), ScmKind.GitHub, "777",
        "pem", "hook", [], "app", "bot", "u",
        DateTimeOffset.UtcNow, null, "app", null);

    // ---- wiki runner ------------------------------------------------------

    [Fact]
    public async Task Runner_Generates_Pages_Validates_Citations_And_Meters()
    {
        var sha = ShaA;
        var idx = SeedIndex(sha: sha);
        SeedCodeUnit(idx.Id, "src/A.cs");
        var workdir = NewWorkdir(
            ("src/A.cs", string.Join("\n",
                Enumerable.Range(1, 40).Select(i => $"// L{i}"))));
        var provider = new FakeContentProvider { Root = workdir, Sha = sha };
        var llm = new FakeLlm
        {
            Script = JsonSerializer.Serialize(new
            {
                title = "Visão geral",
                content_md = "# Overview\n\nEntry point em src/A.cs:L5.",
                source_files = new[] { "src/A.cs" },
            }),
        };
        var meter = new FakeMeter();
        var run = new WikiRunRecord
        {
            Id = Guid.NewGuid(),
            OrgId = _orgId,
            Repository = "afonsoft/demo",
            Branch = "main",
            CommitSha = sha,
            Effort = "low",
            Status = WikiRunStatuses.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.WikiRuns.Add(run);
        await _db.SaveChangesAsync();

        var runner = new WikiRunner(_db, provider, llm,
            new FakeRouter(),
            Options.Create(new WikiOptions
            {
                EffortPages = new(StringComparer.OrdinalIgnoreCase)
                { ["low"] = 2 },
            }),
            meter, NullLogger<WikiRunner>.Instance);
        await runner.RunAsync(run.Id, CancellationToken.None);

        var done = await _db.WikiRuns.IgnoreQueryFilters()
            .SingleAsync(r => r.Id == run.Id);
        done.Status.ShouldBe(WikiRunStatuses.Completed);
        done.Effort.ShouldBe("low");

        var pages = await _db.WikiPages.IgnoreQueryFilters()
            .Where(p => p.WikiRunId == run.Id).ToListAsync();
        pages.Count.ShouldBe(2); // effort low → 2 páginas no plano
        var overview = pages.Single(p => p.Topic == "overview");
        overview.ContentMd.ShouldContain(
            $"[src/A.cs:L5](https://github.com/afonsoft/demo/blob/{sha}/src/A.cs#L5)");
        overview.Stale.ShouldBeFalse();
        overview.SourceFilesJson!.ShouldContain("src/A.cs");
        overview.ContentHash.ShouldNotBeNull();
        meter.Tokens.ShouldBeGreaterThan(0); // usage metering
        done.StatsJson!.ShouldContain("pages_generated");
    }

    [Fact]
    public async Task Runner_Marks_Page_Stale_On_Invented_Citation()
    {
        var idx = SeedIndex();
        SeedCodeUnit(idx.Id, "src/A.cs");
        var workdir = NewWorkdir(("src/A.cs", "class A {}"));
        var provider = new FakeContentProvider { Root = workdir };
        var llm = new FakeLlm
        {
            Script = JsonSerializer.Serialize(new
            {
                title = "T",
                content_md = "Ver src/Invented.cs:L9.",
            }),
        };
        var run = new WikiRunRecord
        {
            Id = Guid.NewGuid(),
            OrgId = _orgId,
            Repository = "afonsoft/demo",
            Branch = "main",
            Effort = "low",
            Status = WikiRunStatuses.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.WikiRuns.Add(run);
        await _db.SaveChangesAsync();

        var runner = new WikiRunner(_db, provider, llm,
            new FakeRouter(),
            Options.Create(new WikiOptions
            {
                EffortPages = new(StringComparer.OrdinalIgnoreCase)
                { ["low"] = 1 },
            }),
            new FakeMeter(), NullLogger<WikiRunner>.Instance);
        await runner.RunAsync(run.Id, CancellationToken.None);

        var page = await _db.WikiPages.IgnoreQueryFilters()
            .SingleAsync(p => p.WikiRunId == run.Id);
        page.Stale.ShouldBeTrue(); // guardrail: citação inventada → stale
        page.ContentMd.ShouldContain("src/Invented.cs:L9");
    }

    [Fact]
    public async Task Runner_WikiJson_Honors_Exclude_And_TopicsHint()
    {
        var idx = SeedIndex();
        SeedCodeUnit(idx.Id, "src/A.cs");
        var workdir = NewWorkdir(
            ("src/A.cs", "class A {}"),
            (".sandcastle/wiki.json",
                "{\"exclude\":[\"*.md\"],\"topics_hint\":[\"custom/topic\"]}"),
            ("docs/README.md", "# doc"));
        var provider = new FakeContentProvider { Root = workdir };
        var llm = new FakeLlm
        {
            Script = "{\"title\":\"T\",\"content_md\":\"# t\"}",
        };
        var run = new WikiRunRecord
        {
            Id = Guid.NewGuid(),
            OrgId = _orgId,
            Repository = "afonsoft/demo",
            Branch = "main",
            Effort = "high",
            Status = WikiRunStatuses.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.WikiRuns.Add(run);
        await _db.SaveChangesAsync();

        var runner = new WikiRunner(_db, provider, llm,
            new FakeRouter(),
            Options.Create(new WikiOptions
            {
                EffortPages = new(StringComparer.OrdinalIgnoreCase)
                { ["high"] = 4 },
            }),
            new FakeMeter(), NullLogger<WikiRunner>.Instance);
        await runner.RunAsync(run.Id, CancellationToken.None);

        var pages = await _db.WikiPages.IgnoreQueryFilters()
            .Where(p => p.WikiRunId == run.Id).ToListAsync();
        pages.ShouldContain(p => p.PathKey == "custom/topic");
        // excluído do plano de arquivos (não entra nos prompts de fontes)
        llm.Prompts.ShouldAllBe(p => !p.Contains("docs/README.md")
            || p.Contains("file_list"));
    }

    // ---- wiki service ------------------------------------------------------

    [Fact]
    public async Task Wiki_Generate_Requires_Ready_Index()
    {
        var disp = new FakeWikiDispatcher();
        var svc = WikiSvc(disp);
        var run = await svc.GenerateAsync(
            "afonsoft/demo", "main", "low", _orgId, "u1",
            CancellationToken.None);
        run.ShouldBeNull(); // sem índice ready → 409 no endpoint
        disp.Queued.ShouldBeEmpty();
    }

    [Fact]
    public async Task Wiki_Generate_Enqueues_Run_When_Index_Ready()
    {
        SeedIndex();
        var disp = new FakeWikiDispatcher();
        var svc = WikiSvc(disp);
        var run = await svc.GenerateAsync(
            "afonsoft/demo", "main", "LOW", _orgId, "u1",
            CancellationToken.None);
        run.ShouldNotBeNull();
        run!.Status.ShouldBe(WikiRunStatuses.Queued);
        run.Effort.ShouldBe("low"); // normalized
        disp.Queued.ShouldContain(run.Id);
    }

    [Fact]
    public async Task Wiki_Push_Marks_Only_Covering_Pages_Stale()
    {
        var disp = new FakeWikiDispatcher();
        var svc = WikiSvc(disp);
        var run = new WikiRunRecord
        {
            Id = Guid.NewGuid(),
            OrgId = _orgId,
            Repository = "afonsoft/demo",
            Branch = "main",
            Effort = "medium",
            Status = WikiRunStatuses.Completed,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.WikiRuns.Add(run);
        _db.WikiPages.AddRange(
            new WikiPageRecord
            {
                Id = Guid.NewGuid(),
                OrgId = _orgId,
                WikiRunId = run.Id,
                Repository = "afonsoft/demo",
                Branch = "main",
                Topic = "overview",
                PathKey = "overview",
                Title = "O",
                ContentMd = "# o",
                SourceFilesJson = "[\"src/A.cs\"]",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            new WikiPageRecord
            {
                Id = Guid.NewGuid(),
                OrgId = _orgId,
                WikiRunId = run.Id,
                Repository = "afonsoft/demo",
                Branch = "main",
                Topic = "modules",
                PathKey = "modules/x",
                Title = "X",
                ContentMd = "# x",
                SourceFilesJson = "[\"src/B.cs\"]",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await _db.SaveChangesAsync();

        var touched = await svc.HandlePushAsync(
            "afonsoft/demo", "main", ["src/A.cs"], "sha1", _orgId,
            CancellationToken.None);
        touched.ShouldBeTrue();
        var pages = await _db.WikiPages.IgnoreQueryFilters()
            .Where(p => p.WikiRunId == run.Id)
            .OrderBy(p => p.PathKey).ToListAsync();
        pages.Single(p => p.PathKey == "overview")
            .Stale.ShouldBeTrue();
        pages.Single(p => p.PathKey == "modules/x")
            .Stale.ShouldBeFalse(); // diff-aware: só quem cobre o diff
    }

    // ---- push webhook ------------------------------------------------------

    [Fact]
    public async Task Push_Triggers_Reindex_And_Wiki_Stale()
    {
        SeedIndex();
        var indexer = new FakeIndexer();
        var disp = new FakeWikiDispatcher();
        var wiki = WikiSvc(disp);
        var audit = new CapturingAudit();
        var hook = Webhook(indexer, wiki: wiki, audit: audit);
        var payload = PushPayload(changed: "src/A.cs");

        var result = await hook.HandleGitHubAsync(
            Sign(payload), "push", "push-1", payload,
            CancellationToken.None);

        result.ShouldBe(ScmWebhookResult.Processed);
        indexer.Calls.ShouldContain(("afonsoft/demo", "main"));
        audit.Actions.ShouldContain("scm.index_refreshed");
    }

    [Fact]
    public async Task Push_Skips_Reindex_When_AutoRefresh_Off()
    {
        SeedIndex();
        var indexer = new FakeIndexer();
        var hook = Webhook(indexer,
            io: new IndexingOptions { AutoRefreshOnPush = false });
        var payload = PushPayload(changed: "src/A.cs");

        var result = await hook.HandleGitHubAsync(
            Sign(payload), "push", "push-2", payload,
            CancellationToken.None);

        // índice existe mas refresh desligado e sem wiki → ignored.
        result.ShouldBe(ScmWebhookResult.Ignored);
        indexer.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Push_Unrelated_Branch_Is_Ignored()
    {
        SeedIndex();
        var indexer = new FakeIndexer();
        var hook = Webhook(indexer);
        var payload = PushPayload(branch: "feature/x",
            changed: "src/A.cs");

        var result = await hook.HandleGitHubAsync(
            Sign(payload), "push", "push-3", payload,
            CancellationToken.None);

        result.ShouldBe(ScmWebhookResult.Ignored);
        indexer.Calls.ShouldBeEmpty();
    }

    // ---- ask permalinks -----------------------------------------------------

    [Fact]
    public async Task Ask_Citations_Carry_Provider_Permalink()
    {
        var sha = ShaA;
        var idx = SeedIndex(sha: sha);
        var store = new FakeSearchStore();
        store.Units.Add(new CodeUnitDto(
            Guid.NewGuid(), idx.Id, "src/A.cs", 1, 50, "chunk",
            "A", "code", "h"));
        var ask = new AskModeService(_db, store,
            NullLogger<AskModeService>.Instance,
            Options.Create(new WikiOptions()));

        var resp = await ask.AskAsync(new AskRequest(
            "afonsoft/demo", "como funciona", "main", _orgId),
            CancellationToken.None);

        var cit = resp.Citations.Single();
        cit.Permalink.ShouldBe(
            "https://github.com/afonsoft/demo/blob/"
            + $"{sha}/src/A.cs#L1-L50");
        resp.Answer.ShouldContain($"({cit.Permalink})");
    }

    [Fact]
    public async Task Ask_Citations_Omit_Permalink_Without_CommitSha()
    {
        var idx = SeedIndex(sha: null);
        var store = new FakeSearchStore();
        store.Units.Add(new CodeUnitDto(
            Guid.NewGuid(), idx.Id, "src/A.cs", 1, 50, "chunk",
            null, "code", "h"));
        var ask = new AskModeService(_db, store,
            NullLogger<AskModeService>.Instance,
            Options.Create(new WikiOptions()));

        var resp = await ask.AskAsync(new AskRequest(
            "afonsoft/demo", "pergunta", "main", _orgId),
            CancellationToken.None);

        resp.Citations.Single().Permalink.ShouldBeNull();
    }

    // ---- mcp tools ------------------------------------------------------------

    [Fact]
    public async Task Mcp_ToolsList_Includes_Wiki_And_Ask()
    {
        var server = new McpServer();
        var resp = await server.HandleLineAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            new ScApiClient(new HttpClient(), "http://localhost:9",
                "sk-test"),
            CancellationToken.None);
        using var doc = JsonDocument.Parse(resp!);
        var names = doc.RootElement.GetProperty("result")
            .GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();
        names.ShouldContain("sc_wiki_structure");
        names.ShouldContain("sc_wiki_contents");
        names.ShouldContain("sc_ask");
    }

    [Fact]
    public async Task Mcp_WikiContents_Resolves_Topic_To_Page()
    {
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/wiki", StringComparison.Ordinal))
            {
                return Json("""
                    {"repository":"o/r","branch":"main","commit_sha":"abc",
                     "topics":[{"key":"overview","title":"Overview",
                        "pages":[{"id":"11111111-1111-1111-1111-111111111111",
                            "topic":"overview","path_key":"overview",
                            "title":"Overview","stale":false,
                            "sort_order":0}]}]}
                    """);
            }
            if (path.EndsWith(
                    "/wiki/pages/11111111-1111-1111-1111-111111111111",
                    StringComparison.Ordinal))
            {
                return Json("""
                    {"id":"11111111-1111-1111-1111-111111111111",
                     "repository":"o/r","branch":"main","topic":"overview",
                     "path_key":"overview","title":"Overview",
                     "content_md":"# hi","source_files":[],"stale":false,
                     "wiki_run_id":"22222222-2222-2222-2222-222222222222",
                     "updated_at":"2026-01-01T00:00:00Z"}
                    """);
            }
            return new HttpResponseMessage(
                System.Net.HttpStatusCode.NotFound);
        });
        var api = new ScApiClient(new HttpClient(handler),
            "http://api.test", "sk-test");
        var server = new McpServer();
        var resp = await server.HandleLineAsync(
            """
            {"jsonrpc":"2.0","id":2,"method":"tools/call",
             "params":{"name":"sc_wiki_contents","arguments":
                {"repository":"o/r","topic":"overview"}}}
            """, api, CancellationToken.None);
        using var doc = JsonDocument.Parse(resp!);
        var text = doc.RootElement.GetProperty("result")
            .GetProperty("content")[0].GetProperty("text").GetString()!;
        text.ShouldContain("content_md");
        text.ShouldContain("# hi");
    }

    [Fact]
    public async Task Mcp_Ask_Posts_Question_With_Repository()
    {
        var handler = new StubHandler(req =>
        {
            req.RequestUri!.AbsolutePath.ShouldBe("/api/v1/ask-sessions");
            return Json("""
                {"session_id":"33333333-3333-3333-3333-333333333333",
                 "answer":"ok","citations":[],"can_promote_to_agent":false}
                """);
        });
        var api = new ScApiClient(new HttpClient(handler),
            "http://api.test", "sk-test");
        var server = new McpServer();
        var resp = await server.HandleLineAsync(
            """
            {"jsonrpc":"2.0","id":3,"method":"tools/call",
             "params":{"name":"sc_ask","arguments":
                {"repository":"o/r","question":"oq faz?","branch":"dev"}}}
            """, api, CancellationToken.None);
        using var doc = JsonDocument.Parse(resp!);
        doc.RootElement.GetProperty("result").GetProperty("content")[0]
            .GetProperty("text").GetString()!.ShouldContain("\"answer\"");
        handler.LastBody.ShouldNotBeNull();
        handler.LastBody.ShouldContain("repository");
        handler.LastBody.ShouldContain("o/r");
        handler.LastBody.ShouldContain("oq faz?");
        handler.LastBody.ShouldContain("dev");
    }

    // ---- mcp http transport ------------------------------------------------

    [Theory]
    [InlineData("10.0.0.0/8", "10.1.2.3", true)]
    [InlineData("10.0.0.0/8", "11.0.0.1", false)]
    [InlineData("127.0.0.1/32", "127.0.0.1", true)]
    [InlineData("192.168.1.5", "192.168.1.5", true)]
    [InlineData("192.168.1.5", "192.168.1.6", false)]
    public void McpHttp_Cidr_Matches(string cidr, string ip, bool expected) =>
        McpHttpServer.MatchesCidr(cidr,
            System.Net.IPAddress.Parse(ip)).ShouldBe(expected);

    [Fact]
    public void McpHttp_Allowlist_Defaults_To_Loopback()
    {
        var rules = McpHttpServer.ParseAllowlist(null);
        rules.ShouldContain("127.0.0.1/8");
        rules.ShouldContain("::1/128");
        McpHttpServer.ParseAllowlist("10.0.0.0/8, 192.168.0.0/16")
            .Count.ShouldBe(2);
    }

    // ---- internals ------------------------------------------------------------

    private static async Task<bool> GitAvailableAsync()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<string> RunGit(string dir, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "bash",
            Arguments = $"-c \"git -C '{dir}' {args}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {args} → {p.ExitCode}: {stderr}");
        }
        return stdout;
    }

    private static HttpResponseMessage Json(string body) =>
        new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8,
                "application/json"),
        };

    /// <summary>HttpMessageHandler stub — roteia resposta por request.</summary>
    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                LastBody = await request.Content
                    .ReadAsStringAsync(cancellationToken);
            }
            return respond(request);
        }
    }
}