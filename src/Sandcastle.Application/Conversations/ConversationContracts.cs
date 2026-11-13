using Sandcastle.Domain.Conversations;

namespace Sandcastle.Application.Conversations;

/// <summary>Result of a conversation create (the async start task).</summary>
public sealed record CreateConversationRequest(
    InitialMessage? InitialMessage,
    string? SelectedRepository,
    string? AgentType,
    string? LlmModel,
    Guid? SandboxSpecId,
    bool ConfirmationMode = false,
    string? SandboxProfile = null,
    IReadOnlyList<Agent.AgentPermissionRule>? Permissions = null,
    string? Mode = null,
    IReadOnlyList<string>? Secrets = null,
    // SPEC-146 RF-001: multi-repo — ordered, primary first (==
    // SelectedRepository when both present). null/empty = single repo.
    IReadOnlyList<string>? SelectedRepositories = null,
    // SPEC-150: JSON de HandoffMeta — conversa criada via
    // POST /conversations/handoff (patch aplicado no provisioning).
    string? HandoffMetaJson = null);

/// <summary>
/// SPEC-146 RF-001: <c>MultiRepo</c> options — <c>MaxRepos</c> is a hard cap
/// of <see cref="MultiRepoOptions.HardMaxRepos"/>; config can only lower it.
/// </summary>
public sealed class MultiRepoOptions
{
    /// <summary>Teto duro — sem bypass por config (guardrail SPEC-146 §9).</summary>
    public const int HardMaxRepos = 4;

    /// <summary>Config value (default 4) — clamped to [1, HardMaxRepos].</summary>
    public int MaxRepos { get; set; } = HardMaxRepos;

    /// <summary>Effective cap used by validation.</summary>
    public int EffectiveMaxRepos => Math.Clamp(MaxRepos, 1, HardMaxRepos);
}

/// <summary>
/// SPEC-146: invalid <c>selected_repository</c>/<c>selected_repositories</c>
/// combination — mapped to HTTP 400 by the API layer.
/// </summary>
public sealed class RepositorySelectionException(string detail)
    : ArgumentException(detail);

/// <summary>
/// SPEC-146 RF-001: normaliza a seleção de repositórios da conversa —
/// <c>selected_repositories</c> ordenado com o primário primeiro
/// (== <c>selected_repository</c> quando ambos presentes), trim, não-vazio,
/// dedup case-insensitive, cap <c>MaxRepos</c>. Retorna a lista ordenada
/// (vazia quando nada foi selecionado).
/// </summary>
public static class RepositorySelection
{
    /// <summary>Ordered repo list (primary at index 0) or throws.</summary>
    public static IReadOnlyList<string> Normalize(
        string? selectedRepository,
        IReadOnlyList<string>? selectedRepositories,
        int maxRepos)
    {
        var repos = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (selectedRepositories is not null)
        {
            foreach (var raw in selectedRepositories)
            {
                var r = raw?.Trim();
                if (string.IsNullOrEmpty(r))
                {
                    throw new RepositorySelectionException(
                        "selected_repositories entries must be non-empty");
                }
                if (!seen.Add(r))
                {
                    throw new RepositorySelectionException(
                        $"duplicate repository '{r}'");
                }
                repos.Add(r);
            }
        }

        var primary = string.IsNullOrWhiteSpace(selectedRepository)
            ? null : selectedRepository.Trim();
        if (primary is not null)
        {
            if (repos.Count == 0)
            {
                repos.Add(primary);
            }
            else if (!string.Equals(
                    repos[0], primary, StringComparison.OrdinalIgnoreCase))
            {
                throw new RepositorySelectionException(
                    "selected_repository must equal the first entry of "
                    + "selected_repositories (the primary repository)");
            }
        }

        if (repos.Count > maxRepos)
        {
            throw new RepositorySelectionException(
                $"too many repositories ({repos.Count} > max {maxRepos})");
        }

        return repos;
    }
}

