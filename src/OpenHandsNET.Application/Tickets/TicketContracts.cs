namespace OpenHandsNET.Application.Tickets;

/// <summary>Tipo de tracker suportado (SPEC-55 RF-001).</summary>
public enum TicketKind
{
    /// <summary>Linear (GraphQL API).</summary>
    Linear,
    /// <summary>Jira Cloud (REST v3).</summary>
    Jira,
}

/// <summary>Wire helpers.</summary>
public static class TicketKindExtensions
{
    /// <summary>linear | jira.</summary>
    public static string ToWire(this TicketKind k) => k switch
    {
        TicketKind.Linear => "linear",
        TicketKind.Jira => "jira",
        _ => k.ToString().ToLowerInvariant(),
    };

    /// <summary>Parse tolerante.</summary>
    public static TicketKind? Parse(string? s) => s?.ToLowerInvariant() switch
    {
        "linear" => TicketKind.Linear,
        "jira" => TicketKind.Jira,
        _ => null,
    };
}

/// <summary>
/// Conexão com um tracker (SPEC-55 RF-001). WorkspaceId = id do
/// workspace externo (Linear organizationId / Jira host) — casa
/// webhooks à conexão certa; TriggerConfigJson = JSON de
/// <see cref="TicketTriggerConfig"/>.
/// </summary>
public sealed record TicketConnection(
    Guid Id,
    Guid? OrgId,
    TicketKind Kind,
    string ApiUrl,
    string TokenSecretRef,
    string? WebhookSecretRef,
    string? WorkspaceId,
    string? TriggerConfigJson,
    string? CreatedByUserId,
    DateTimeOffset CreatedAt);

/// <summary>
/// Regra de disparo por connection (SPEC-55 RF-003):
/// <c>mode</c> = label | assignee | state; <c>value</c> = valor que
/// dispara (ex.: "openhandsnet"); template com placeholders
/// {identifier} {title} {description} {labels} {url} {comment}.
/// </summary>
public sealed record TicketTriggerConfig(
    string Mode = "label",
    string Value = "openhandsnet",
    string? PromptTemplate = null,
    string? SelectedRepository = null,
    string? DoneState = null);

/// <summary>Mapeamento projeto → repo/branch (SPEC-55 RF-003).</summary>
public sealed record TicketProjectConfig(
    Guid Id,
    Guid ConnectionId,
    string ProjectKey,
    string? SelectedRepository,
    string? SelectedBranch);

/// <summary>Issue normalizada.</summary>
public sealed record TicketIssue(
    string Key,
    string Title,
    string? Description,
    string? Url,
    IReadOnlyList<string> Labels,
    string? State,
    string? Assignee,
    string? ProjectKey);

/// <summary>
/// Evento de webhook normalizado. EventKind: issue_updated |
/// comment_created; AddedLabel/NewAssignee/NewState só em
/// issue_updated; CommentBody só em comment_created.
/// </summary>
public sealed record TicketWebhookEvent(
    string EventKind,
    string IssueKey,
    string? DeliveryId,
    string? AddedLabel,
    string? NewAssignee,
    string? NewState,
    string? CommentBody,
    string? ProjectKey,
    string? Title,
    string? Description,
    string? Url,
    IReadOnlyList<string>? Labels,
    string? State,
    string? Assignee);

/// <summary>Resultado do pipeline de tickets.</summary>
public enum TicketOpsResult
{
    /// <summary>Start-task disparado.</summary>
    Processed,
    /// <summary>Evento válido mas não bateu trigger.</summary>
    Ignored,
    /// <summary>Assinatura/segredo inválido.</summary>
    InvalidSignature,
    /// <summary>Nenhuma connection casou.</summary>
    NoConnection,
    /// <summary>Delivery repetido.</summary>
    Duplicate,
    /// <summary>Payload malformado.</summary>
    BadPayload,
}

/// <summary>Provider de tracker (SPEC-55 RF-001).</summary>
public interface ITicketProvider
{
    /// <summary>Kind atendido.</summary>
    TicketKind Kind { get; }

