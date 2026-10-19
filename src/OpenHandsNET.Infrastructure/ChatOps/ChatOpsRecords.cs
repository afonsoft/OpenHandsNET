using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.ChatOps;

/// <summary>
/// Workspace Slack conectado à org (SPEC-54 RF-001). Bot token e
/// signing secret vivem por-referência via <c>ISecretStore</c>.
/// </summary>
public sealed class ChatConnectionRecord : IOrgScoped
{
    public required Guid Id { get; set; }
    public Guid? OrgId { get; set; }

    /// <summary><c>slack</c>.</summary>
    public required string Kind { get; set; }

    /// <summary>Slack <c>team_id</c> (T…).</summary>
    public required string TeamId { get; set; }

    /// <summary>Nome do secret com o bot token (xoxb-…).</summary>
    public required string BotTokenSecretRef { get; set; }

    /// <summary>Nome do secret com o signing secret.</summary>
    public required string SigningSecretRef { get; set; }

    /// <summary>User id do bot no workspace (resolvido no test).</summary>
    public string? BotUserId { get; set; }

    /// <summary>Canal default do workspace (fallback p/ config).</summary>
    public string? DefaultChannelId { get; set; }
    public string? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Thread → conversa (SPEC-54 RF-002): primeira menção cria a conversa;
/// replies seguintes viram send-message na mesma conversa.
/// </summary>
public sealed class ChatThreadBindingRecord
{
    public required Guid Id { get; set; }
    public required Guid ConnectionId { get; set; }
    public required string ChannelId { get; set; }

    /// <summary><c>thread_ts</c> (ou ts da própria mensagem).</summary>
    public required string ThreadTs { get; set; }
    public required Guid ConversationId { get; set; }

    /// <summary>Ts da mensagem de status postada no thread.</summary>
    public string? StatusMessageTs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Canal → repo/automation/auto-triage (RF-004/RF-005).</summary>
public sealed class ChatChannelConfigRecord
{
    public required Guid Id { get; set; }
    public required Guid ConnectionId { get; set; }
    public required string ChannelId { get; set; }
    public string? SelectedRepository { get; set; }
    public string? SelectedBranch { get; set; }
    public bool AutoTriage { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>EF config for <see cref="ChatConnectionRecord"/>.</summary>
public sealed class ChatConnectionRecordConfiguration
    : IEntityTypeConfiguration<ChatConnectionRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ChatConnectionRecord> b)
    {
        b.ToTable("chat_connections");
        b.HasKey(e => e.Id);
        b.Property(e => e.Kind).HasMaxLength(20).IsRequired();
        b.Property(e => e.TeamId).HasMaxLength(40).IsRequired();
        b.Property(e => e.BotTokenSecretRef).HasMaxLength(200).IsRequired();
        b.Property(e => e.SigningSecretRef).HasMaxLength(200).IsRequired();
        b.Property(e => e.BotUserId).HasMaxLength(40);
        b.Property(e => e.DefaultChannelId).HasMaxLength(40);
        b.Property(e => e.CreatedByUserId).HasMaxLength(200);
        b.HasIndex(e => e.TeamId).IsUnique();
        b.HasIndex(e => e.OrgId);
    }
}

/// <summary>EF config for <see cref="ChatThreadBindingRecord"/>.</summary>
public sealed class ChatThreadBindingRecordConfiguration
    : IEntityTypeConfiguration<ChatThreadBindingRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ChatThreadBindingRecord> b)
    {
        b.ToTable("chat_thread_bindings");
        b.HasKey(e => e.Id);
        b.Property(e => e.ChannelId).HasMaxLength(40).IsRequired();
        b.Property(e => e.ThreadTs).HasMaxLength(40).IsRequired();
        b.Property(e => e.StatusMessageTs).HasMaxLength(40);
        b.HasIndex(e => new { e.ConnectionId, e.ChannelId, e.ThreadTs })
            .IsUnique();
        b.HasIndex(e => e.ConversationId);
    }
}

/// <summary>EF config for <see cref="ChatChannelConfigRecord"/>.</summary>
public sealed class ChatChannelConfigRecordConfiguration
    : IEntityTypeConfiguration<ChatChannelConfigRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ChatChannelConfigRecord> b)
    {
        b.ToTable("chat_channel_configs");
        b.HasKey(e => e.Id);
        b.Property(e => e.ChannelId).HasMaxLength(40).IsRequired();
        b.Property(e => e.SelectedRepository).HasMaxLength(200);
        b.Property(e => e.SelectedBranch).HasMaxLength(200);
        b.HasIndex(e => new { e.ConnectionId, e.ChannelId }).IsUnique();
    }
}