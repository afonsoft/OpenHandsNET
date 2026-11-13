using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Coordination;
using Sandcastle.Application.Git;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Sandbox;
using Sandcastle.Domain.Conversations;
using Sandcastle.Domain.Identity;
using Sandcastle.Domain.Sandbox;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Sandbox;
using Sandcastle.Sandboxing;
using Sandcastle.UnitTests.Agent;

using Shouldly;

using Xunit;

using DomainSandbox = Sandcastle.Domain.Sandbox.Sandbox;

namespace Sandcastle.UnitTests.Conversations;

/// <summary>
/// #434 — <see cref="ConversationStartWorker"/> orphan reconcile at boot
/// (same pattern as the SPEC-140/141/142/145 run workers): tasks left
/// <c>Working</c> (the queued state) by a dead process re-enqueue; tasks
/// interrupted mid-provisioning fail with <c>worker_restart</c> and get
/// best-effort cleanup of the partial sandbox (<see cref="ISandboxService
/// .DeleteAsync"/>, the same path as conversation delete). The atomic
/// <c>Working→WaitingForSandbox</c> claim keeps a double-enqueued task
/// from provisioning twice.
/// </summary>
public sealed class StartWorkerOrphanReconcileTests : IAsyncDisposable
{
    // Shared-cache in-memory: cada DbContext abre a SUA SqliteConnection
    // (o worker itera em background enquanto o teste recarrega o estado —
    // uma única conexão física quebraria com "active statements").
    private readonly string _cs =
        $"Data Source=startworker-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _keepAlive;
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), $"sc-startworker-{Guid.NewGuid():N}");

    public StartWorkerOrphanReconcileTests()
    {
        _keepAlive = new SqliteConnection(_cs);
        _keepAlive.Open();
        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    private AppDbContext NewDb() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_cs).UseSnakeCaseNamingConvention().Options);

    private (ConversationStartWorker Worker, StartTaskService Tasks)
        BuildWorker(ISandboxService sandboxes, out ServiceProvider sp)
    {
        var sc = new ServiceCollection();
        sc.AddDbContext<AppDbContext>(o => o.UseSqlite(_cs)
            .UseSnakeCaseNamingConvention());
        sc.AddLogging();
        sc.AddSingleton<ISandboxService>(sandboxes);
        sc.AddSingleton(Options.Create(new SandboxOptions
        {
            WorkspaceRoot = _workspace,
        }));
        sc.AddSingleton<WorkspaceProvisioner>();
        sc.AddSingleton<IPlanEnforcementService>(new AllowAllPlan());
        sc.AddSingleton<IGitService>(new NoopGit());
        sc.AddScoped<BlueprintService>();
        sc.AddSingleton<ISandboxExecutor>(new FakeSandboxExecutor());
        sp = sc.BuildServiceProvider();
        var tasks = new StartTaskService(
            sp.GetRequiredService<IServiceScopeFactory>());
        var worker = new ConversationStartWorker(tasks,
            sp.GetRequiredService<IServiceScopeFactory>(),
            new NullDistributedLock(),
            NullLogger<ConversationStartWorker>.Instance);
        return (worker, tasks);
    }

    private async Task<Guid> SeedTaskAsync(
        string status, Guid? sandboxId = null,
        Guid? conversationId = null, bool seedConv = true)
    {
        var convId = conversationId ?? Guid.NewGuid();
        await using var db = NewDb();
        if (seedConv)
        {
            db.Conversations.Add(new ConversationRecord
            {
                Id = convId,
                ExecutionStatus = "Starting",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        var task = new StartTaskRecord
        {
            Id = Guid.NewGuid(),
            Status = status,
            AppConversationId = convId,
            SandboxId = sandboxId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.StartTasks.Add(task);
        await db.SaveChangesAsync();
        return task.Id;
    }

    private async Task<StartTaskRecord> ReloadAsync(Guid id)
    {
        await using var db = NewDb();
        return await db.StartTasks.IgnoreQueryFilters()
            .SingleAsync(t => t.Id == id);
    }

    private async Task<ConversationRecord> ReloadConvAsync(Guid id)
    {
        await using var db = NewDb();
        return await db.Conversations.IgnoreQueryFilters()
            .SingleAsync(c => c.Id == id);
    }

    private static async Task UntilAsync(
        Func<Task<bool>> probe, string what,
        CancellationToken ct, int ms = 10000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(ms);
        while (!await probe())
        {
            (DateTime.UtcNow < deadline).ShouldBeTrue(
                $"timed out waiting for {what}");
            await Task.Delay(50, ct);
        }
    }

    // ---- queued (Working) → requeued + processed --------------------

    [Fact]
    public async Task Orphan_working_task_is_requeued_and_processed()
    {
        // Working == queued: órfã de um processo morto → volta ao canal
        // no boot e é provisionada até READY.
        var taskId = await SeedTaskAsync(
            nameof(StartTaskStatus.Working));
        var sandboxes = new RecordingSandboxService();
        var (worker, _) = BuildWorker(sandboxes, out var sp);
        await using (sp)
        {
            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(20));
            await worker.StartAsync(cts.Token);
            try
            {
                await UntilAsync(async () =>
                        (await ReloadAsync(taskId)).Status
                        == nameof(StartTaskStatus.Ready),
                    "start task to reach Ready", cts.Token);
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }
        }

        var task = await ReloadAsync(taskId);
        task.SandboxId.ShouldBe(sandboxes.StartedId);
        (await ReloadConvAsync(task.AppConversationId!.Value))
            .ExecutionStatus.ShouldBe("Running");
    }

    // ---- interrupted → Error + worker_restart + sandbox cleanup -----

    [Fact]
    public async Task Interrupted_task_fails_and_cleans_partial_sandbox()
    {
        var convId = Guid.NewGuid();
        var orphanSandboxId = Guid.NewGuid();
        var healthySandboxId = Guid.NewGuid();

        // Crash-window fidelity: o record do sandbox ficou bound à
        // conversa mas a task morreu antes de gravar o handle.
        var interruptedId = await SeedTaskAsync(
            nameof(StartTaskStatus.PreparingRepository),
            sandboxId: null, conversationId: convId);
        var healthyTaskId = await SeedTaskAsync(
            nameof(StartTaskStatus.Ready));

        await using (var db = NewDb())
        {
            db.Sandboxes.Add(new SandboxRecord
            {
                Id = orphanSandboxId,
                SpecId = Guid.Empty,
                ConversationId = convId,
                ContainerId = "deadbeef01",
                Status = "Running",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            var healthyConvId = (await ReloadAsync(healthyTaskId))
                .AppConversationId!.Value;
            db.Sandboxes.Add(new SandboxRecord
            {
                Id = healthySandboxId,
                SpecId = Guid.Empty,
                ConversationId = healthyConvId,
                ContainerId = "alive00002",
                Status = "Running",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var sandboxes = new RecordingSandboxService();
        var (worker, _) = BuildWorker(sandboxes, out var sp);
        await using (sp)
        {
            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(20));
            await worker.StartAsync(cts.Token);
            try
            {
                await UntilAsync(async () =>
                        (await ReloadAsync(interruptedId)).Status
                        == nameof(StartTaskStatus.Error),
                    "interrupted task to fail", cts.Token);
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }
        }

        var task = await ReloadAsync(interruptedId);
        task.Detail.ShouldBe("worker_restart");
        (await ReloadConvAsync(convId)).ExecutionStatus.ShouldBe("Error");

        // Sandbox parcial removido via DeleteAsync (mesmo caminho do
        // delete de conversa); o sandbox saudável da outra conversa
        // nunca é tocado.
        sandboxes.Deleted.ShouldBe([orphanSandboxId]);
        (await ReloadAsync(healthyTaskId)).Status
            .ShouldBe(nameof(StartTaskStatus.Ready));
    }

    [Fact]
    public async Task Cleanup_failure_still_fails_task_and_audits()
    {
        var convId = Guid.NewGuid();
        var taskId = await SeedTaskAsync(
            nameof(StartTaskStatus.WaitingForSandbox),
            conversationId: convId);
        var sandboxId = Guid.NewGuid();
        await using (var db = NewDb())
        {
            db.Sandboxes.Add(new SandboxRecord
            {
                Id = sandboxId,
                SpecId = Guid.Empty,
                ConversationId = convId,
                ContainerId = "stuck00003",
                Status = "Starting",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var audit = new CapturingAudit();
        var sandboxes = new RecordingSandboxService
        {
            DeleteError = new InvalidOperationException("docker daemon gone"),
        };
        var sc = new ServiceCollection();
        sc.AddDbContext<AppDbContext>(o => o.UseSqlite(_cs)
            .UseSnakeCaseNamingConvention());
        sc.AddLogging();
        sc.AddSingleton<ISandboxService>(sandboxes);
        sc.AddSingleton<IAuditService>(audit);
        await using var sp = sc.BuildServiceProvider();
        var tasks = new StartTaskService(
            sp.GetRequiredService<IServiceScopeFactory>());
        var worker = new ConversationStartWorker(tasks,
            sp.GetRequiredService<IServiceScopeFactory>(),
            new NullDistributedLock(),
            NullLogger<ConversationStartWorker>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await worker.StartAsync(cts.Token);
        try
        {
            await UntilAsync(async () =>
                    (await ReloadAsync(taskId)).Status
                    == nameof(StartTaskStatus.Error),
                "task to fail despite cleanup error", cts.Token);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        (await ReloadAsync(taskId)).Detail.ShouldBe("worker_restart");
        audit.Actions.ShouldContain("starttask.orphan_cleanup_failed");
    }

    // ---- atomic claim vs double-enqueue ------------------------------

    [Fact]
    public async Task Atomic_claim_prevents_double_provisioning()
    {
        // A mesma task no canal duas vezes (requeue do reconcile + push
        // prévio) — só o primeiro claim Working→WaitingForSandbox
        // provisiona; o segundo sai no ExecuteUpdate.
        var taskId = await SeedTaskAsync(
            nameof(StartTaskStatus.Working));
        var sandboxes = new RecordingSandboxService();
        var (worker, tasks) = BuildWorker(sandboxes, out var sp);
        await using (sp)
        {
            // Simula a entrada que já estava no canal quando o processo
            // morreu — o reconcile de boot re-enfileira a MESMA task.
            await tasks.EnqueueAsync(taskId, CancellationToken.None);
            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(20));
            await worker.StartAsync(cts.Token);
            try
            {
                await UntilAsync(async () =>
                        (await ReloadAsync(taskId)).Status
                        == nameof(StartTaskStatus.Ready),
                    "start task to reach Ready", cts.Token);
                // Drena a 2ª entrada do canal — claim falha (status já
                // saiu de Working) e StartAsync não roda de novo.
                await Task.Delay(300, cts.Token);
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }
        }

        sandboxes.StartCalls.ShouldBe(1);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_workspace))
            {
                Directory.Delete(_workspace, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        await _keepAlive.DisposeAsync();
    }

    // ---- fakes --------------------------------------------------------

    private sealed class RecordingSandboxService : ISandboxService
    {
        public int StartCalls;
        public Guid StartedId;
        public List<Guid> Deleted { get; } = [];
        public Exception? DeleteError;

        private static DomainSandbox Make(Guid id) => new(id, Guid.Empty,
            null, "fake", SandboxStatus.Running, null,
            DateTimeOffset.UtcNow, null, null);

        public Task<DomainSandbox> StartAsync(
            Guid specId, Guid? conversationId, CancellationToken ct)
        {
            StartCalls++;
            StartedId = Guid.NewGuid();
            return Task.FromResult(Make(StartedId));
        }

        public Task<DomainSandbox> PauseAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(Make(id));
        public Task<DomainSandbox> StopAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(Make(id));
        public Task<DomainSandbox> ResumeAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(Make(id));

        public Task DeleteAsync(Guid id, CancellationToken ct)
        {
            Deleted.Add(id);
            return DeleteError is not null
                ? Task.FromException(DeleteError)
                : Task.CompletedTask;
        }

        public Task<DomainSandbox?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<DomainSandbox?>(Make(id));

        public Task<IReadOnlyList<DomainSandbox>> SearchAsync(
            SandboxStatus? status, Guid? conversationId,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DomainSandbox>>([]);

        public Task<SandboxSpec> UpsertSpecAsync(
            SandboxSpec spec, CancellationToken ct) =>
            Task.FromResult(spec);

        public Task<SandboxSpec?> GetSpecAsync(
            Guid specId, CancellationToken ct) =>
            Task.FromResult<SandboxSpec?>(null);

        public Task SweepOrphansAsync(CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class AllowAllPlan : IPlanEnforcementService
    {
        public Task<EffectivePlan> GetEffectivePlanAsync(
            Guid? orgId, CancellationToken cancellationToken) =>
            Task.FromResult(new EffectivePlan(null, "free",
                PlanLimits.Empty));

        public Task<EffectivePlan> GetEffectiveLimitsAsync(
            Guid? orgId, Guid? userId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EffectivePlan(null, "free",
                PlanLimits.Empty));

        public Task<PlanLimitError?> CheckConversationCreateAsync(
            Guid? orgId, Guid? userId,
            CancellationToken cancellationToken) =>
            Task.FromResult<PlanLimitError?>(null);

        public Task<PlanLimitError?> CheckRunStartAsync(
            Guid? orgId, Guid? userId,
            CancellationToken cancellationToken) =>
            Task.FromResult<PlanLimitError?>(null);
    }

    private sealed class NoopGit : IGitService
    {
        public Task<bool> HasRepositoryAsync(
            DomainSandbox sandbox, CancellationToken ct,
            string? repoPath = null) => Task.FromResult(false);

        public Task CloneAsync(
            DomainSandbox sandbox, string repoUrl, string workBranch,
            string? ownerId, CancellationToken ct,
            string? repoPath = null, string? credentialToken = null) =>
            Task.CompletedTask;

        public Task<bool> ProvisionGhAuthAsync(
            DomainSandbox sandbox, string? ownerId,
            CancellationToken ct) => Task.FromResult(false);

        public Task<bool> ProvisionTokenAuthAsync(
            DomainSandbox sandbox, string host, string token,
            CancellationToken ct) => Task.FromResult(false);

        public Task<GitChanges> GetChangesAsync(
            DomainSandbox sandbox, CancellationToken ct,
            string? repoPath = null) =>
            throw new NotImplementedException();

        public Task<GitDiffReport?> GetDiffAsync(
            DomainSandbox sandbox, string? path, CancellationToken ct,
            string? repoPath = null) =>
            Task.FromResult<GitDiffReport?>(null);

        public Task<string?> GetRawDiffAsync(
            DomainSandbox sandbox, string? path, CancellationToken ct,
            string? repoPath = null) => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<GitLogEntry>> GetLogAsync(
            DomainSandbox sandbox, int limit, CancellationToken ct,
            string? repoPath = null) =>
            Task.FromResult<IReadOnlyList<GitLogEntry>>([]);

        public Task<string?> GetFileAtRefAsync(
            DomainSandbox sandbox, string path, string gitRef,
            CancellationToken ct, string? repoPath = null) =>
            Task.FromResult<string?>(null);

        public Task<GitCommitResult> CommitAsync(
            DomainSandbox sandbox, string message,
            IReadOnlyList<string>? files, CancellationToken ct,
            string? repoPath = null) =>
            throw new NotImplementedException();

        public Task<GitPushResult> PushAsync(
            DomainSandbox sandbox, string head, string? remoteBranch,
            bool forceWithLease, string? ownerId, CancellationToken ct,
            string? repoPath = null) =>
            throw new NotImplementedException();

        public Task<string?> GetCurrentBranchAsync(
            DomainSandbox sandbox, CancellationToken ct,
            string? repoPath = null) => Task.FromResult<string?>(null);
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
}