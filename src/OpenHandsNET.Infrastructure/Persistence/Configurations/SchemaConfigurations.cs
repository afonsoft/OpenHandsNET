using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace OpenHandsNET.Infrastructure.Persistence.Configurations;

/// <summary>Table/index mappings for the SPEC-10 control entities.</summary>
public sealed class PendingMessageConfiguration : IEntityTypeConfiguration<PendingMessageRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PendingMessageRecord> b)
    {
        b.ToTable("pending_messages");
        b.HasKey(e => e.Id);
        b.HasIndex(e => new { e.ConversationId, e.CreatedAt });
    }
}

/// <inheritdoc />
public sealed class SecretConfiguration : IEntityTypeConfiguration<SecretRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SecretRecord> b)
    {
        b.ToTable("secrets");
        b.HasKey(e => new { e.Scope, e.OwnerId, e.Name });
    }
}

/// <inheritdoc />
public sealed class McpServerConfiguration : IEntityTypeConfiguration<McpServerRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<McpServerRecord> b)
    {
        b.ToTable("mcp_servers");
        b.HasKey(e => new { e.OwnerId, e.Name });
    }
}

/// <inheritdoc />
public sealed class SkillConfiguration : IEntityTypeConfiguration<SkillRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SkillRecord> b)
    {
        b.ToTable("skills");
        b.HasKey(e => e.Id);
        b.HasIndex(e => new { e.OwnerId, e.Name });
    }
}

/// <inheritdoc />
public sealed class ConversationSkillConfiguration
    : IEntityTypeConfiguration<ConversationSkillRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ConversationSkillRecord> b)
    {
        b.ToTable("conversation_skills");
        b.HasKey(e => new { e.ConversationId, e.SkillId });
    }
}

/// <inheritdoc />
public sealed class HookConfigConfiguration : IEntityTypeConfiguration<HookConfigRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<HookConfigRecord> b)
    {
        b.ToTable("hook_configs");
        b.HasKey(e => e.Id);
        b.HasIndex(e => new { e.OwnerId, e.Event });
    }
}

/// <inheritdoc />
public sealed class WebhookRegistrationConfiguration
    : IEntityTypeConfiguration<WebhookRegistrationRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<WebhookRegistrationRecord> b)
    {
        b.ToTable("webhook_registrations");
        b.HasKey(e => e.Id);
        b.HasIndex(e => e.OwnerId);
        b.HasIndex(e => e.ConversationId);
    }
}

/// <inheritdoc />
public sealed class UserConfiguration : IEntityTypeConfiguration<UserRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UserRecord> b)
    {
        b.ToTable("users");
        b.HasKey(e => e.Id);
        b.HasIndex(e => e.Email).IsUnique();
    }
}

/// <inheritdoc />
public sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKeyRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ApiKeyRecord> b)
    {
        b.ToTable("api_keys");
        b.HasKey(e => e.Id);
        b.HasIndex(e => e.Prefix);
        b.HasIndex(e => e.UserId);
        b.HasIndex(e => e.OrgId);
    }
}

/// <inheritdoc />
public sealed class OrganizationConfiguration : IEntityTypeConfiguration<OrganizationRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<OrganizationRecord> b)
    {
        b.ToTable("organizations");
        b.HasKey(e => e.Id);
        b.HasIndex(e => e.Slug).IsUnique();
    }
}

/// <inheritdoc />
public sealed class OrgMemberConfiguration : IEntityTypeConfiguration<OrgMemberRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<OrgMemberRecord> b)
    {
        b.ToTable("org_members");
        b.HasKey(e => new { e.OrgId, e.UserId });
        b.HasIndex(e => e.UserId);
    }
}

/// <inheritdoc />
public sealed class OrgInvitationConfiguration : IEntityTypeConfiguration<OrgInvitationRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<OrgInvitationRecord> b)
    {
        b.ToTable("org_invitations");
        b.HasKey(e => e.Id);
        b.HasIndex(e => e.Token).IsUnique();
        b.HasIndex(e => new { e.OrgId, e.Email });
    }
}

/// <inheritdoc />
public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEventRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AuditEventRecord> b)
    {
        b.ToTable("audit_events");
        b.HasKey(e => e.Id);
        b.HasIndex(e => new { e.OrgId, e.Timestamp });
        b.HasIndex(e => new { e.UserId, e.Timestamp });
        b.HasIndex(e => e.Action);
    }
}

/// <inheritdoc />
public sealed class MemoryConfiguration : IEntityTypeConfiguration<MemoryRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MemoryRecord> b)
    {
        b.ToTable("memory_records");
        b.HasKey(e => e.Id);
        b.Property(e => e.Key).HasMaxLength(200);
        b.HasIndex(e => new { e.OrgId, e.Key });
        b.HasIndex(e => e.UpdatedAt);
    }
}

/// <inheritdoc />
public sealed class DeviceCodeConfiguration : IEntityTypeConfiguration<DeviceCodeRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DeviceCodeRecord> b)
    {
        b.ToTable("device_codes");
        b.HasKey(e => e.Id);
        b.HasIndex(e => e.DeviceCodeHash).IsUnique();
        b.HasIndex(e => e.UserCode).IsUnique();
        b.HasIndex(e => e.ExpiresAt);
    }
}

/// <inheritdoc />
public sealed class FeatureFlagConfiguration : IEntityTypeConfiguration<FeatureFlagRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<FeatureFlagRecord> b)
    {
        b.ToTable("feature_flags");
        b.HasKey(e => e.Name);
    }
}

/// <inheritdoc />
public sealed class PlanConfiguration : IEntityTypeConfiguration<PlanRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PlanRecord> b) => b.ToTable("plans");
}

/// <inheritdoc />
public sealed class SubscriptionConfiguration : IEntityTypeConfiguration<SubscriptionRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SubscriptionRecord> b)
    {
        b.ToTable("subscriptions");
        b.HasIndex(e => e.OrgId);
    }
}

/// <inheritdoc />
public sealed class UsageRecordConfiguration : IEntityTypeConfiguration<UsageRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UsageRecord> b)
    {
        b.ToTable("usage_records");
        b.HasIndex(e => new { e.OrgId, e.Period });
    }
}

/// <inheritdoc />
public sealed class ByorKeyConfiguration : IEntityTypeConfiguration<ByorKeyRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ByorKeyRecord> b)
    {
        b.ToTable("byor_keys");
        b.HasKey(e => e.Id);
        b.HasIndex(e => e.UserId)
            .IsUnique()
            .HasFilter("revoked_at IS NULL");
        b.HasIndex(e => e.KeyId);
    }
}

/// <inheritdoc />
public sealed class RepoIndexConfiguration : IEntityTypeConfiguration<RepoIndexRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RepoIndexRecord> b)
    {
        b.ToTable("repo_indexes");
        b.HasKey(e => e.Id);
        b.HasIndex(e => new { e.OrgId, e.Repository, e.Branch });
    }
}

/// <inheritdoc />
public sealed class CodeUnitConfiguration : IEntityTypeConfiguration<CodeUnitRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CodeUnitRecord> b)
    {
        b.ToTable("code_units");
        b.HasKey(e => e.Id);
        b.HasIndex(e => e.RepoIndexId);
        b.HasIndex(e => new { e.RepoIndexId, e.Path });
    }
}
