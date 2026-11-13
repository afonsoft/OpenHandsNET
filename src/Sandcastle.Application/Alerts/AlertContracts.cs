namespace Sandcastle.Application.Alerts;

/// <summary>
/// Alert provider kinds (SPEC-151 RF-001): sentry | pagerduty | generic.
/// <c>generic</c> cobre Datadog/NewRelic/Grafana/etc. via shared-secret.
/// </summary>
public enum AlertProvider
{
    /// <summary>Sentry issue webhooks.</summary>
    Sentry,

    /// <summary>PagerDuty incident webhooks (v3).</summary>
    PagerDuty,

    /// <summary>Generic JSON + shared-secret header.</summary>
    Generic,
}

/// <summary>Wire name ↔ <see cref="AlertProvider"/>.</summary>
public static class AlertProviderExtensions
{
    /// <summary>Wire name (e.g. <c>sentry</c>).</summary>
    public static string ToWire(this AlertProvider provider) => provider switch
    {
        AlertProvider.Sentry => "sentry",
        AlertProvider.PagerDuty => "pagerduty",
        AlertProvider.Generic => "generic",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    /// <summary>Parses a wire name (null when unknown).</summary>
    public static AlertProvider? Parse(string? wire) => wire switch
    {
        "sentry" => AlertProvider.Sentry,
        "pagerduty" => AlertProvider.PagerDuty,
        "generic" => AlertProvider.Generic,
        _ => null,
    };
}

/// <summary>Canonical alert severities (ordered high→low).</summary>
public static class AlertSeverities
{
    /// <summary>fatal/critical.</summary>
    public const string Critical = "critical";

    /// <summary>error/high.</summary>
    public const string High = "high";

    /// <summary>warning/medium.</summary>
    public const string Medium = "medium";

    /// <summary>low.</summary>
    public const string Low = "low";

    /// <summary>info/debug.</summary>
    public const string Info = "info";

    /// <summary>All severities (validation).</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Critical, High, Medium, Low, Info], StringComparer.Ordinal);

    /// <summary>
    /// Numeric rank — bigger = mais severo. Desconhecido rankeia como
    /// <see cref="Info"/> (fail-safe: nunca dispara acima de low).
    /// </summary>
    public static int Rank(string? severity) => severity switch
    {
        Critical => 5,
        High => 4,
        Medium => 3,
        Low => 2,
        Info => 1,
        _ => 1,
    };

    /// <summary>
    /// Normaliza aliases de providers (fatal/error/warning/debug,
    /// P1..P5, urgent/high) para o conjunto canônico.
    /// </summary>
    public static string Normalize(string? severity) =>
        severity?.Trim().ToLowerInvariant() switch
        {
            "critical" or "fatal" or "sev0" or "sev1" or "p1"
                or "urgent" => Critical,
            "high" or "error" or "sev2" or "p2" => High,
            "medium" or "warning" or "warn" or "sev3" or "p3" => Medium,
            "low" or "sev4" or "p4" => Low,
            "info" or "debug" or "informational" or "sev5" or "p5" => Info,
            _ => Medium,
        };
}

/// <summary>Event kinds normalizados.</summary>
public static class AlertEventKinds
{
    /// <summary>Nova issue/erro (Sentry).</summary>
    public const string Issue = "issue";

    /// <summary>Incidente operacional (PagerDuty).</summary>
    public const string Incident = "incident";

    /// <summary>Alerta resolvido no provider (fecha o loop).</summary>
    public const string Resolved = "resolved";
}

/// <summary>Event statuses (lifecycle).</summary>
public static class AlertEventStatuses
{
    /// <summary>Recém-ingestado.</summary>
    public const string New = "new";

    /// <summary>Sessão de investigação em curso.</summary>
    public const string Investigating = "investigating";

    /// <summary>PR de fix aberto pela sessão.</summary>
    public const string PrOpen = "pr_open";

    /// <summary>Resolvido (inbound resolved ou manual).</summary>
    public const string Resolved = "resolved";

    /// <summary>Descartado por um humano.</summary>
    public const string Dismissed = "dismissed";