/// <summary>
/// User/agent message seed for a new conversation. <paramref name="Run"/>
/// controls whether the agent loop starts right after READY.
/// </summary>
public sealed record InitialMessage(
    string Role, IReadOnlyList<object> Content, bool Run = true);

/// <summary>Paged search over conversations. SPEC-44:
/// <c>Attention=true</c> filtra só conversas com
/// <c>NeedsAttentionKind != null</c> ("Aguardando você").</summary>
public sealed record ConversationQuery(
    string? TitleEq = null,
    DateTimeOffset? CreatedAtGte = null,
    string? PageId = null,
    int Limit = 100,
    bool Attention = false);

/// <summary>Paged result (cursor semantics equal to EventPage).</summary>
public sealed record ConversationPage(
    IReadOnlyList<AppConversation> Items, string? NextPageId);

/// <summary>Conversation aggregate service (CRUD + state).</summary>
public interface IAppConversationService
{
    /// <summary>Creates the conversation record + enqueues its start task.</summary>
    Task<AppConversationStartTask> CreateAsync(
        CreateConversationRequest request, CancellationToken ct);

    /// <summary>
    /// SPEC-47: creates the conversation as an explicit owner — used by
    /// automation firing outside an HTTP request (same plan enforcement).
    /// </summary>
    Task<AppConversationStartTask> CreateForUserAsync(
        Guid userId, Guid? orgId, CreateConversationRequest request,
        CancellationToken ct);

    Task<ConversationPage> SearchAsync(ConversationQuery query, CancellationToken ct);

    Task<long> CountAsync(ConversationQuery query, CancellationToken ct);

    Task<IReadOnlyList<AppConversation>> GetBatchAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct);

    Task<AppConversation?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Patches title/tags/pinned.</summary>
    Task<AppConversation?> UpdateAsync(
        Guid id, string? title, IReadOnlyList<string>? tags, bool? pinned,
        CancellationToken ct);

    /// <summary>Archives the conversation and deletes its sandbox.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Validates that a user message can be sent now.
    /// Returns the upstream error key or <see langword="null"/> when OK.
    /// </summary>
    Task<SendMessageError?> ValidateSendMessageAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// SPEC-18 RF-005: changes the sandbox profile (<paramref name="profile"/>
    /// null/empty clears). A running sandbox is deleted and a new start-task
    /// recreates it with the new profile (host workspace survives).
    /// </summary>
    Task<SwitchResult> SwitchProfileAsync(
        Guid id, string? profile, CancellationToken ct);

    /// <summary>
    /// SPEC-18 RF-006: changes the LLM model used on the next run
    /// (upstream <c>switch_acp_model</c> route kept for parity).
    /// </summary>
    Task<SwitchResult> SwitchModelAsync(Guid id, string model, CancellationToken ct);

    /// <summary>
    /// SPEC-29 RF-002: replaces the conversation tool-permission rules
    /// (<paramref name="rules"/> null/empty clears the override).
    /// </summary>
    Task<AppConversation?> SetPermissionsAsync(Guid id,
        IReadOnlyList<Agent.AgentPermissionRule>? rules, CancellationToken ct);

    /// <summary>
    /// SPEC-29 RF-003: sets the execution mode (<c>plan</c>|<c>build</c>);
    /// <c>plan</c> denies mutation tools and asks on bash/browser.
    /// </summary>
    Task<AppConversation?> SetModeAsync(
        Guid id, string mode, CancellationToken ct);

    /// <summary>
    /// SPEC-44 RF-002: conversations que aguardam o usuário
    /// (<c>NeedsAttentionKind != null</c>, exceto arquivadas) — mais
    /// recentes primeiro; excerpt = texto do evento pendente quando
    /// barato de derivar.
    /// </summary>
    Task<IReadOnlyList<AttentionItem>> ListAttentionAsync(
        int limit, CancellationToken ct);

    /// <summary>
    /// SPEC-44 RF-002: limpa a projeção de attention. Retorna o kind
    /// removido; <see cref="AttentionKinds.Confirmation"/> nunca é
    /// dismissável (a decisão real é exigida — SPEC-29) → retorna
    /// <see cref="AttentionDismissResult.Conflict"/> nesse caso.
    /// </summary>
    Task<AttentionDismissResult> DismissAttentionAsync(
        Guid id, CancellationToken ct);
}

