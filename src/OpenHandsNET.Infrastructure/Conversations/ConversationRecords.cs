using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenHandsNET.Infrastructure.Persistence;
using OpenHandsNET.Domain.Conversations;

namespace OpenHandsNET.Infrastructure.Conversations;

/// <summary>Persisted conversation row.</summary>
public sealed class ConversationRecord : IOrgScoped
{
    public required Guid Id { get; set; }
    public string? Title { get; set; }
    public Guid? SandboxId { get; set; }
    public Guid? OrgId { get; set; }
    public string? SelectedRepository { get; set; }
    public string? AgentType { get; set; }
    public string? LlmModel { get; set; }
    public required string ExecutionStatus { get; set; }
    public string? TagsJson { get; set; }
    public bool Pinned { get; set; }
    public bool ConfirmationMode { get; set; }

    /// <summary>Agente deve iniciar ao atingir READY (initial_message.run).</summary>
    public bool PendingKickoff { get; set; }
    public string? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public AppConversation ToDomain() => new(
        Id, Title, SandboxId, SelectedRepository, AgentType, LlmModel,
        Enum.Parse<ConversationExecutionStatus>(ExecutionStatus, ignoreCase: true),
        TagsJson is null
            ? []
            : System.Text.Json.JsonSerializer.Deserialize<List<string>>(TagsJson) ?? [],
        Pinned, CreatedAt, UpdatedAt, CreatedByUserId, ConfirmationMode);
}

/// <summary>Persisted start-task row.</summary>
public sealed class StartTaskRecord
{
    public required Guid Id { get; set; }
    public required string Status { get; set; }
    public Guid? AppConversationId { get; set; }
    public Guid? SandboxId { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public AppConversationStartTask ToDomain() => new(
        Id, Enum.Parse<StartTaskStatus>(Status, ignoreCase: true),
        AppConversationId, SandboxId, Detail, CreatedAt, UpdatedAt);
}

/// <summary>EF config for <see cref="ConversationRecord"/>.</summary>
public sealed class ConversationRecordConfiguration : IEntityTypeConfiguration<ConversationRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ConversationRecord> builder)
    {
        builder.ToTable("conversations");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Title).HasMaxLength(500);
        builder.Property(e => e.ExecutionStatus).HasMaxLength(20).IsRequired();
        builder.HasIndex(e => e.CreatedAt);
        builder.HasIndex(e => e.ExecutionStatus);
    }
}

/// <summary>EF config for <see cref="StartTaskRecord"/>.</summary>
public sealed class StartTaskRecordConfiguration : IEntityTypeConfiguration<StartTaskRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<StartTaskRecord> builder)
    {
        builder.ToTable("conversation_start_tasks");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Status).HasMaxLength(30).IsRequired();
        builder.HasIndex(e => e.Status);
    }
}
