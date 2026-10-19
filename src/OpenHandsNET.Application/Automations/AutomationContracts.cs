namespace OpenHandsNET.Application.Automations;

/// <summary>Schedule kinds aceitos (SPEC-47 RF-001).</summary>
public static class AutomationScheduleKinds
{
    /// <summary>A cada N minutos.</summary>
    public const string Interval = "interval";
    /// <summary>Diariamente no horário UTC.</summary>
    public const string Daily = "daily";
    /// <summary>Toda semana no weekday + horário UTC.</summary>
    public const string Weekly = "weekly";
    /// <summary>Execução única — desabilita após disparar.</summary>
    public const string Once = "once";

    /// <summary>Todos os kinds válidos.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        { Interval, Daily, Weekly, Once };
}

/// <summary>Automation DTO (SPEC-47 RF-003).</summary>
public sealed record AutomationInfo(
    Guid Id, Guid? OrgId, string? CreatedByUserId, string Name,
    string Prompt, string? LlmModel, string? SelectedRepository,
    string ScheduleKind, string? ScheduleTimeUtc, int IntervalMinutes,
    int? Weekday, bool Enabled, DateTimeOffset? NextRunAt,
    DateTimeOffset? LastRunAt, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Run de uma automation (SPEC-47 RF-002/RF-003).</summary>
public sealed record AutomationRunInfo(
    Guid Id, Guid AutomationId, DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt, string Status, Guid? ConversationId,
    string? Error);

/// <summary>Hook de trigger externo (token exibido uma única vez).</summary>
public sealed record AutomationHookInfo(
    Guid Id, Guid AutomationId, DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt);

/// <summary>Payload de criação/edição (SPEC-47 RF-003).</summary>
public sealed record AutomationUpsert(
    string? Name, string? Prompt, string? LlmModel,
    string? SelectedRepository, string? ScheduleKind,
    string? ScheduleTimeUtc, int? IntervalMinutes, int? Weekday,
    bool? Enabled, DateTimeOffset? NextRunAt);

/// <summary>
/// Cálculo do próximo disparo (UTC) — puro e testável
/// (SPEC-47 RF-002; port do open-webui <c>AutomationSchedule</c>, MIT).
/// </summary>
public static class AutomationSchedule
{
    /// <summary>
    /// Próximo disparo a partir de <paramref name="now"/>, ou
    /// <see langword="null"/> quando a agenda está inválida/esgotada
    /// (<c>once</c> já disparada ou kind desconhecido).
    /// </summary>
    public static DateTimeOffset? ComputeNextRun(
        string scheduleKind, string? scheduleTimeUtc,
        int? intervalMinutes, int? weekday,
        DateTimeOffset? storedNextRunAt, DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        return scheduleKind switch
        {
            AutomationScheduleKinds.Interval =>
                utc.AddMinutes(Math.Max(1, intervalMinutes ?? 0)),
            AutomationScheduleKinds.Daily =>
                NextDaily(utc, scheduleTimeUtc),
            AutomationScheduleKinds.Weekly =>
                NextWeekly(utc, weekday ?? 0, scheduleTimeUtc),
            // Execução única: mantém o horário já armazenado enquanto
            // futuro; depois de disparar devolve null e nunca reagenda.
            AutomationScheduleKinds.Once =>
                storedNextRunAt is { } at && at > utc ? at : null,
            _ => null,
        };
    }

    private static DateTimeOffset? NextDaily(
        DateTimeOffset now, string? timeOfDay)
    {
        if (!TryParseTime(timeOfDay, out var time))
        {
            return null;
        }

        var candidate = new DateTimeOffset(now.Year, now.Month, now.Day,
            time.Hour, time.Minute, 0, TimeSpan.Zero);
        return candidate <= now ? candidate.AddDays(1) : candidate;
    }

    private static DateTimeOffset? NextWeekly(
        DateTimeOffset now, int weekday, string? timeOfDay)
    {
        if (!TryParseTime(timeOfDay, out var time))
        {
            return null;
        }

        var day = (DayOfWeek)Math.Clamp(weekday, 0, 6);
        var candidate = new DateTimeOffset(now.Year, now.Month, now.Day,
            time.Hour, time.Minute, 0, TimeSpan.Zero);
        candidate = candidate.AddDays(
            ((int)day - (int)now.DayOfWeek + 7) % 7);
        return candidate <= now ? candidate.AddDays(7) : candidate;
    }

    private static bool TryParseTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value, "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None, out time);
}

/// <summary>
/// Automation service (SPEC-47): CRUD owner-scoped, disparo (start-task
/// de conversa), histórico de runs e hooks de trigger.
/// </summary>
public interface IAutomationService
{
    /// <summary>Lista as automations do owner.</summary>
    Task<IReadOnlyList<AutomationInfo>> ListAsync(
        Guid ownerUserId, CancellationToken ct);

    /// <summary>Detalhe (owner-scoped).</summary>
    Task<AutomationInfo?> GetAsync(
        Guid id, Guid ownerUserId, CancellationToken ct);

    /// <summary>
    /// Cria (valida kind/horários/intervalo — o segundo elemento do
    /// tuple carrega a mensagem de validação quando inválido).
    /// </summary>
    Task<(AutomationInfo? Info, string? Error)> CreateAsync(
        AutomationUpsert req, Guid ownerUserId, Guid? orgId,
        CancellationToken ct);

    /// <summary>Atualiza campos + toggle Enabled (recomputa NextRunAt).</summary>
    Task<(AutomationInfo? Info, string? Error)> UpdateAsync(
        Guid id, AutomationUpsert req, Guid ownerUserId,
        CancellationToken ct);

    /// <summary>Remove a automation (runs/hooks em cascata).</summary>
    Task<bool> DeleteAsync(
        Guid id, Guid ownerUserId, CancellationToken ct);

    /// <summary>
    /// Dispara agora — cria a conversa (start-task) e grava o run.
    /// Concurrency guard: retorna <c>(null, false)</c> quando já há run
    /// <c>running</c> da mesma automation.
    /// </summary>
    Task<(AutomationRunInfo? Run, bool Fired)> RunNowAsync(
        Guid id, Guid ownerUserId, CancellationToken ct);

    /// <summary>Runs de uma automation (mais recentes primeiro).</summary>
    Task<IReadOnlyList<AutomationRunInfo>> ListRunsAsync(
        Guid id, Guid ownerUserId, int take, CancellationToken ct);

    /// <summary>Histórico global de runs do owner (janela from/to).</summary>
    Task<IReadOnlyList<AutomationRunInfo>> ListAllRunsAsync(
        Guid ownerUserId, DateTimeOffset? from, DateTimeOffset? to,
        int take, CancellationToken ct);

    /// <summary>Cria hook — retorna o token UMA vez (sha256 no banco).</summary>
    Task<(AutomationHookInfo? Hook, string? Token)> CreateHookAsync(
        Guid automationId, Guid ownerUserId, CancellationToken ct);

    /// <summary>Remove hook (owner-scoped).</summary>
    Task<bool> DeleteHookAsync(
        Guid hookId, Guid ownerUserId, CancellationToken ct);

    /// <summary>
    /// Trigger anônimo por token — resolve o hash e dispara a automation
    /// linkada (mesmo guard de concorrência). <see langword="null"/> =
    /// token inválido (endpoint devolve 404, não 401 — não vazar
    /// existência).
    /// </summary>
    Task<AutomationRunInfo?> FireByTokenAsync(
        string token, string? ip, CancellationToken ct);

    /// <summary>
    /// Tick do scheduler: dispara todas as automations devidas
    /// (<c>Enabled</c> e <c>NextRunAt &lt;= now</c>) — catch-up dispara uma
    /// vez e recomputa — e reconcilia runs <c>running</c> com o
    /// ExecutionStatus da conversa linkada.
    /// </summary>
    Task TickAsync(DateTimeOffset now, CancellationToken ct);
}