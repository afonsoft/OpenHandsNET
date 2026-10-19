namespace OpenHandsNET.Application.ChatOps;

/// <summary>Chat provider kinds (SPEC-54; ms-teams futuro).</summary>
public enum ChatKind
{
    /// <summary>Slack workspace.</summary>
    Slack,
}

/// <summary>Wire name ↔ <see cref="ChatKind"/>.</summary>
public static class ChatKindExtensions
{
    /// <summary>Wire name (e.g. <c>slack</c>).</summary>
    public static string ToWire(this ChatKind kind) => kind switch
    {
        ChatKind.Slack => "slack",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Parses a wire name (null when unknown).</summary>
    public static ChatKind? Parse(string? wire) => wire switch
    {
        "slack" => ChatKind.Slack,
        _ => null,
    };
}

/// <summary>Org-level chat (Slack) connection — secrets by-ref only.</summary>
public sealed record ChatConnection(
    Guid Id,
    Guid? OrgId,
    ChatKind Kind,
    string TeamId,
    string BotTokenSecretRef,
    string SigningSecretRef,
    string? BotUserId,
    string? DefaultChannelId,
    string? CreatedByUserId,
    DateTimeOffset CreatedAt);

/// <summary>Canal → repo/automation mapping (SPEC-54 RF-004/RF-005).</summary>
public sealed record ChatChannelConfig(
    Guid Id,
    Guid ConnectionId,
    string ChannelId,
    string? SelectedRepository,
    string? SelectedBranch,
    bool AutoTriage,
    DateTimeOffset UpdatedAt);

/// <summary>Resultado do pipeline de evento/comando inbound.</summary>
public enum ChatOpsResult
{
    /// <summary>200 — processado (run iniciada ou mensagem roteada).</summary>
    Processed,

    /// <summary>200 — `url_verification` respondida.</summary>
    Challenge,

    /// <summary>401 — assinatura/timestamp inválidos.</summary>
    InvalidSignature,

    /// <summary>404 — sem connection para o team.</summary>
    NoConnection,

    /// <summary>200 — entrega duplicada (event_id).</summary>
    Duplicate,

    /// <summary>200 — nada a fazer (bot message, canal não configurado).</summary>
    Ignored,

    /// <summary>200 — bloqueado pelo input guard (SPEC-59).</summary>
    Blocked,
}

/// <summary>
/// Inbound chat pipeline (SPEC-54 RF-001/RF-002): verificação
/// <c>X-Slack-Signature</c> v0 + timestamp ≤5min → idempotência
/// <c>event_id</c> → thread binding → start-task/send-message.
/// </summary>
public interface IChatOpsService
{
    /// <summary>Events API. Retorna (result, challenge) — challenge só
    /// em url_verification.</summary>
    Task<(ChatOpsResult Result, string? Challenge)> HandleEventAsync(
        string? signature, string? timestamp, byte[] payload,
        CancellationToken ct);

    /// <summary>Slash command <c>/openhandsnet …</c> (form-encoded;
    /// <paramref name="rawBody"/> é o corpo bruto p/ verificação).</summary>
    Task<(ChatOpsResult Result, string? ReplyText)> HandleCommandAsync(
        string? signature, string? timestamp, byte[] rawBody,
        IReadOnlyDictionary<string, string> form, CancellationToken ct);

    /// <summary>Block actions (confirm/cancel de ask_user);
    /// <paramref name="payload"/> = JSON do campo form <c>payload</c>.</summary>
    Task<ChatOpsResult> HandleInteractionAsync(
        string? signature, string? timestamp, byte[] rawBody,
        byte[] payload, CancellationToken ct);
}

/// <summary>
/// Postback para o chat (SPEC-54 RF-003): mensagens, updates, reactions
/// e status do thread. Saída passa por <c>SecretRedactor</c> no caller.
/// </summary>
public interface IChatSink
{
    /// <summary><c>chat.postMessage</c>; retorna o ts da mensagem.</summary>
    Task<string?> PostMessageAsync(
        ChatConnection connection, string channelId, string? threadTs,
        string text, CancellationToken ct);

    /// <summary><c>chat.update</c> de uma mensagem postada.</summary>
    Task UpdateMessageAsync(
        ChatConnection connection, string channelId, string ts,
        string text, CancellationToken ct);

    /// <summary><c>reactions.add</c> (ack 👀 / done ✅).</summary>
    Task AddReactionAsync(
        ChatConnection connection, string channelId, string ts,
        string emoji, CancellationToken ct);

    /// <summary><c>assistant.threads.setStatus</c> (quando disponível).</summary>
    Task SetStatusAsync(
        ChatConnection connection, string channelId, string threadTs,
        string status, CancellationToken ct);
}

/// <summary>Admin CRUD de chat_connections + channel configs.</summary>
public interface IChatConnectionService
{
    /// <summary>Cria connection (bot token + signing secret por-ref).</summary>
    Task<ChatConnection> CreateAsync(
        Guid orgId, string teamId, string botTokenSecretRef,
        string signingSecretRef, string? botUserId,
        string? defaultChannelId, string? createdByUserId,
        CancellationToken ct);

    /// <summary>Lista connections da org.</summary>
    Task<IReadOnlyList<ChatConnection>> ListAsync(
        Guid orgId, CancellationToken ct);

    /// <summary>Obtém por id.</summary>
    Task<ChatConnection?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Resolve connection pelo Slack team id.</summary>
    Task<ChatConnection?> FindByTeamAsync(
        string teamId, CancellationToken ct);

    /// <summary>Remove (false = não encontrada).</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>Upsert do canal→repo/autotriage (RF-004/RF-005).</summary>
    Task<ChatChannelConfig> SetChannelConfigAsync(
        Guid connectionId, string channelId,
        string? selectedRepository, string? selectedBranch,
        bool autoTriage, CancellationToken ct);

    /// <summary>Config do canal (null = não configurado).</summary>
    Task<ChatChannelConfig?> GetChannelConfigAsync(
        Guid connectionId, string channelId, CancellationToken ct);

    /// <summary>Configs de uma connection.</summary>
    Task<IReadOnlyList<ChatChannelConfig>> ListChannelConfigsAsync(
        Guid connectionId, CancellationToken ct);
}

/// <summary>
/// Reporta conclusão de run num thread bound (SPEC-54 RF-003):
/// ✅ reaction + resumo. Fire-and-forget — falhas só logadas.
/// </summary>
public interface IChatOpsReporter
{
    /// <summary>Posta resultado da run no thread bound à conversa.</summary>
    Task ReportRunFinishedAsync(
        Guid conversationId, bool success, string? summary,
        CancellationToken ct);
}