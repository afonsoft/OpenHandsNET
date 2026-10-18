using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Domain.Sandbox;
using OpenHandsNET.Infrastructure.Agent;
using OpenHandsNET.Infrastructure.Conversations;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>In-memory IEventService: append + ordered search + live channel.</summary>
internal sealed class FakeEventService : IEventService
{
    private readonly List<EventBase> _events = [];
    private readonly Channel<EventBase> _live = Channel.CreateUnbounded<EventBase>();

    public IReadOnlyList<EventBase> Events => _events;

    public ValueTask<EventBase> AppendAsync(
        Guid conversationId, EventBase e, CancellationToken ct)
    {
        _events.Add(e);
        _live.Writer.TryWrite(e);
        return ValueTask.FromResult(e);
    }

    public Task<EventPage> SearchAsync(
        Guid conversationId, EventQuery q, CancellationToken ct)
    {
        IEnumerable<EventBase> filtered = _events;
        if (q.KindEq is not null)
        {
            filtered = filtered.Where(e => e.EventKind == q.KindEq);
        }

        var list = (q.Sort == EventSortOrder.TimestampDesc
            ? filtered.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Id)
            : filtered.OrderBy(e => e.Timestamp).ThenBy(e => e.Id)).ToList();
        var start = int.TryParse(q.PageId, out var idx) ? idx : 0;
        var page = list.Skip(start).Take(q.EffectiveLimit).ToList();
        return Task.FromResult(new EventPage(page,
            start + page.Count < list.Count ? (start + page.Count).ToString() : null));
    }

    public Task<long> CountAsync(Guid conversationId, EventQuery q, CancellationToken ct) =>
        Task.FromResult((long)_events.Count);

    public Task<IReadOnlyList<EventBase>> GetBatchAsync(
        Guid conversationId, IReadOnlyCollection<string> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EventBase>>(
            _events.Where(e => ids.Contains(e.Id)).ToList());

    public async IAsyncEnumerable<EventBase> SubscribeAsync(
        Guid conversationId, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var e in _live.Reader.ReadAllAsync(ct))
        {
            yield return e;
        }
    }
}

/// <summary>In-memory executor: scriptable command results + file ops.</summary>
internal sealed class FakeSandboxExecutor : ISandboxExecutor
{
    private readonly Dictionary<string, string> _files = new();
    public List<string> Commands { get; } = [];
    public Func<string, CommandResult>? ExecHandler { get; set; }
    public Func<string, IReadOnlyList<FileEntry>>? ListHandler { get; set; }