/// <summary>SPEC-44: kinds de attention persistidos na projeção.</summary>
public static class AttentionKinds
{
    /// <summary>Permission ask pendente (<c>waiting_for_confirmation</c>).</summary>
    public const string Confirmation = "confirmation";

    /// <summary>Tool <c>ask_user</c> aguardando resposta.</summary>
    public const string Question = "question";

    /// <summary>Run terminou em erro e ainda não foi vista.</summary>
    public const string Failed = "failed";
}

/// <summary>SPEC-44: um item do inbox de attention.</summary>
public sealed record AttentionItem(
    Guid ConversationId,
    string? Title,
    string Kind,
    long SinceTicks,
    string? Excerpt);

/// <summary>Resultado do dismiss de attention.</summary>
public enum AttentionDismissResult
{
    /// <summary>Projeção limpa.</summary>
    Dismissed,

    /// <summary>404 — conversa inexistente.</summary>
    NotFound,

    /// <summary>409 — kind <c>confirmation</c> exige decisão real.</summary>
    Conflict,

    /// <summary>Nada a limpar (kind já null).</summary>
    Noop,
}

/// <summary>Outcome of a conversation switch operation (SPEC-18).</summary>
public sealed record SwitchResult(AppConversation? Conversation, SwitchError? Error)
{
    /// <summary>Successful switch.</summary>
    public static SwitchResult Ok(AppConversation c) => new(c, null);

    /// <summary>Failed switch.</summary>
    public static SwitchResult Fail(SwitchError e) => new(null, e);
}

/// <summary>Switch rejection reasons mapped to HTTP by the API layer.</summary>
public enum SwitchError
{
    /// <summary>404 — conversation missing or archived.</summary>
    NotFound,

    /// <summary>400 — unknown sandbox profile name.</summary>
    InvalidProfile,

    /// <summary>400 — model missing/blank.</summary>
    InvalidModel,

    /// <summary>409 — a run or sandbox (re)creation is in flight.</summary>
    RunActive,

    /// <summary>502 — sandbox teardown failed.</summary>
    SandboxError,
}

/// <summary>Upstream-aligned send-message rejection reasons (SPEC-04 RF-001).</summary>
public enum SendMessageError
{
    /// <summary>404 — conversation does not exist.</summary>
    NotFound,

    /// <summary>410 — archived/deleted.</summary>
    Gone,

    /// <summary>409 — sandbox not running (resume first).</summary>
    ConflictSandboxNotRunning,

    /// <summary>503 — sandbox in error.</summary>
    ServiceUnavailable,
}

/// <summary>Start-task tracking + enqueue channel for the worker.</summary>
public interface IStartTaskService
{
    /// <summary>Gets one start task.</summary>
    Task<AppConversationStartTask?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Batch fetch by ids.</summary>
    Task<IReadOnlyList<AppConversationStartTask>> GetBatchAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct);

    Task<IReadOnlyList<AppConversationStartTask>> SearchAsync(
        StartTaskStatus? status, string? pageId, int limit, CancellationToken ct);

    Task<long> CountAsync(StartTaskStatus? status, CancellationToken ct);

    /// <summary>Streams task updates until terminal (READY|ERROR) — SSE feed.</summary>
    IAsyncEnumerable<AppConversationStartTask> WatchAsync(
        Guid taskId, CancellationToken ct);

    /// <summary>Enqueues a task id for the background start worker.</summary>
    ValueTask EnqueueAsync(Guid taskId, CancellationToken ct);
}