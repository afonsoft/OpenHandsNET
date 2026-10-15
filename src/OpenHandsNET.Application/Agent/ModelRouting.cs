namespace OpenHandsNET.Application.Agent;

/// <summary>
/// Functional role a model plays in the agent pipeline (SPEC-23 RF-002):
/// <c>Worker</c> drives the ReAct loop, <c>Summarizer</c> produces compaction
/// summaries (SPEC-22), <c>Planner</c> is reserved for planning passes.
/// </summary>
public enum ModelRole
{
    /// <summary>Main conversation model (ReAct loop).</summary>
    Worker,

    /// <summary>Compaction/utility summaries — may be a cheaper model.</summary>
    Summarizer,

    /// <summary>Planning passes (reserved).</summary>
    Planner,

    /// <summary>Subagent loops (SPEC-28) — may route to a cheaper model.</summary>
    Subagent,
}

/// <summary>
/// Effective model chosen for one call (SPEC-23 RF-002):
/// resolved id, advertised provider/context window, the attempt ordinal for
/// this selection, and which model it fell back from.
/// </summary>
public sealed record ModelSelection(
    string Model,
    string? Provider,
    int? ContextWindow,
    int Attempt,
    string? FallbackFrom);

/// <summary>Error classification driving turn recovery (SPEC-23 RF-003).</summary>
public enum LlmErrorKind
{
    /// <summary>Prompt exceeded the model's context window.</summary>
    ContextOverflow,

    /// <summary>429 / rate-limit — retry with backoff, then fallback.</summary>
    RateLimited,

    /// <summary>5xx, timeout or transport error — retried then fallback.</summary>
    Transient,

    /// <summary>404 / model_not_found — fall back immediately.</summary>
    ModelUnavailable,

    /// <summary>401/403 — fatal, never retried.</summary>
    Auth,

    /// <summary>Unclassified failure.</summary>
    Unknown,
}

/// <summary>
/// Classified LLM call failure (SPEC-23 RF-003). The router reads
/// <see cref="Kind"/> to decide retry vs fallback vs abort.
/// </summary>
public class LlmCallException : Exception
{
    /// <summary>Classified failure kind.</summary>
    public LlmErrorKind Kind { get; }

    /// <summary>HTTP status when the gateway returned one.</summary>
    public int? StatusCode { get; }

    /// <summary>Creates a classified failure.</summary>
    public LlmCallException(
        LlmErrorKind kind, string message, int? statusCode = null,
        Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        StatusCode = statusCode;
    }
}

/// <summary>
/// Model catalog abstraction (SPEC-23 RF-001): discovery of models exposed
/// by the gateway (OmniRoute <c>GET /models</c>) with cached results.
/// </summary>
public interface IModelCatalog
{
    /// <summary>Models advertised by the gateway (cached; config overrides win).</summary>
    Task<IReadOnlyList<ModelInfo>> ListAsync(CancellationToken ct);

    /// <summary>Resolves id → <see cref="ModelInfo"/> (null = unknown).</summary>
    Task<ModelInfo?> GetAsync(string modelId, CancellationToken ct);

    /// <summary>
    /// Last synchronously-known snapshot for <paramref name="modelId"/>
    /// (null until the first successful <see cref="ListAsync"/>). Lets the
    /// token meter read discovered windows without async plumbing.
    /// </summary>
    ModelInfo? TryGetCached(string modelId);
}

/// <summary>
/// Per-role model selection and fallback chaining (SPEC-23 RF-002).
/// </summary>
public interface IModelRouter
{
    /// <summary>Effective model for <paramref name="role"/> in this conversation.</summary>
    Task<ModelSelection> SelectAsync(
        Guid conversationId, ModelRole role, string? preferredModel,
        CancellationToken ct);

    /// <summary>
    /// Next candidate after <paramref name="current"/> failed with
    /// <paramref name="kind"/> (fallback chain; null = exhausted).
    /// </summary>
    Task<ModelSelection?> NextFallbackAsync(
        ModelSelection current, LlmErrorKind kind, CancellationToken ct);
}