    public Task<IBashSession> OpenBashSessionAsync(Sandbox sandbox, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<CommandResult> ExecAsync(
        Sandbox sandbox, string command, TimeSpan timeout, CancellationToken ct)
    {
        Commands.Add(command);
        // Simula o mínimo do shell no mapa de arquivos (SPEC-30 memory forget).
        var trimmed = command.Trim();
        if (trimmed.StartsWith("rm ", StringComparison.Ordinal)
            || trimmed.StartsWith("rm -", StringComparison.Ordinal))
        {
            foreach (var part in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Skip(1).Where(p => !p.StartsWith('-')))
            {
                _files.Remove(part);
            }
        }

        return Task.FromResult(ExecHandler?.Invoke(command)
            ?? new CommandResult(0, "ok\n", "", false));
    }

    public Task<string> ReadFileAsync(Sandbox sandbox, string path, CancellationToken ct) =>
        Task.FromResult(_files.TryGetValue(path, out var c)
            ? c : throw new FileNotFoundException(path));

    public Task WriteFileAsync(
        Sandbox sandbox, string path, string content, CancellationToken ct)
    {
        _files[path] = content;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FileEntry>> ListFilesAsync(
        Sandbox sandbox, string path, CancellationToken ct) =>
        Task.FromResult(ListHandler?.Invoke(path)
            ?? (IReadOnlyList<FileEntry>)_files
                .Select(kv => new FileEntry(kv.Key, false, kv.Value.Length))
                .ToList());

    /// <summary>Seeds the in-memory file map (test helper).</summary>
    public void SeedFile(string path, string content) => _files[path] = content;

    public Task<GitDiff> GetGitDiffAsync(Sandbox sandbox, CancellationToken ct) =>
        Task.FromResult(new GitDiff([], "", 0, 0));
}

/// <summary>Always returns a RUNNING sandbox.</summary>
internal sealed class FakeSandboxService(Guid sandboxId) : ISandboxService
{
    private static Sandbox Make(Guid id) => new(id, Guid.Empty, null, "fake",
        SandboxStatus.Running, null, DateTimeOffset.UtcNow, null, null);

    public Task<Sandbox> StartAsync(Guid specId, Guid? conversationId, CancellationToken ct) =>
        Task.FromResult(Make(sandboxId));

    public Task<Sandbox> PauseAsync(Guid id, CancellationToken ct) => Task.FromResult(Make(id));
    public Task<Sandbox> StopAsync(Guid id, CancellationToken ct) => Task.FromResult(Make(id));
    public Task<Sandbox> ResumeAsync(Guid id, CancellationToken ct) => Task.FromResult(Make(id));
    public Task DeleteAsync(Guid id, CancellationToken ct) => Task.CompletedTask;

    public Task<Sandbox?> GetAsync(Guid id, CancellationToken ct) =>
        Task.FromResult<Sandbox?>(id == sandboxId ? Make(id) : null);

    public Task<IReadOnlyList<Sandbox>> SearchAsync(
        SandboxStatus? status, Guid? conversationId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Sandbox>>([Make(sandboxId)]);

    public Task<SandboxSpec> UpsertSpecAsync(SandboxSpec spec, CancellationToken ct) =>
        Task.FromResult(spec);

    public Task<SandboxSpec?> GetSpecAsync(Guid specId, CancellationToken ct) =>
        Task.FromResult<SandboxSpec?>(null);

    public Task SweepOrphansAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Runner fixture: DI scope com fakes + Sqlite :memory:.</summary>
internal sealed class AgentRunnerFixture : IAsyncDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _scope;

    public AgentRunnerFixture(
        string? mockScript = null, AgentLlmOptions? options = null,
        bool confirmationMode = false, ICondenser? condenser = null,
        IAgentLlmProvider? provider = null, IModelCatalog? catalog = null,
        ISecurityAnalyzer? security = null,
        Action<ConversationRecord>? configureConv = null,
        IAgentActivityBus? activityBus = null,
        OpenHandsNET.Application.Conversations.IAttentionBus?
            attentionBus = null,
        ISecurityPolicyService? securityPolicy = null,
        IToolRegistry? toolRegistry = null,
        IByorKeyService? byor = null, LlmProxyOptions? proxyOptions = null)
    {
        ConversationId = Guid.NewGuid();
        Events = new FakeEventService();
        Executor = new FakeSandboxExecutor();
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();

        var opt = options ?? new AgentLlmOptions();
        var sc = new ServiceCollection();
        sc.AddDbContext<AppDbContext>(o => o.UseSqlite(_conn)
            .UseSnakeCaseNamingConvention());
        sc.AddSingleton<IEventService>(Events);
        sc.AddSingleton<ISandboxExecutor>(Executor);
        sc.AddSingleton<ISandboxService>(new FakeSandboxService(SandboxId));
        sc.AddSingleton<ITaskTrackerService, InMemoryTaskTrackerService>();
        if (toolRegistry is not null)
        {
            sc.AddSingleton<IToolRegistry>(toolRegistry);
        }
        else
        {
            sc.AddSingleton<IToolRegistry, ToolRegistry>();
        }
        if (securityPolicy is not null)
        {
            sc.AddSingleton<ISecurityPolicyService>(securityPolicy);
        }
        if (byor is not null)
        {
            sc.AddSingleton(byor);
            sc.AddSingleton(Options.Create(
                proxyOptions ?? new LlmProxyOptions()));
        }
        sc.AddSingleton<ISubagentHost,
            OpenHandsNET.Infrastructure.Agent.Subagents.SubagentHost>();
        sc.AddSingleton(condenser ?? (ICondenser)new NoOpCondenser());
        sc.AddSingleton<IContextMeter>(
            new OpenHandsNET.Infrastructure.Agent.Context
                .HeuristicContextMeter(Options.Create(opt)));
        sc.AddSingleton<OpenHandsNET.Infrastructure.Agent.Context
            .ConversationHistoryBuilder>();
        sc.AddSingleton(Options.Create(opt));
        sc.AddSingleton(provider ?? (IAgentLlmProvider)
            new MockLlmProvider(Parse(mockScript)));
        if (catalog is not null)
        {
            sc.AddSingleton<IModelCatalog>(catalog);
            sc.AddSingleton<IModelRouter>(
                new OpenHandsNET.Infrastructure.Agent.Routing
                    .ModelRouter(catalog, Options.Create(opt)));
        }
        if (security is not null)
        {
            sc.AddSingleton(security);
        }
        if (activityBus is not null)
        {
            sc.AddSingleton(activityBus);
        }
        if (attentionBus is not null)
        {
            sc.AddSingleton(attentionBus);
        }
        _provider = sc.BuildServiceProvider();
        _scope = _provider.CreateAsyncScope();

        var db = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
        var record = new ConversationRecord
        {
            Id = ConversationId, SandboxId = SandboxId,
            ExecutionStatus = "Running", ConfirmationMode = confirmationMode,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        configureConv?.Invoke(record);
        db.Conversations.Add(record);
        db.SaveChanges();

        Runner = new AgentRunner(ConversationId, _scope.ServiceProvider,
            NullLogger<AgentRunner>.Instance);
    }

    public Guid ConversationId { get; }
    public Guid SandboxId { get; } = Guid.NewGuid();
    public FakeEventService Events { get; }
    public FakeSandboxExecutor Executor { get; }
    public AgentRunner Runner { get; }
    public IServiceProvider ScopeServices => _scope.ServiceProvider;

    public async Task<string?> RecordStatusAsync()
    {
        var db = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.Conversations.FindAsync(ConversationId))?.ExecutionStatus;
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _provider.DisposeAsync();
        await _conn.DisposeAsync();
    }

    private static IEnumerable<System.Text.Json.Nodes.JsonNode> Parse(string? script) =>
        string.IsNullOrEmpty(script)
            ? []
            : System.Text.Json.Nodes.JsonNode.Parse(script)!.AsArray()
                .Select(n => n!).ToList();
}
