using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using OpenHandsNET.Application.Automations;
using OpenHandsNET.Application.Conversations;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Domain.Conversations;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Infrastructure.Automations;
using OpenHandsNET.Infrastructure.Conversations;
using OpenHandsNET.Infrastructure.Persistence;
using OpenHandsNET.UnitTests.Agent;

using Shouldly;

using Xunit;

namespace OpenHandsNET.UnitTests;

/// <summary>
/// SPEC-47 RF-002/RF-006: <see cref="AutomationService"/> — CRUD
/// owner-scoped, disparo (conversa + run + reagenda + diagnostic +
/// audit), guard de concorrência, once esgota, quota → failed e
/// reconcile running→succeeded/failed.
/// </summary>
public sealed class Spec47AutomationServiceTests
{
    private sealed class FakeConversationService : IAppConversationService
    {
        public int Created { get; private set; }
        public CreateConversationRequest? LastRequest { get; private set; }
        public Guid? LastUserId { get; private set; }
        public Guid? LastOrgId { get; private set; }
        public Exception? ThrowOnCreate { get; set; }
        public Guid ConversationId { get; } = Guid.NewGuid();

        public Task<AppConversationStartTask> CreateForUserAsync(
            Guid userId, Guid? orgId, CreateConversationRequest request,
            CancellationToken ct)
        {
            if (ThrowOnCreate is not null) { throw ThrowOnCreate; }
            Created++;
            LastRequest = request;
            LastUserId = userId;
            LastOrgId = orgId;
            return Task.FromResult(new AppConversationStartTask(
                Guid.NewGuid(), StartTaskStatus.Working, ConversationId,
                null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

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
        public Task<AppConversation?> GetAsync(Guid id, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<AppConversation?> UpdateAsync(Guid id, string? title,
            IReadOnlyList<string>? tags, bool? pinned,
            CancellationToken ct) =>
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
            IReadOnlyList<Application.Agent.AgentPermissionRule>? rules,
            CancellationToken ct) =>
            throw new NotSupportedException();
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

    private sealed class RecordingAudit : IAuditService
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

    private static async
        Task<(SqliteConnection conn, AppDbContext db, AutomationService svc,
            FakeConversationService convs, FakeEventService events,
            RecordingAudit audit)> NewAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(conn).UseSnakeCaseNamingConvention().Options);
        await db.Database.EnsureCreatedAsync();
        var convs = new FakeConversationService();
        var events = new FakeEventService();
        var audit = new RecordingAudit();
        return (conn, db, new AutomationService(db, convs, events, audit),
            convs, events, audit);
    }

    private static readonly Guid Owner = Guid.NewGuid();

    private static AutomationUpsert Upsert(
        string kind = "interval", int? minutes = 15,
        string? time = null, int? weekday = null,
        DateTimeOffset? next = null, bool? enabled = true) => new(
        "review diária", "rode a review", null, "owner/repo",
        kind, time, minutes, weekday, enabled, next);

    [Fact]
    public async Task Create_Valida_Prompt_Vazio_E_Intervalo_Minimo()
    {
        var (conn, _, svc, _, _, _) = await NewAsync();
        await using var _ = conn;

        (await svc.CreateAsync(Upsert() with { Prompt = " " },
            Owner, null, CancellationToken.None)).Error
            .ShouldBe("prompt required");
        (await svc.CreateAsync(Upsert(minutes: 2),
            Owner, null, CancellationToken.None)).Error!
            .ShouldContain("interval_minutes");
        (await svc.CreateAsync(Upsert(kind: "daily"),
            Owner, null, CancellationToken.None)).Error!
            .ShouldContain("schedule_time_utc");
        (await svc.CreateAsync(Upsert(kind: "banana"),
            Owner, null, CancellationToken.None)).Error!
            .ShouldContain("schedule_kind");
    }

    [Fact]
    public async Task Create_Interval_Computa_NextRun()
    {
        var (conn, _, svc, _, _, _) = await NewAsync();
        await using var _ = conn;

        var (info, err) = await svc.CreateAsync(
            Upsert(minutes: 30), Owner, null, CancellationToken.None);
        err.ShouldBeNull();
        info!.NextRunAt.ShouldNotBeNull();
        info.NextRunAt!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        info.Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task RunNow_Cria_Conversa_Run_Rodando_E_Reagenda()
    {
        var (conn, db, svc, convs, events, audit) = await NewAsync();
        await using var _ = conn;

        var (info, _) = await svc.CreateAsync(
            Upsert(minutes: 30), Owner, Guid.NewGuid(), CancellationToken.None);
        var (run, fired) = await svc.RunNowAsync(
            info!.Id, Owner, CancellationToken.None);

        fired.ShouldBeTrue();
        run!.Status.ShouldBe("running");
        run.ConversationId.ShouldBe(convs.ConversationId);
        convs.Created.ShouldBe(1);
        convs.LastUserId.ShouldBe(Owner);
        convs.LastRequest!.InitialMessage!.Run.ShouldBeTrue();
        convs.LastRequest.SelectedRepository.ShouldBe("owner/repo");

        // diagnostic de origem automation:{id} na conversa (RF-006).
        events.Events.OfType<AgentDiagnosticEvent>().Single()
            .Code.ShouldBe("automation");
        audit.Actions.ShouldContain("automation_fired");
        audit.Actions.ShouldContain("automation_run_created");

        var rec = await db.Automations.FindAsync(info.Id);
        rec!.LastRunAt.ShouldNotBeNull();
        rec.NextRunAt.ShouldNotBeNull(); // interval reagenda
    }

    [Fact]
    public async Task RunNow_Guard_Skips_Quando_Run_Ativa()
    {
        var (conn, _, svc, _, _, _) = await NewAsync();
        await using var _ = conn;

        var (info, _) = await svc.CreateAsync(
            Upsert(), Owner, null, CancellationToken.None);
        var (r1, f1) = await svc.RunNowAsync(
            info!.Id, Owner, CancellationToken.None);
        f1.ShouldBeTrue();
        r1!.Status.ShouldBe("running");

        // segunda chamada enquanto a primeira está running → skipped.
        var (r2, f2) = await svc.RunNowAsync(
            info.Id, Owner, CancellationToken.None);
        f2.ShouldBeFalse();
        r2!.Status.ShouldBe("skipped");
    }

    [Fact]
    public async Task Once_Desabilita_Apos_Disparo()
    {
        var (conn, db, svc, _, _, _) = await NewAsync();
        await using var _ = conn;

        var (info, _) = await svc.CreateAsync(
            Upsert(kind: "once", minutes: null,
                next: DateTimeOffset.UtcNow.AddHours(1)),
            Owner, null, CancellationToken.None);
        await svc.RunNowAsync(info!.Id, Owner, CancellationToken.None);

        var rec = await db.Automations.FindAsync(info.Id);
        rec!.Enabled.ShouldBeFalse();
        rec.NextRunAt.ShouldBeNull();
    }

    [Fact]
    public async Task Quota_Negada_Vira_Run_Failed()
    {
        var (conn, _, svc, convs, _, _) = await NewAsync();
        await using var _ = conn;

        var (info, _) = await svc.CreateAsync(
            Upsert(), Owner, null, CancellationToken.None);
        convs.ThrowOnCreate = new PlanLimitExceededException(
            new PlanLimitError("conversations_per_day", 3, 3));
        var (run, fired) = await svc.RunNowAsync(
            info!.Id, Owner, CancellationToken.None);

        fired.ShouldBeTrue(); // run row existe — só o create falhou
        run!.Status.ShouldBe("failed");
        run.Error.ShouldStartWith("quota:");
    }

    [Fact]
    public async Task Tick_Dispara_Devidas_E_Reconcilia_Runs()
    {
        var (conn, db, svc, convs, _, _) = await NewAsync();
        await using var _ = conn;

        // automation devida (NextRunAt no passado) + conversa Finished.
        var now = DateTimeOffset.UtcNow;
        var (info, _) = await svc.CreateAsync(
            Upsert(minutes: 15), Owner, null, CancellationToken.None);
        var rec = await db.Automations.FindAsync(info!.Id);
        rec!.NextRunAt = now.AddMinutes(-1);
        await db.SaveChangesAsync();

        await svc.TickAsync(now, CancellationToken.None);
        convs.Created.ShouldBe(1);

        // seed conversa Finished linkada ao run → reconcile → succeeded.
        var run = await db.AutomationRuns
            .SingleAsync(r => r.AutomationId == info.Id);
        db.Conversations.Add(new ConversationRecord
        {
            Id = run.ConversationId!.Value,
            ExecutionStatus = nameof(ConversationExecutionStatus.Finished),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        await svc.TickAsync(now, CancellationToken.None);

        var after = await db.AutomationRuns.FindAsync(run.Id);
        after!.Status.ShouldBe("succeeded");
    }

    [Fact]
    public async Task Hook_Token_Hash_Fire_E_404_Em_Invalido()
    {
        var (conn, db, svc, convs, _, _) = await NewAsync();
        await using var _ = conn;

        var (info, _) = await svc.CreateAsync(
            Upsert(), Owner, null, CancellationToken.None);
        var (hook, token) = await svc.CreateHookAsync(
            info!.Id, Owner, CancellationToken.None);
        hook.ShouldNotBeNull();
        token.ShouldNotBeNull();
        token!.ShouldStartWith("ohn_hook_");
        token.Length.ShouldBe(73); // prefixo + 64 hex

        // banco guarda só o hash.
        var rec = await db.AutomationHooks.FindAsync(hook!.Id);
        rec!.TokenHash.ShouldNotBe(token);

        (await svc.FireByTokenAsync("token-errado", "1.2.3.4",
            CancellationToken.None)).ShouldBeNull();
        var run = await svc.FireByTokenAsync(
            token!, "1.2.3.4", CancellationToken.None);
        run.ShouldNotBeNull();
        convs.Created.ShouldBe(1);

        (await db.AutomationHooks.FindAsync(hook.Id))!.LastUsedAt
            .ShouldNotBeNull();
    }

    [Fact]
    public async Task Owner_Scope_Isola_Outros_Usuarios()
    {
        var (conn, _, svc, _, _, _) = await NewAsync();
        await using var _ = conn;

        var (info, _) = await svc.CreateAsync(
            Upsert(), Owner, null, CancellationToken.None);
        var other = Guid.NewGuid();
        (await svc.GetAsync(info!.Id, other, CancellationToken.None))
            .ShouldBeNull();
        (await svc.RunNowAsync(info.Id, other, CancellationToken.None))
            .Fired.ShouldBeFalse();
        (await svc.ListAsync(other, CancellationToken.None))
            .ShouldBeEmpty();
    }
}