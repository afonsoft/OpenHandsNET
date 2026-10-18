namespace OpenHandsNET.Application.Sharing;

/// <summary>
/// Public read-only share link for a conversation (SPEC-56 RF-001).
/// The raw token is returned exactly once at creation — only its SHA-256
/// hash is persisted.
/// </summary>
public sealed record ShareLinkResult(
    string Url,
    string Token,
    DateTimeOffset? ExpiresAt);

/// <summary>Share status for a conversation (SPEC-56 RF-001).</summary>
public sealed record ShareStatusResult(
    bool Shared,
    string? Url,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Public conversation payload served under
/// <c>GET /api/v1/shared/{token}</c> — metadata + first page of filtered
/// events (SPEC-56 RF-001/RF-003).
/// </summary>
public sealed record SharedConversationPayload(
    string Title,
    string? SelectedRepository,
    string? AgentType,
    string ExecutionStatus,
    DateTimeOffset CreatedAt,
    IReadOnlyList<System.Text.Json.JsonElement> Items,
    string? NextPageId);

/// <summary>Cursor page of sanitized public events.</summary>
public sealed record SharedEventPage(
    IReadOnlyList<System.Text.Json.JsonElement> Items,
    string? NextPageId);

/// <summary>
/// Share link lifecycle + public read path + trajectory export
/// (SPEC-56). Public lookups are keyed by token hash — the share record
/// is intentionally not org-scoped (the token IS the credential).
/// </summary>
public interface IShareService
{
    /// <summary>
    /// Creates or returns the active share link for
    /// <paramref name="conversationId"/>. Throws
    /// <see cref="ShareForbiddenException"/> when the actor does not own
    /// the conversation or shares are disabled (instance config or org
    /// settings).
    /// </summary>
    Task<ShareLinkResult> CreateOrGetAsync(
        Guid conversationId, Guid actorUserId, CancellationToken ct);

    /// <summary>Revokes the active share link. False = none active.</summary>
    Task<bool> RevokeAsync(
        Guid conversationId, Guid actorUserId, CancellationToken ct);

    /// <summary>Current share status (actor must own the conversation).</summary>
    Task<ShareStatusResult> GetStatusAsync(
        Guid conversationId, Guid actorUserId, CancellationToken ct);

    /// <summary>
    /// Public read: resolves a token to conversation metadata + first
    /// page of filtered events. Null = invalid/expired/revoked token
    /// (indistinguishable — no enumeration).
    /// </summary>
    Task<SharedConversationPayload?> GetSharedAsync(
        string token, int limit, CancellationToken ct);

    /// <summary>Public event paging for a share token.</summary>
    Task<SharedEventPage?> GetSharedEventsAsync(
        string token, string? cursor, int limit, CancellationToken ct);
}

/// <summary>Shares disabled or actor is not allowed (SPEC-56 NFR).</summary>
public sealed class ShareForbiddenException(string message)
    : InvalidOperationException(message);

/// <summary>
/// Trajectory zip export (SPEC-56 RF-004). Streams to an output
/// <see cref="Stream"/> — never materializes on disk.
/// </summary>
public interface ITrajectoryExporter
{
    /// <summary>
    /// Writes <c>events.jsonl</c>, <c>metadata.json</c>,
    /// <c>workspace.diff</c> and <c>summary.md</c> as a zip to
    /// <paramref name="output"/>. Throws
    /// <see cref="ShareForbiddenException"/> when the actor may not read
    /// the conversation.
    /// </summary>
    Task WriteZipAsync(
        Guid conversationId, Guid actorUserId, Stream output,
        CancellationToken ct);
}
