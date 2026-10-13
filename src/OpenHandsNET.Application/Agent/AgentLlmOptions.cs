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
}
