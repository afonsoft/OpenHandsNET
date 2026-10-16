using Docker.DotNet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Conversations;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Domain.Sandbox;
using OpenHandsNET.Infrastructure.Conversations;
using OpenHandsNET.Infrastructure.Persistence;
using OpenHandsNET.Infrastructure.Sandbox;
using OpenHandsNET.Sandboxing;

namespace OpenHandsNET.IntegrationTests;

/// <summary>
/// SPEC-16 lifecycle tests: sleep scheduling, stop-reaper, wake-on-message e
/// workspace GC contra o daemon Docker real. Skip silencioso sem daemon.
/// </summary>
public sealed class Spec16LifecycleTests : IAsyncLifetime
{
    private const string Image = "bash:5";
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-s16-{Guid.NewGuid():N}.db");
    private readonly string _wsRoot = Path.Combine(
        Path.GetTempPath(), $"ohn-s16-ws-{Guid.NewGuid():N}");
    private readonly string _owner = $"test-{Guid.NewGuid():N}";
    private AppDbContext _db = null!;
    private IDockerClient _docker = null!;
    private SandboxService _service = null!;
    private DockerSandboxExecutor _executor = null!;
    private WorkspaceProvisioner _provisioner = null!;
    private bool _dockerUp;

    public async Task InitializeAsync()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .UseSnakeCaseNamingConvention().Options);
        await _db.Database.MigrateAsync();
        try
        {
            _docker = DockerClientFactory.Create();
            await _docker.System.PingAsync();
            _dockerUp = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DOCKER UNAVAILABLE: {ex.GetBaseException().Message}");
            _dockerUp = false;
            return;
        }

        var opts = Options.Create(new SandboxOptions
        {
            WorkspaceRoot = _wsRoot, InstanceOwner = _owner,
        });
        _provisioner = new WorkspaceProvisioner(
            opts, NullLogger<WorkspaceProvisioner>.Instance);
        var egress = new DockerNetworkPolicy(_docker, opts,
            NullLogger<DockerNetworkPolicy>.Instance);
        var engine = new DockerSandboxEngine(_docker, _provisioner, egress,
            opts, NullLogger<DockerSandboxEngine>.Instance);
        _service = new SandboxService(
            _db, engine, opts, NullLogger<SandboxService>.Instance);
        _executor = new DockerSandboxExecutor(_docker);
    }

    public async Task DisposeAsync()
    {
        _db?.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (Directory.Exists(_wsRoot)) Directory.Delete(_wsRoot, recursive: true);
        await Task.CompletedTask;
    }

    private async Task<Domain.Sandbox.Sandbox> StartAsync(Guid conversationId)
    {
        var spec = new SandboxSpec(Guid.NewGuid(), "test", Image,
            MemoryMb: 128, CpuLimit: 0.5, TimeoutMinutes: 30);
        await _service.UpsertSpecAsync(spec, CancellationToken.None);
        return await _service.StartAsync(
            spec.Id, conversationId, CancellationToken.None);
    }

    private async Task<ConversationRecord> SeedConversationAsync(
        Guid sandboxId, string executionStatus = "Finished")
    {
        var conv = new ConversationRecord
        {
            Id = Guid.NewGuid(), SandboxId = sandboxId,
            ExecutionStatus = executionStatus,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db.Conversations.Add(conv);
        await _db.SaveChangesAsync();
        return conv;
    }

    [Fact]
    public async Task Stop_Resume_Preserves_ContainerFs_And_Workspace()
    {
        if (!_dockerUp) return;
        var sb = await StartAsync(Guid.NewGuid());
        try
        {
            // Arquivo fora do bind-mount — vive no fs do próprio container.
            (await _executor.ExecAsync(sb, "echo mark > /tmp/marker",
                TimeSpan.FromSeconds(15), CancellationToken.None))
                .ExitCode.ShouldBe(0);
            await _executor.WriteFileAsync(sb, "/workspace/keep.txt", "ws",
                CancellationToken.None);

            var stopped = await _service.StopAsync(sb.Id, CancellationToken.None);
            stopped.Status.ShouldBe(SandboxStatus.Stopped);
            var inspect = await _docker.Containers
                .InspectContainerAsync(sb.ContainerId!);
            inspect.State!.Status.ShouldBe("exited");

            var resumed = await _service.ResumeAsync(sb.Id, CancellationToken.None);
            resumed.Status.ShouldBe(SandboxStatus.Running);

            var cat = await _executor.ExecAsync(sb, "cat /tmp/marker",
                TimeSpan.FromSeconds(15), CancellationToken.None);
            cat.StdOut.Trim().ShouldBe("mark");
            (await _executor.ReadFileAsync(sb, "/workspace/keep.txt",
                CancellationToken.None)).ShouldBe("ws");
        }
        finally { await _service.DeleteAsync(sb.Id, CancellationToken.None); }
    }

    [Fact]
    public async Task SleepSchedule_Pauses_Running_Sandbox()
    {
        if (!_dockerUp) return;
        var sb = await StartAsync(Guid.NewGuid());
        var conv = await SeedConversationAsync(sb.Id);
        try
        {
            var sleep = BuildSleepService(idleSeconds: 0);
            sleep.ScheduleSleep(conv.Id);
            await WaitForAsync(async () =>
                (await _service.GetAsync(sb.Id, CancellationToken.None))!
                    .Status == SandboxStatus.Paused);
        }
        finally { await _service.DeleteAsync(sb.Id, CancellationToken.None); }
    }

    [Fact]
    public async Task SleepSchedule_Skips_Conversation_With_Active_Run()
    {
        if (!_dockerUp) return;
        var sb = await StartAsync(Guid.NewGuid());
        var conv = await SeedConversationAsync(sb.Id, "Running");
        try
        {
            var sleep = BuildSleepService(idleSeconds: 0);
            sleep.ScheduleSleep(conv.Id);
            await Task.Delay(300);
            (await _service.GetAsync(sb.Id, CancellationToken.None))!
                .Status.ShouldBe(SandboxStatus.Running);
        }
        finally { await _service.DeleteAsync(sb.Id, CancellationToken.None); }
    }

    [Fact]
    public async Task SleepSchedule_Cancel_Keeps_Running()
    {
        if (!_dockerUp) return;
        var sb = await StartAsync(Guid.NewGuid());
        var conv = await SeedConversationAsync(sb.Id);
        try
        {
            var sleep = BuildSleepService(idleSeconds: 2);
            sleep.ScheduleSleep(conv.Id);
            sleep.CancelSleep(conv.Id);
            await Task.Delay(2400);
            (await _service.GetAsync(sb.Id, CancellationToken.None))!
                .Status.ShouldBe(SandboxStatus.Running);
        }
        finally { await _service.DeleteAsync(sb.Id, CancellationToken.None); }
    }

    [Fact]
    public async Task Paused_Reaper_Stops_Sandbox_Older_Than_Threshold()
    {
        if (!_dockerUp) return;
        var sb = await StartAsync(Guid.NewGuid());
        try
        {
            await _service.PauseAsync(sb.Id, CancellationToken.None);
            // Envelhece o record além do threshold do reaper — raw SQL porque
            // SaveChangesAsync reescreve UpdatedAt em todo save Modified.
            var old = DateTimeOffset.UtcNow.AddHours(-2);
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE sandboxes SET updated_at = {old}, updated_at_ticks = {old.UtcTicks} WHERE id = {sb.Id}",
                CancellationToken.None);
            var stale = await _db.Sandboxes.FindAsync(sb.Id);
            _db.Entry(stale!).State = EntityState.Detached;

            var prevInterval = SandboxTimeoutWatcher.Interval;
            SandboxTimeoutWatcher.Interval = TimeSpan.FromMilliseconds(80);
            try
            {
                var watcher = new SandboxTimeoutWatcher(
                    SingleScopeFactory(), Options.Create(new SandboxOptions
                    {
                        WorkspaceRoot = _wsRoot, InstanceOwner = _owner,
                        SleepStopAfterHours = 1,
                    }),
                    NullLogger<SandboxTimeoutWatcher>.Instance);
                await watcher.StartAsync(CancellationToken.None);
                await WaitForAsync(async () =>
                {
                    var r = await _db.Sandboxes.FindAsync(sb.Id);
                    return r!.Status == nameof(SandboxStatus.Stopped);
                });
                await watcher.StopAsync(CancellationToken.None);
            }
            finally { SandboxTimeoutWatcher.Interval = prevInterval; }

            var inspect = await _docker.Containers
                .InspectContainerAsync(sb.ContainerId!);
            inspect.State!.Status.ShouldBe("exited");
        }
        finally { await _service.DeleteAsync(sb.Id, CancellationToken.None); }
    }

    [Fact]
    public async Task WakeOnMessage_Resumes_Paused_Sandbox()
    {
        if (!_dockerUp) return;
        var sb = await StartAsync(Guid.NewGuid());
        var conv = await SeedConversationAsync(sb.Id);
        try
        {
            await _service.PauseAsync(sb.Id, CancellationToken.None);
            var svc = BuildConversationService();
            (await svc.ValidateSendMessageAsync(conv.Id, CancellationToken.None))
                .ShouldBeNull();
            (await _service.GetAsync(sb.Id, CancellationToken.None))!
                .Status.ShouldBe(SandboxStatus.Running);
        }
        finally { await _service.DeleteAsync(sb.Id, CancellationToken.None); }
    }

    [Fact]
    public async Task SendMessage_On_Error_Sandbox_Returns_Gone()
    {
        if (!_dockerUp) return;
        var sb = await StartAsync(Guid.NewGuid());
        var conv = await SeedConversationAsync(sb.Id);
        try
        {
            // Simula sandbox em erro terminal (read-only upstream parity).
            var record = await _db.Sandboxes.FindAsync(sb.Id);
            record!.Status = SandboxStatus.Error.ToString();
            await _db.SaveChangesAsync();

            var svc = BuildConversationService();
            (await svc.ValidateSendMessageAsync(conv.Id, CancellationToken.None))
                .ShouldBe(OpenHandsNET.Application.Conversations
                    .SendMessageError.Gone);
        }
        finally { await _service.DeleteAsync(sb.Id, CancellationToken.None); }
    }

    [Fact]
    public async Task SendMessage_Renews_Lease_On_Running_Sandbox()
    {
        if (!_dockerUp) return;
        var sb = await StartAsync(Guid.NewGuid());
        var conv = await SeedConversationAsync(sb.Id);
        try
        {
            var record = await _db.Sandboxes.FindAsync(sb.Id);
            record!.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(5);
            await _db.SaveChangesAsync();

            var svc = BuildConversationService();
            (await svc.ValidateSendMessageAsync(conv.Id, CancellationToken.None))
                .ShouldBeNull();
            await _db.Entry(record).ReloadAsync();
            record.ExpiresAt.ShouldNotBeNull();
            record.ExpiresAt.Value.ShouldBeGreaterThan(
                DateTimeOffset.UtcNow.AddMinutes(20));
        }
        finally { await _service.DeleteAsync(sb.Id, CancellationToken.None); }
    }

    [Fact]
    public async Task Delete_Removes_Container_And_Workspace()
    {
        if (!_dockerUp) return;
        var sb = await StartAsync(Guid.NewGuid());
        var conv = await SeedConversationAsync(sb.Id);
        var ws = _provisioner.PreparePath(conv.Id);
        Directory.CreateDirectory(ws);
        File.WriteAllText(Path.Combine(ws, "keep.txt"), "x");

        var svc = BuildConversationService();
        (await svc.DeleteAsync(conv.Id, CancellationToken.None)).ShouldBeTrue();
        Directory.Exists(ws).ShouldBeFalse();
        (await _service.GetAsync(sb.Id, CancellationToken.None)).ShouldBeNull();
    }

    private SandboxSleepService BuildSleepService(int idleSeconds) =>
        new(SingleScopeFactory(),
            Options.Create(new SandboxOptions { IdleSleepSeconds = idleSeconds }),
            NullLogger<SandboxSleepService>.Instance);

    private AppConversationService BuildConversationService() =>
        new(_db, new FakeStartTaskService(), _service, new FakeEventService(),
            _provisioner, new FakeAgentRunnerHost());

    private IServiceScopeFactory SingleScopeFactory()
    {
        // Escopo único sobre o mesmo DbContext do teste — o serviço sob teste
        // resolve db + ISandboxService desse provider.
        var sc = new ServiceCollection();
        sc.AddSingleton(_db);
        sc.AddSingleton<ISandboxService>(_service);
        return sc.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }

        (await condition()).ShouldBeTrue("timed out waiting for condition");
    }

    /// <summary>Start-task stub — os caminhos sob teste não usam o tracker.</summary>
    private sealed class FakeStartTaskService
        : OpenHandsNET.Application.Conversations.IStartTaskService
    {
        public Task<AppConversationStartTask?> GetAsync(
            Guid id, CancellationToken ct) =>
            Task.FromResult<AppConversationStartTask?>(null);

        public Task<IReadOnlyList<AppConversationStartTask>> GetBatchAsync(
            IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AppConversationStartTask>>([]);

        public Task<IReadOnlyList<AppConversationStartTask>> SearchAsync(
            StartTaskStatus? status, string? pageId, int limit,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AppConversationStartTask>>([]);

        public Task<long> CountAsync(
            StartTaskStatus? status, CancellationToken ct) =>
            Task.FromResult(0L);

        public IAsyncEnumerable<AppConversationStartTask> WatchAsync(
            Guid taskId, CancellationToken ct) =>
            AsyncEnumerable.Empty<AppConversationStartTask>();

        public ValueTask EnqueueAsync(Guid taskId, CancellationToken ct) =>
            ValueTask.CompletedTask;
    }

    /// <summary>Runner host stub — os caminhos sob teste nunca têm run ativo.</summary>
    private sealed class FakeAgentRunnerHost
        : OpenHandsNET.Application.Agent.IAgentRunnerHost
    {
        public bool IsRunning(Guid conversationId) => false;

        public Task NotifyMessageAsync(Guid conversationId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task ConfirmActionAsync(
            Guid conversationId, string actionId, bool approved, string? reason,
            CancellationToken ct) => Task.CompletedTask;

        public Task InterruptAsync(Guid conversationId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task PauseAsync(Guid conversationId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task ResumeAsync(Guid conversationId, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class FakeEventService : IEventService
    {
        public ValueTask<EventBase> AppendAsync(
            Guid conversationId, EventBase e, CancellationToken ct) =>
            ValueTask.FromResult(e);

        public Task<EventPage> SearchAsync(
            Guid conversationId, EventQuery q, CancellationToken ct) =>
            Task.FromResult(new EventPage([], null));

        public Task<long> CountAsync(
            Guid conversationId, EventQuery q, CancellationToken ct) =>
            Task.FromResult(0L);

        public Task<IReadOnlyList<EventBase>> GetBatchAsync(
            Guid conversationId, IReadOnlyCollection<string> ids,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<EventBase>>([]);

        public IAsyncEnumerable<EventBase> SubscribeAsync(
            Guid conversationId, CancellationToken ct) =>
            AsyncEnumerable.Empty<EventBase>();
    }
}
