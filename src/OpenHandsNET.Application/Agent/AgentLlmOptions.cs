namespace OpenHandsNET.Application.Agent;

/// <summary>
/// Agent/LLM configuration bound from the <c>AgentLlm</c> section
/// (<c>OPENHANDSNET_AgentLlm__*</c> env vars; the legacy upstream-style
/// <c>LLM__*</c> flat keys are mapped by the infrastructure registration).
/// </summary>
public sealed class AgentLlmOptions
{
    /// <summary><c>openai_compat</c> | <c>mock</c>. Empty = auto (mock only in oss/dev without a key).</summary>
    public string Provider { get; set; } = "";

    /// <summary>Model id (e.g. <c>gpt-5-mini</c>, <c>anthropic/claude-sonnet</c>).</summary>
    public string Model { get; set; } = "openhandsnet-agent";

    /// <summary>OpenAI-compatible endpoint (OpenAI, Azure, Ollama, LiteLLM, LLMRouter).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Provider API key — never logged nor sent to the sandbox.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Per-request timeout seconds.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Transient-failure retries (exponential backoff).</summary>
    public int MaxRetries { get; set; } = 1;

    /// <summary>ReAct loop bound (upstream <c>max_iterations</c>).</summary>
    public int MaxIterations { get; set; } = 250;

    /// <summary>Default per-tool timeout seconds.</summary>
    public int ToolTimeoutSeconds { get; set; } = 120;

    /// <summary>Long-running tool (bash) timeout seconds.</summary>
    public int BashToolTimeoutSeconds { get; set; } = 600;

    /// <summary>Confirmation-mode wait bound (minutes) before interrupt.</summary>
    public int ConfirmationTimeoutMinutes { get; set; } = 30;

    /// <summary>Allows <see cref="Provider"/>=<c>mock</c> outside oss/dev.</summary>
    public bool AllowMock { get; set; }

    /// <summary>
    /// JSON script consumed by the mock provider (dev/test only):
    /// <c>[{"text":"…"},{"tool":{"name":"bash","arguments":{"command":"ls"}}}]</c>.
    /// </summary>
    public string? MockScript { get; set; }

    /// <summary>
    /// Fallback context window (tokens) when the model/catalog does not
    /// advertise one (SPEC-22). <c>null</c> = unlimited (meter never trips).
    /// </summary>
    public int? DefaultContextWindow { get; set; } = 128000;

    /// <summary>
    /// Per-model window overrides (<c>{"model-id":tokens}</c>) — always win
    /// over discovery (SPEC-22/23).
    /// </summary>
    public Dictionary<string, int> ContextWindowOverrides { get; set; } = new();

    /// <summary>
    /// Model id promoted to on context overflow when its window is larger
    /// (SPEC-22 RF-006). <c>null</c> = compaction instead.
    /// </summary>
    public string? PromotionTarget { get; set; }

    /// <summary>Context compaction behaviour (SPEC-22).</summary>
    public CompactionOptions Compaction { get; set; } = new();
}

/// <summary>Compaction tuning (<c>AgentLlm:Compaction:*</c>).</summary>
public sealed class CompactionOptions
{
    /// <summary>Master switch (disabled ⇒ passthrough, MVP behaviour).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Trigger when tokens/window ≥ this fraction.</summary>
    public double ThresholdPercent { get; set; } = 0.75;

    /// <summary>Absolute token threshold — wins over percent when set.</summary>
    public int? ThresholdTokens { get; set; }

    /// <summary>Recent messages always kept verbatim in summarization.</summary>
    public int KeepLastTurns { get; set; } = 6;

    /// <summary>Newest tool results exempt from pruning.</summary>
    public int KeepRecentToolResults { get; set; } = 8;

    /// <summary>Tool results longer than this are pruned (chars).</summary>
    public int PruneMaxChars { get; set; } = 4000;

    /// <summary>Check the threshold inside the tool loop, not just at run start.</summary>
    public bool MidTurnEnabled { get; set; } = true;

    /// <summary>Emit the <c>context_state</c> ledger every N iterations.</summary>
    public int LedgerEveryIterations { get; set; } = 10;

    /// <summary>Model used for summaries (null = conversation model).</summary>
    public string? SummaryModel { get; set; }

    /// <summary>Max output tokens for the summarizer completion.</summary>
    public int SummaryMaxTokens { get; set; } = 1024;
}
