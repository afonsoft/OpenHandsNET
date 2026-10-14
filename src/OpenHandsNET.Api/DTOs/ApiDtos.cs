using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenHandsNET.Api.DTOs;

// DTOs da API V1 — snake_case via JsonNamingPolicy global (paridade upstream).

/// <summary>POST /app-conversations body (paridade <c>AppConversationStartRequest</c>).</summary>
public sealed record CreateAppConversationRequest
{
    public InitialMessageDto? InitialMessage { get; init; }
    public string? SelectedRepository { get; init; }
    public string? AgentType { get; init; }
    public string? LlmModel { get; init; }
    public Guid? SandboxSpecId { get; init; }
    public Guid? SandboxId { get; init; }
    public string? SandboxProfile { get; init; }
    public bool ConfirmationMode { get; init; }
    public JsonElement? LlmConfig { get; init; }
    public JsonElement? ObservabilityConfig { get; init; }
}

/// <summary>initial_message.content[] item.</summary>
public sealed record InitialMessageDto
{
    public string Role { get; init; } = "user";
    public IReadOnlyList<JsonElement> Content { get; init; } = [];
    public bool Run { get; init; } = true;
}

/// <summary>AppConversationStartTask DTO.</summary>
public sealed record StartTaskDto
{
    public required string Id { get; init; }
    public required string Status { get; init; }
    public string? AppConversationId { get; init; }
    public string? SandboxId { get; init; }
    public string? Detail { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>AppConversation DTO (wire).</summary>
public sealed record AppConversationDto
{
    public required string Id { get; init; }
    public string? Title { get; init; }
    public string? SandboxId { get; init; }
    public string? SelectedRepository { get; init; }
    public string? AgentType { get; init; }
    public string? LlmModel { get; init; }
    public string? SandboxProfile { get; init; }
    public required string ExecutionStatus { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool Pinned { get; init; }
    public bool ConfirmationMode { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>POST .../send-message body.</summary>
public sealed record SendMessageRequest
{
    public string Role { get; init; } = "user";
    public IReadOnlyList<JsonElement> Content { get; init; } = [];
    public bool Run { get; init; } = true;
}

/// <summary>PATCH /app-conversations/{id} body.</summary>
public sealed record PatchConversationRequest
{
    public string? Title { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public bool? Pinned { get; init; }
}

/// <summary>POST .../switch_profile body (SPEC-18 RF-005).</summary>
public sealed record SwitchProfileRequest
{
    /// <summary>Profile name (<c>small|medium|large</c>) or null to clear.</summary>
    public string? Profile { get; init; }
}

/// <summary>POST .../switch_acp_model body (SPEC-18 RF-006).</summary>
public sealed record SwitchAcpModelRequest
{
    /// <summary>Model name for the next run.</summary>
    public string? Model { get; init; }
}

/// <summary>POST /sandboxes body.</summary>
public sealed record CreateSandboxRequest
{
    public Guid? SpecId { get; init; }
    public Guid? ConversationId { get; init; }
}

/// <summary>Sandbox DTO.</summary>
public sealed record SandboxDto
{
    public required string Id { get; init; }
    public required string SpecId { get; init; }
    public string? ConversationId { get; init; }
    public required string Status { get; init; }
    public string Pool { get; init; } = "cold";
    public string? PoolKey { get; init; }
    public string BlueprintStatus { get; init; } = "none";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>SandboxSpec DTO (wire).</summary>
public sealed record SandboxSpecDto
{
    public string? Id { get; init; }
    public required string Name { get; init; }
    public required string Image { get; init; }
    public int MemoryMb { get; init; } = 4096;
    public double CpuLimit { get; init; } = 2.0;
    public int TimeoutMinutes { get; init; } = 60;
    public Dictionary<string, string>? Env { get; init; }
    public bool MountWorkspace { get; init; } = true;
    public int PidsLimit { get; init; } = 512;
    public string? Profile { get; init; }
}

/// <summary>Secret metadata (value nunca trafega).</summary>
public sealed record SecretMetaDto
{
    public required string Name { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Webhook event injection body.</summary>
public sealed record WebhookEventRequest
{
    public required JsonElement Event { get; init; }
}

/// <summary>Pending message DTO.</summary>
public sealed record PendingMessageDto
{
    public required string Id { get; init; }
    public required string ConversationId { get; init; }
    public required string Content { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