    /// <summary>
    /// Verifica autenticidade do webhook. Linear: Linear-Signature HMAC;
    /// Jira: segredo compartilhado (header/query).
    /// </summary>
    Task<bool> VerifyAsync(
        TicketConnection connection, string? signatureOrSecret,
        byte[] payload, CancellationToken ct);

    /// <summary>Parseia o payload num evento normalizado.</summary>
    TicketWebhookEvent? ParseWebhook(byte[] payload);

    /// <summary>GET issue (enriquecimento para run manual).</summary>
    Task<TicketIssue?> GetIssueAsync(
        TicketConnection connection, string issueKey,
        CancellationToken ct);

    /// <summary>Comenta na issue.</summary>
    Task AddCommentAsync(
        TicketConnection connection, string issueKey, string body,
        CancellationToken ct);

    /// <summary>Move a issue para o estado nomeado (best-effort).</summary>
    Task TransitionAsync(
        TicketConnection connection, string issueKey, string stateName,
        CancellationToken ct);

    /// <summary>Diagnóstico (auth check).</summary>
    Task<(bool Ok, string? Detail)> TestConnectionAsync(
        TicketConnection connection, CancellationToken ct);
}

/// <summary>Registry kind → provider.</summary>
public interface ITicketProviderRegistry
{
    /// <summary>Provider do kind (null = não suportado).</summary>
    ITicketProvider? Get(TicketKind kind);
}

/// <summary>CRUD de connections + project configs (admin).</summary>
public interface ITicketConnectionService
{
    /// <summary>Cria conexão.</summary>
    Task<TicketConnection> CreateAsync(
        Guid orgId, TicketKind kind, string apiUrl,
        string tokenSecretRef, string? webhookSecretRef,
        string? workspaceId, string? triggerConfigJson,
        string createdByUserId, CancellationToken ct);

    /// <summary>Lista por org.</summary>
    Task<IReadOnlyList<TicketConnection>> ListAsync(
        Guid orgId, CancellationToken ct);

    /// <summary>Por id.</summary>
    Task<TicketConnection?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Resolve conexão para um webhook (kind + workspace).</summary>
    Task<TicketConnection?> FindForWebhookAsync(
        TicketKind kind, string? workspaceId, CancellationToken ct);

    /// <summary>
    /// Remove se pertence à org (false = não encontrada ou fora da
    /// org — SPEC-79).
    /// </summary>
    Task<bool> DeleteAsync(Guid id, Guid orgId, CancellationToken ct);

    /// <summary>
    /// Upsert project config (null = connection fora da org — SPEC-79).
    /// </summary>
    Task<TicketProjectConfig?> SetProjectConfigAsync(
        Guid connectionId, Guid orgId, string projectKey,
        string? repository, string? branch, CancellationToken ct);

    /// <summary>Project config por chave.</summary>
    Task<TicketProjectConfig?> GetProjectConfigAsync(
        Guid connectionId, string projectKey, CancellationToken ct);

    /// <summary>
    /// Lista project configs da connection (null = connection
    /// inexistente ou fora da org — SPEC-79).
    /// </summary>
    Task<IReadOnlyList<TicketProjectConfig>?> ListProjectConfigsAsync(
        Guid connectionId, Guid orgId, CancellationToken ct);
}

/// <summary>
/// Pipeline do webhook de ticket (SPEC-55 RF-002/003): verify → parse →
/// trigger eval → start-task + binding.
/// </summary>
public interface ITicketTriggerService
{
    /// <summary>Processa um webhook.</summary>
    Task<TicketOpsResult> HandleAsync(
        TicketKind kind, string? signatureOrSecret, string? workspaceHint,
        byte[] payload, CancellationToken ct);

    /// <summary>
    /// Run manual por issue (CLI <c>tickets run</c>): busca a issue e
    /// dispara start-task com o mesmo enriquecimento.
    /// </summary>
    Task<TicketOpsResult> RunAsync(
        Guid connectionId, string issueKey, CancellationToken ct);
}

/// <summary>Write-back pós-run (SPEC-55 RF-004).</summary>
public interface ITicketReporter
{
    /// <summary>Comenta resultado + transição configurada.</summary>
    Task ReportRunFinishedAsync(
        Guid conversationId, bool success, string? summary,
        string? pullRequestUrl, CancellationToken ct);
}