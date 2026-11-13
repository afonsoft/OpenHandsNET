using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Conversations;
using Sandcastle.Application.Eval;
using Sandcastle.Application.Sandbox;
using Sandcastle.Domain.Conversations;
using Sandcastle.Domain.Events;
using Sandcastle.Domain.Sandbox;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Eval;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.UnitTests.Agent;

using Shouldly;

namespace Sandcastle.UnitTests.Eval;

/// <summary>
/// SPEC-152 RF-002 — EvalRunner sobre Sqlite :memory: + fakes:
/// conversa eval criada pela pipeline normal
/// (<see cref="FakeConversationService"/> sementeia start-task READY +
/// conversa Finished), sandbox/checker via
/// <see cref="FakeSandboxExecutor"/> — cobre os quatro verdicts,
/// propagação do eval_mode (agent_type=eval + model pin), agregação
/// de tokens/cost (TokenEvent), log digest e cancelamento da run.
/// </summary>
public sealed class Spec152EvalRunnerTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly AppDbContext _db;
    private readonly Guid _sandboxId = Guid.NewGuid();
    private readonly FakeSandboxService _sandboxes;
    private readonly FakeSandboxExecutor _executor = new();
    private readonly FakeEventService _events = new();
    private readonly FakeConversationService _conversations;
    private readonly EvalOptions _opt = new()
    {
        MaxConcurrency = 2,
        PollIntervalSec = 1,
        TaskTimeoutSec = 30,
        CheckerTimeoutSec = 60,
        ProvisionTimeoutSec = 30,
        SetupCmdTimeoutSec = 10,
    };

    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly FakeDbFactory _dbFactory;

    public Spec152EvalRunnerTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_conn).UseSnakeCaseNamingConvention().Options;
        _db = new AppDbContext(_dbOptions);
        _db.Database.EnsureCreated();
        _dbFactory = new FakeDbFactory(_dbOptions);
        _sandboxes = new FakeSandboxService(_sandboxId);
        _conversations = new FakeConversationService(_db, _sandboxId, _events);
    }

    /// <summary>IDbContextFactory sobre a conexão :memory: compartilhada.</summary>
    private sealed class FakeDbFactory(
        DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(
            CancellationToken ct = default) =>
            Task.FromResult(new AppDbContext(options));
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    /// <summary>
    /// Fake da pipeline normal: grava start-task READY + conversa com
    /// o ExecutionStatus desejado (o runner só poll status — mock do
    /// agente fica fora do escopo, AgentRunner é testado à parte).
    /// </summary>
    private sealed class FakeConversationService(
        AppDbContext db, Guid sandboxId,
        FakeEventService? events = null) : IAppConversationService
    {
        // Tasks paralelas compartilham o db do teste — lock nas writes.
        private readonly SemaphoreSlim _lock = new(1, 1);
        public List<CreateConversationRequest> Requests { get; } = [];

        /// <summary>
        /// TokenEvents emitidos por conversa criada — determinístico
        /// (o runner coleta uso logo após o status terminal).
        /// </summary>
        public List<TokenEvent> TokenEventsToEmit { get; } = [];

        /// <summary>Status terminal que a conversa simula (default Finished).</summary>
        public string TerminalStatus { get; set; } =
            ConversationExecutionStatus.Finished.ToString();

        /// <summary>Delay artificial por task (timeout test).</summary>
        public TimeSpan CreateDelay { get; set; } = TimeSpan.Zero;

        public async Task<AppConversationStartTask> CreateForUserAsync(
            Guid userId, Guid? orgId, CreateConversationRequest request,
            CancellationToken ct)
        {
            Requests.Add(request);
            if (CreateDelay > TimeSpan.Zero)
            {
                await Task.Delay(CreateDelay, ct);
            }

            var convId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await _lock.WaitAsync(ct);
            try
            {
                db.Conversations.Add(new ConversationRecord
                {
                    Id = convId,
                    OrgId = orgId,
                    CreatedByUserId = userId.ToString(),
                    AgentType = request.AgentType,
                    LlmModel = request.LlmModel,
                    SelectedRepository = request.SelectedRepository,
                    SandboxId = sandboxId,
                    ExecutionStatus = TerminalStatus,
                    ConfirmationMode = request.ConfirmationMode,
                    Mode = request.Mode ?? "build",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                db.StartTasks.Add(new StartTaskRecord
                {
                    Id = taskId,
                    Status = StartTaskStatus.Ready.ToString(),
                    AppConversationId = convId,
                    SandboxId = sandboxId,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                await db.SaveChangesAsync(ct);
            }
            finally
            {
                _lock.Release();
            }

            if (events is not null)
            {
                foreach (var te in TokenEventsToEmit)
                {
                    await events.AppendAsync(convId, te, ct);
                }
            }

            return new AppConversationStartTask(taskId,
                StartTaskStatus.Ready, convId, sandboxId, null,
                now, now);
        }

        // Membros fora do caminho do runner — não implementados.
        public Task<AppConversationStartTask> CreateAsync(
            CreateConversationRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ConversationPage> SearchAsync(
            ConversationQuery query, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<long> CountAsync(
            ConversationQuery query, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<AppConversation>> GetBatchAsync(
            IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<AppConversation?> GetAsync(
            Guid id, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<AppConversation?> UpdateAsync(
            Guid id, string? title, IReadOnlyList<string>? tags,
            bool? pinned, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<SendMessageError?> ValidateSendMessageAsync(
            Guid id, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<SwitchResult> SwitchProfileAsync(
            Guid id, string? profile, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<SwitchResult> SwitchModelAsync(
            Guid id, string model, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<AppConversation?> SetPermissionsAsync(Guid id,
            IReadOnlyList<AgentPermissionRule>? rules,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<AppConversation?> SetModeAsync(
            Guid id, string mode, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<AttentionItem>> ListAttentionAsync(
            int limit, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<AttentionDismissResult> DismissAttentionAsync(
            Guid id, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeAdapter(
        IReadOnlyList<EvalTaskV1> tasks,
        TaskCompletionSource? gate = null) : IEvalSuiteAdapter
    {
        public string Suite => "fake-suite";
        public async Task<IReadOnlyList<EvalTaskV1>> LoadAsync(
            EvalSuiteLoadContext ctx, CancellationToken ct)
        {
            if (gate is not null)
            {
                await gate.Task;
            }

            return tasks;
        }
    }

    private EvalRunner RunnerFor(IReadOnlyList<EvalTaskV1> tasks,
        TaskCompletionSource? gate = null) =>
        new(_dbFactory, _conversations, _sandboxes, _executor, _events,
            new EvalSuiteResolver([new FakeAdapter(tasks, gate)]),
            Options.Create(_opt), NullLogger<EvalRunner>.Instance);

    private static EvalTaskV1 MakeTask(string id,
        IReadOnlyList<string>? setup = null,
        string checkerCmd = "run-check", int? timeout = null,
        string? image = "img:1", string? repo = null) =>
        new()
        {
            TaskId = id,
            Image = image,
            Repo = repo,
            SetupCmds = setup ?? [],
            Prompt = "fix it",
            Checker = new EvalTaskChecker { Cmd = checkerCmd },
            TimeoutS = timeout,
        };

    private async Task<EvalRunRecord> SeedRunAsync(
        int n = 3, string status = "queued")
    {
        var run = new EvalRunRecord
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            CreatedByUserId = Guid.NewGuid().ToString(),
            Suite = "fake-suite",
            NTasks = n,
            Model = "m-eval",
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.EvalRuns.Add(run);
        await _db.SaveChangesAsync();
        return run;
    }

    /// <summary>
    /// Recarrega a run — o seed fica tracked no <c>_db</c> do teste e
    /// o runner escreve via <c>IDbContextFactory</c> (outra ctx), então
    /// <c>FindAsync</c> devolveria a cópia stale.
    /// </summary>
    private async Task<EvalRunRecord> ReloadedAsync(EvalRunRecord run)
    {
        await _db.Entry(run).ReloadAsync();
        return run;
    }

    private void CheckerExits(int code) =>
        _executor.ExecHandler = c =>
            c.Contains("run-check")
                ? new CommandResult(code, "out", "", false)
                : new CommandResult(0, "ok", "", false);

    [Fact]
    public async Task Run_Three_Tasks_All_Resolved()
    {
        CheckerExits(0);
        var run = await SeedRunAsync(3);
        await RunnerFor([MakeTask("a"), MakeTask("b"), MakeTask("c")])
            .RunAsync(run.Id, CancellationToken.None);

        var done = await ReloadedAsync(run);
        done!.Status.ShouldBe("completed");
        done.ResolvedN.ShouldBe(3);
        done.ScorePct.ShouldBe(100.0);
        done.StatsJson.ShouldNotBeNull();
        done.FinishedAt.ShouldNotBeNull();
        var results = await _db.EvalResults
            .Where(r => r.RunId == run.Id).ToListAsync();
        results.ShouldAllBe(r => r.Verdict == "resolved"
            && r.SessionId != null
            && r.LogDigest != null
            && r.DurationS != null);
    }

    [Fact]
    public async Task Checker_NonZero_Marks_Failed()
    {
        CheckerExits(1);
        var run = await SeedRunAsync(1);
        await RunnerFor([MakeTask("x")])
            .RunAsync(run.Id, CancellationToken.None);

        var result = await _db.EvalResults.SingleAsync(
            r => r.RunId == run.Id);
        result.Verdict.ShouldBe("failed");
        result.Error!.ShouldContain("checker exit 1");
        (await ReloadedAsync(run)).FailedN.ShouldBe(1);
    }

    [Fact]
    public async Task Setup_Failure_Marks_Error_Not_Failed()
    {
        _executor.ExecHandler = c =>
            c.Contains("bad-setup")
                ? new CommandResult(2, "", "boom", false)
                : new CommandResult(0, "ok", "", false);
        var run = await SeedRunAsync(1);
        await RunnerFor([MakeTask("s", setup: ["bad-setup --now"])])
            .RunAsync(run.Id, CancellationToken.None);

        var result = await _db.EvalResults.SingleAsync(
            r => r.RunId == run.Id);
        result.Verdict.ShouldBe("error");
        result.Error!.ShouldContain("setup failed");
        (await ReloadedAsync(run)).ErrorN.ShouldBe(1);
    }

    [Fact]
    public async Task Agent_Error_Status_Marks_Error()
    {
        CheckerExits(0);
        _conversations.TerminalStatus =
            ConversationExecutionStatus.Error.ToString();
        var run = await SeedRunAsync(1);
        await RunnerFor([MakeTask("e")])
            .RunAsync(run.Id, CancellationToken.None);

        var result = await _db.EvalResults.SingleAsync(
            r => r.RunId == run.Id);
        result.Verdict.ShouldBe("error");
        result.Error!.ShouldContain("status Error");
    }

    [Fact]
    public async Task Task_Deadline_Marks_Timeout()
    {
        // Conversa nunca termina (Running eterno) + timeout_s=1 →
        // deadline da task estoura → verdict timeout.
        CheckerExits(0);
        _conversations.TerminalStatus =
            ConversationExecutionStatus.Running.ToString();
        var run = await SeedRunAsync(1);
        await RunnerFor([MakeTask("t", timeout: 1)])
            .RunAsync(run.Id, CancellationToken.None);

        var result = await _db.EvalResults.SingleAsync(
            r => r.RunId == run.Id);
        result.Verdict.ShouldBe("timeout");
        (await ReloadedAsync(run)).TimeoutN.ShouldBe(1);
    }

    [Fact]
    public async Task EvalMode_Propagates_To_Conversation_Request()
    {
        CheckerExits(0);
        var run = await SeedRunAsync(2);
        await RunnerFor([
            MakeTask("img-task", image: "ghcr.io/acme/img:2"),
            MakeTask("repo-task", image: null, repo: "org/repo"),
        ]).RunAsync(run.Id, CancellationToken.None);

        var reqs = _conversations.Requests;
        reqs.Count.ShouldBe(2);
        reqs.ShouldAllBe(r =>
            r.AgentType == EvalAgentTypes.Eval // eval_mode flag
            && r.LlmModel == "m-eval"          // model pin (SPEC-149)
            && r.ConfirmationMode == false
            && r.Mode == "build");
        var imgReq = reqs.First(r => r.SelectedRepository is null);
        imgReq.SandboxSpecId.ShouldNotBeNull(); // imagem custom → spec
        reqs.First(r => r.SelectedRepository == "org/repo")
            .SandboxSpecId.ShouldBeNull();
    }

    [Fact]
    public async Task TokenEvents_Aggregate_Into_Result()
    {
        CheckerExits(0);
        // TokenEvents emitidos na criação da conversa (determinístico).
        _conversations.TokenEventsToEmit.AddRange([
            new TokenEvent
            {
                PromptTokens = 100, CompletionTokens = 40,
                CostUsd = 0.01m,
            },
            new TokenEvent
            {
                PromptTokens = 200, CompletionTokens = 60,
                CostUsd = 0.02m,
            },
        ]);
        var run = await SeedRunAsync(1);
        await RunnerFor([MakeTask("u")])
            .RunAsync(run.Id, CancellationToken.None);

        var result = await _db.EvalResults.SingleAsync(
            r => r.RunId == run.Id);
        result.Tokens.ShouldBe(400);
        result.PromptTokens.ShouldBe(300);
        result.CompletionTokens.ShouldBe(100);
        result.CostUsd.ShouldBe(0.03m);
        (await ReloadedAsync(run))
            .ActualCostUsd.ShouldBe(0.03m);
    }

    [Fact]
    public async Task Cancelled_Run_Marks_Pending_Results_Error()
    {
        CheckerExits(0);
        var run = await SeedRunAsync(2);
        var gate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = RunnerFor(
            [MakeTask("p1"), MakeTask("p2")], gate);
        var runTask = runner.RunAsync(run.Id, CancellationToken.None);
        // Cancela enquanto o adapter ainda está bloqueado.
        var tracked = await _db.EvalRuns.FindAsync(run.Id);
        tracked!.Status = "cancelled";
        await _db.SaveChangesAsync();
        gate.SetResult();
        await runTask;

        var results = await _db.EvalResults
            .Where(r => r.RunId == run.Id).ToListAsync();
        results.ShouldAllBe(r =>
            r.Verdict == "error" && r.Error == "run cancelled");
        (await ReloadedAsync(run)).Status
            .ShouldBe("cancelled");
    }

    [Fact]
    public async Task RunAsync_On_Terminal_Run_Is_Noop()
    {
        var run = await SeedRunAsync(1, status: "completed");
        await RunnerFor([MakeTask("z")])
            .RunAsync(run.Id, CancellationToken.None);
        (await _db.EvalResults.CountAsync(r => r.RunId == run.Id))
            .ShouldBe(0);
    }

    [Fact]
    public async Task Suite_Load_Failure_Fails_Run()
    {
        var run = await SeedRunAsync(1);
        var runner = new EvalRunner(_dbFactory, _conversations,
            _sandboxes, _executor, _events,
            new EvalSuiteResolver([]), // suite desconhecida → throw
            Options.Create(_opt), NullLogger<EvalRunner>.Instance);
        await runner.RunAsync(run.Id, CancellationToken.None);
        var done = await ReloadedAsync(run);
        done!.Status.ShouldBe("failed");
        done.Error!.ShouldContain("unknown suite");
    }
}