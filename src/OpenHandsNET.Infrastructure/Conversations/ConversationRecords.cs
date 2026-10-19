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

    /// <summary>Override MCP da conversa (JSON de McpServerConfig[]).</summary>
    public string? McpConfigJson { get; set; }

    /// <summary>Perfil de sizing do sandbox (SPEC-14 RF-001): small|medium|large.</summary>
    public string? SandboxProfile { get; set; }

    /// <summary>
    /// Regras de permissão por tool da conversa (SPEC-29 RF-001) — JSON de
    /// <c>AgentPermissionRule[]</c>; null = herda org/instância.
    /// </summary>
    public string? PermissionsJson { get; set; }

    /// <summary>Modo de execução (SPEC-29 RF-003): <c>build</c> | <c>plan</c>.</summary>
    public string Mode { get; set; } = "build";

    /// <summary>
    /// Nomes de secrets do usuário a injetar no sandbox (SPEC-33 RF-003) —
    /// JSON array; os VALORES são resolvidos na borda (ISecretStore) e nunca
    /// serializados.
    /// </summary>
    public string? SecretNamesJson { get; set; }
    public string? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Projeção de attention (SPEC-44 RF-001): <c>null | confirmation |
    /// question | failed</c> — setada pelo runner nas transições de status,
    /// limpa na retomada/mensagem do usuário/dismiss. Projeção, não fonte
    /// de verdade (reconcilia no próximo transition).
    /// </summary>
    public string? NeedsAttentionKind { get; set; }

    /// <summary>Ticks UTC de quando a attention foi marcada.</summary>
    public long? NeedsAttentionAtTicks { get; set; }

    /// <summary>SPEC-52 RF-003: PR aberto pela conversa (url completa).</summary>
    public string? PullRequestUrl { get; set; }

    /// <summary>SPEC-52: número do PR no repo.</summary>
    public int? PullRequestNumber { get; set; }

    /// <summary>SPEC-52: estado do PR (open|closed|merged) — atualizado por webhook/status.</summary>
    public string? PullRequestState { get; set; }

    /// <summary>SPEC-52 RF-005: head SHA reportado no último commit status.</summary>
    public string? PullRequestHeadSha { get; set; }

    /// <summary>SPEC-55 RF-003: ticket que originou a conversa (linear|jira).</summary>
    public string? TicketKind { get; set; }
    /// <summary>SPEC-57: quem criou via impersonation (service_user:{id}…).</summary>
    public string? CreatedVia { get; set; }

    /// <summary>SPEC-55: identificador do ticket (ENG-123 / PROJ-42).</summary>
    public string? TicketId { get; set; }

    /// <summary>SPEC-55: URL do ticket.</summary>
    public string? TicketUrl { get; set; }

    /// <summary>SPEC-62 RF-001: ID da conversa coordenadora/pai em delegações cross-sandbox.</summary>
    public Guid? ParentConversationId { get; set; }

    /// <summary>SPEC-62 RF-001: Nível de profundidade da delegação (0 = raiz).</summary>
    public int DelegationDepth { get; set; } = 0;

    /// <summary>SPEC-62 RF-001: Metadados adicionais de delegação em JSON.</summary>
    public string? DelegationMetaJson { get; set; }

    /// <summary>
    /// Espelho numérico de <see cref="CreatedAt"/> (UtcTicks) — SQLite não
    /// ordena DateTimeOffset; sincronizado no SaveChanges (ver AppDbContext).
    /// </summary>
    public long CreatedAtTicks { get; set; }
    public long UpdatedAtTicks { get; set; }

    public AppConversation ToDomain() => new(
        Id, Title, SandboxId, SelectedRepository, AgentType, LlmModel,
        Enum.Parse<ConversationExecutionStatus>(ExecutionStatus, ignoreCase: true),
        TagsJson is null
            ? []
            : System.Text.Json.JsonSerializer.Deserialize<List<string>>(TagsJson) ?? [],
        Pinned, CreatedAt, UpdatedAt, CreatedByUserId, ConfirmationMode,
        SandboxProfile, NeedsAttentionKind,
        PullRequestUrl, PullRequestNumber, PullRequestState,
        TicketKind, TicketId, TicketUrl);
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
    public long CreatedAtTicks { get; set; }
    public long UpdatedAtTicks { get; set; }

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
        builder.Property(e => e.NeedsAttentionKind).HasMaxLength(20);
        builder.HasIndex(e => e.CreatedAtTicks);
        builder.HasIndex(e => e.ExecutionStatus);
        // SPEC-44 RF-001: count/list de attention baratos por org.
        builder.HasIndex(e => new { e.OrgId, e.NeedsAttentionAtTicks });
        builder.HasIndex(e => e.ParentConversationId);
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
