using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace OpenHandsNET.Infrastructure.Persistence;

// Records do schema completo (SPEC-10 RF-001): entidades de controle cujos
// services chegam nas SPECs de domínio. Todos os payloads complexos ficam em
// colunas JSON (*Json) — snake_case via EFCore.NamingConventions.

/// <summary>Org-scoped row marker — global filter applies when multi-tenancy is on.</summary>
public interface IOrgScoped
{
    /// <summary>Owning organization (null = unscoped/global row).</summary>
    Guid? OrgId { get; set; }
}

/// <summary>Queued user message while the agent runs (upstream pending_messages).</summary>
public sealed class PendingMessageRecord
{
    public required Guid Id { get; set; }
    public required Guid ConversationId { get; set; }
    public required string Role { get; set; }
    public required string ContentJson { get; set; }
    public bool Run { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Encrypted secret — ciphertext only, never plaintext at rest.</summary>
public sealed class SecretRecord
{
    public required string Scope { get; set; }
    public required string OwnerId { get; set; }
    public required string Name { get; set; }
    public required string Ciphertext { get; set; }
    public string? Nonce { get; set; }
    public string? MetaJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>MCP server registration per owner (SPEC-07).</summary>
public sealed class McpServerRecord
{
    public required string OwnerId { get; set; }
    public required string Name { get; set; }
    public required string ConfigJson { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>Reusable skill document (SPEC-11).</summary>
public sealed class SkillRecord
{
    public required Guid Id { get; set; }
    public string? OwnerId { get; set; }
    public string? OrgId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string Content { get; set; }
    public string? TriggersJson { get; set; }
    public string? Source { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Skill resolved into a conversation at SETTING_UP_SKILLS (SPEC-11 RF-005).</summary>
public sealed class ConversationSkillRecord
{
    public required Guid ConversationId { get; set; }
    public required Guid SkillId { get; set; }
}

/// <summary>Lifecycle hook configuration (SPEC-11 RF-004).</summary>
public sealed class HookConfigRecord
{
    public required Guid Id { get; set; }
    public string? OwnerId { get; set; }
    public string? OrgId { get; set; }
    public required string Name { get; set; }
    public required string Event { get; set; }
    public required string Action { get; set; }
    public string? Payload { get; set; }
    public bool Blocking { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Outbound webhook registration.</summary>
public sealed class WebhookRegistrationRecord
{
    public required Guid Id { get; set; }
    public string? OwnerId { get; set; }
    public Guid? ConversationId { get; set; }
    public required string Url { get; set; }
    public string? SecretCiphertext { get; set; }
    public string? KindsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Platform user (SPEC-08).</summary>
public sealed class UserRecord
{
    public required Guid Id { get; set; }
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
    public string? RolesJson { get; set; }
    public string? CurrentOrgId { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
    public string? IdpType { get; set; }
    public bool EmailVerified { get; set; }
    public DateTimeOffset? TosAcceptedAt { get; set; }
    public bool OnboardingDone { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>API key — hash-only at rest, optional org binding.</summary>
public sealed class ApiKeyRecord : IOrgScoped
{
    public required Guid Id { get; set; }
    public required string Prefix { get; set; }
    public required string UserId { get; set; }
    public Guid? OrgId { get; set; }
    public required string KeyHash { get; set; }
    public string? Name { get; set; }
    public string? ScopesJson { get; set; }
    public DateTimeOffset? NotBefore { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Organization (tenant).</summary>
public sealed class OrganizationRecord
{
    public required Guid Id { get; set; }
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public Guid? PlanId { get; set; }
    public DateTimeOffset? SuspendedAt { get; set; }
    public string? SettingsJson { get; set; }
    public int? QuotaDailyMax { get; set; }
    public int? QuotaConcurrentMax { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Org membership with role.</summary>
public sealed class OrgMemberRecord
{
    public required Guid OrgId { get; set; }
    public required Guid UserId { get; set; }
    public required string Role { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Pending org invitation token.</summary>
public sealed class OrgInvitationRecord
{
    public required Guid Id { get; set; }
    public required string Token { get; set; }
    public required Guid OrgId { get; set; }
    public required string Email { get; set; }
    public required string Role { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public long ExpiresAtTicks { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Org-scoped memory entry (SPEC-30 RF-001) — soft-deleted via
/// <see cref="DeletedAt"/>; writes are audit-logged by the service.
/// </summary>
public sealed class MemoryRecord
{
    public required Guid Id { get; set; }
    public required Guid OrgId { get; set; }
    public required string Key { get; set; }
    public required string Text { get; set; }
    public string? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Append-only security audit trail (SPEC-08).</summary>
public sealed class AuditEventRecord
{
    public required Guid Id { get; set; }
    public Guid? OrgId { get; set; }
    public string? UserId { get; set; }
    public required string Action { get; set; }
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public string? PayloadJson { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public long TimestampTicks { get; set; }
}

/// <summary>RFC 8628 device authorization session (SPEC-08 RF-009).</summary>
public sealed class DeviceCodeRecord
{
    public required Guid Id { get; set; }
    public required string DeviceCodeHash { get; set; }
    public required string UserCode { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public bool Denied { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public long ExpiresAtTicks { get; set; }
    public DateTimeOffset LastPolledAt { get; set; }
    public int PollCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Instance-level feature flag (SPEC-08 RF-010 admin surface).</summary>
public sealed class FeatureFlagRecord
{
    public required string Name { get; set; }
    public bool Enabled { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Billing plan catalog row (SPEC-08).</summary>
public sealed class PlanRecord
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public string? LimitsJson { get; set; }
    public decimal? MonthlyPriceUsd { get; set; }
}

/// <summary>Org subscription to a plan.</summary>
public sealed class SubscriptionRecord
{
    public required Guid Id { get; set; }
    public required Guid OrgId { get; set; }
    public required Guid PlanId { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}

/// <summary>Metered usage row per org/period.</summary>
public sealed class UsageRecord
{
    public required Guid Id { get; set; }
    public required Guid OrgId { get; set; }
    public required string Period { get; set; }
    public long TokensUsed { get; set; }
    public decimal? CostUsd { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}
