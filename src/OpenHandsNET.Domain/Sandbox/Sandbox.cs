namespace OpenHandsNET.Domain.Sandbox;

/// <summary>
/// A running (or tracked) sandbox container bound to a conversation.
/// <see cref="ContainerId"/> is the Docker id; <see cref="AgentUrl"/> points at
/// an agent-server inside the sandbox when it exists (phase 2+).
/// </summary>
public sealed record Sandbox(
    Guid Id,
    Guid SpecId,
    Guid? ConversationId,
    string? ContainerId,
    SandboxStatus Status,
    Uri? AgentUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    string? LastError,
    string Pool = "cold");