    /// <summary>Statuses que PATCH manual aceita.</summary>
    public static readonly IReadOnlySet<string> Updatable =
        new HashSet<string>([Dismissed, Resolved, New],
            StringComparer.Ordinal);
}

/// <summary>Config de uma fonte de alertas (secret por-referência).</summary>
public sealed record AlertSource(
    Guid Id,
    Guid? OrgId,
    AlertProvider Provider,
    string Name,
    string SecretRef,
    IReadOnlyDictionary<string, string> RepoMapping,
    bool Enabled,
    bool AutoCreateSession,
    string TriageSeverityMin,
    string? CreatedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Evento de alerta normalizado e persistido.</summary>
public sealed record AlertEvent(
    Guid Id,
    Guid? OrgId,
    Guid SourceId,
    AlertProvider Provider,
    string ExternalId,
    string Kind,
    string Title,
    string? Url,
    string Severity,
    string? Service,
    string? Culprit,
    string? Repository,
    string? LabelsJson,
    DateTimeOffset FirstSeen,
    string Status,
    Guid? SessionId,
    string? PrUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>True quando nenhum repo foi mapeado (route manual).</summary>
    public bool Unrouted => Repository is null;
}

/// <summary>Resultado do pipeline inbound do webhook de alertas.</summary>
public enum AlertIngestResult
{
    /// <summary>200 — evento persistido (e possivelmente roteado).</summary>
    Processed,

    /// <summary>401 — signature ausente/inválida (fail-closed).</summary>
    InvalidSignature,

    /// <summary>404 — source inexistente ou disabled.</summary>
    NoSource,

    /// <summary>200 — entrega duplicada (delivery id / external id).</summary>
    Duplicate,

    /// <summary>200 — payload inválido/descartado (não é alerta).</summary>
    Ignored,

    /// <summary>429 — rate limit inbound por source excedido.</summary>
    RateLimited,
}

/// <summary>
/// Inbound alert pipeline (SPEC-151 RF-001): signature verify por
/// provider → normalização → persist idempotente → routing/sessão.
/// O endpoint só autentica por signature — nunca por sessão de usuário.
/// </summary>
public interface IAlertIngestService
{
    /// <summary>
    /// Processa uma entrega de webhook. <paramref name="headers"/> é o
    /// mapa case-insensitive dos headers relevantes (signature +
    /// delivery). <paramref name="payload"/> é o corpo bruto.
    /// </summary>
    Task<AlertIngestResult> HandleAsync(
        Guid sourceId,
        IReadOnlyDictionary<string, string?> headers,
        byte[] payload,
        CancellationToken ct);
}

/// <summary>Filtros da listagem de eventos.</summary>
public sealed record AlertEventQuery(
    Guid? SourceId = null,
    string? Provider = null,
    string? Severity = null,
    string? Status = null,
    string? Repository = null,
    bool Unrouted = false,
    string? PageId = null,
    int Limit = 50);

/// <summary>Página cursada de eventos.</summary>
public sealed record AlertEventPage(
    IReadOnlyList<AlertEvent> Items, string? NextPageId);

/// <summary>Resultado de create-session (auto ou manual).</summary>
public sealed record AlertSessionResult(
    Guid? ConversationId,
    AlertSessionError? Error)
{
    /// <summary>Sessão criada.</summary>
    public static AlertSessionResult Ok(Guid conversationId) =>
        new(conversationId, null);

    /// <summary>Falha mapeada p/ HTTP.</summary>
    public static AlertSessionResult Fail(AlertSessionError error) =>
        new(null, error);
}

/// <summary>Motivos de falha de create-session.</summary>
public enum AlertSessionError
{
    /// <summary>Evento inexistente/fora da org.</summary>
    NotFound,

    /// <summary>Evento sem repo mapeado (unrouted).</summary>
    Unrouted,

    /// <summary>Já existe sessão para (source, external_id).</summary>
    Duplicate,

    /// <summary>Teto de sessões/hora da source excedido.</summary>
    QuotaExceeded,

    /// <summary>Severidade abaixo do mínimo de triagem da source.</summary>
    BelowSeverity,
}

/// <summary>Admin CRUD de fontes + consulta/ações de eventos.</summary>
public interface IAlertService
{
    /// <summary>Cria a fonte (secret_ref aponta o signing secret/token).</summary>
    Task<AlertSource> CreateSourceAsync(
        Guid orgId, AlertProvider provider, string name, string secretRef,
        IReadOnlyDictionary<string, string>? repoMapping,
        bool enabled, bool autoCreateSession, string triageSeverityMin,
        string? createdByUserId, CancellationToken ct);

    /// <summary>Lista fontes da org.</summary>
    Task<IReadOnlyList<AlertSource>> ListSourcesAsync(
        Guid orgId, CancellationToken ct);

    /// <summary>Obtém fonte por id (org-validada).</summary>
    Task<AlertSource?> GetSourceAsync(
        Guid id, Guid orgId, CancellationToken ct);

    /// <summary>
    /// PATCH da fonte — campos nulos preservam o valor atual.
    /// Retorna null quando inexistente/fora da org.
    /// </summary>
    Task<AlertSource?> UpdateSourceAsync(
        Guid id, Guid orgId, string? name, string? secretRef,
        IReadOnlyDictionary<string, string>? repoMapping,
        bool? enabled, bool? autoCreateSession,
        string? triageSeverityMin, CancellationToken ct);

    /// <summary>Remove se pertence à org (false = não achou).</summary>
    Task<bool> DeleteSourceAsync(
        Guid id, Guid orgId, CancellationToken ct);

    /// <summary>Lista eventos da org com filtros + cursor.</summary>
    Task<AlertEventPage> SearchEventsAsync(
        Guid orgId, AlertEventQuery query, CancellationToken ct);

    /// <summary>Obtém evento por id (org-validado).</summary>
    Task<AlertEvent?> GetEventAsync(
        Guid id, Guid orgId, CancellationToken ct);

    /// <summary>
    /// PATCH do evento — <paramref name="status"/> em
    /// <see cref="AlertEventStatuses.Updatable"/>;
    /// <paramref name="repository"/> faz route manual do unrouted.
    /// </summary>
    Task<AlertEvent?> UpdateEventAsync(
        Guid id, Guid orgId, string? status, string? repository,
        CancellationToken ct);

    /// <summary>
    /// Cria a sessão de investigação para o evento (manual —
    /// mesma policy do auto-create: mapping + dedup + cap;
    /// severity gate não se aplica no manual explícito).
    /// </summary>
    Task<AlertSessionResult> CreateSessionAsync(
        Guid id, Guid orgId, string actorUserId, CancellationToken ct);
}