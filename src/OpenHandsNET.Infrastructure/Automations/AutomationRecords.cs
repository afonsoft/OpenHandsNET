using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Automations;

/// <summary>
/// SPEC-47 RF-001: agendamento de usuário — dispara uma conversa
/// (start-task) por agenda (<c>interval|daily|weekly|once</c>) ou por
/// webhook externo.
/// </summary>
public sealed class AutomationRecord : IOrgScoped
{
    /// <summary>PK.</summary>
    public required Guid Id { get; set; }
    /// <inheritdoc cref="IOrgScoped.OrgId" />
    public Guid? OrgId { get; set; }
    /// <summary>Dono — a conversa dispara com as credenciais dele.</summary>
    public required string CreatedByUserId { get; set; }
    /// <summary>Nome amigável.</summary>
    public required string Name { get; set; }
    /// <summary>Mensagem inicial da conversa disparada (≤8KB).</summary>
    public required string Prompt { get; set; }
    /// <summary>Override de modelo (null = default do usuário).</summary>
    public string? LlmModel { get; set; }
    /// <summary>Repo alvo opcional (mesmo campo do start-task).</summary>
    public string? SelectedRepository { get; set; }
    /// <summary><c>interval|daily|weekly|once</c>.</summary>
    public required string ScheduleKind { get; set; }
    /// <summary>HH:mm UTC (daily/weekly).</summary>
    public string? ScheduleTimeUtc { get; set; }
    /// <summary>Minutos entre disparos (interval; ≥5 na borda).</summary>
    public int IntervalMinutes { get; set; }
    /// <summary>DayOfWeek 0..6 (weekly).</summary>
    public int? Weekday { get; set; }
    /// <summary>Ligada/desligada (once desliga após disparar).</summary>
    public bool Enabled { get; set; }
    /// <summary>Próximo disparo UTC (null = sem agenda ativa).</summary>
    public DateTimeOffset? NextRunAt { get; set; }
    /// <summary>Espelho em ticks — SQLite não ordena DateTimeOffset.</summary>
    public long? NextRunAtTicks { get; set; }
    /// <summary>Último disparo efetivo.</summary>
    public DateTimeOffset? LastRunAt { get; set; }
    /// <summary>Criada em.</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Atualizada em.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Execução registrada de uma <see cref="AutomationRecord"/>.</summary>
public sealed class AutomationRunRecord
{
    /// <summary>PK.</summary>
    public required Guid Id { get; set; }
    /// <summary>FK lógica → <see cref="AutomationRecord"/>.</summary>
    public required Guid AutomationId { get; set; }
    /// <summary>Início do disparo.</summary>
    public DateTimeOffset StartedAt { get; set; }
    /// <summary>Espelho em ticks — SQLite não ordena DateTimeOffset.</summary>
    public long StartedAtTicks { get; set; }
    /// <summary>Fim (create da conversa retornou/falhou ou reconcile).</summary>
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary><c>running|succeeded|failed|skipped</c>.</summary>
    public required string Status { get; set; }
    /// <summary>Conversa criada (link p/ UI).</summary>
    public Guid? ConversationId { get; set; }
    /// <summary>Erro (quota, start-task negado, …).</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Webhook trigger anônimo — o token (256-bit) é exibido uma vez;
/// persistimos apenas o sha256 (SPEC-47 RF-003/NFR).
/// </summary>
public sealed class AutomationHookRecord
{
    /// <summary>PK.</summary>
    public required Guid Id { get; set; }
    /// <summary>FK lógica → <see cref="AutomationRecord"/>.</summary>
    public required Guid AutomationId { get; set; }
    /// <summary>SHA-256 hex do token de disparo.</summary>
    public required string TokenHash { get; set; }
    /// <summary>Criado em.</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Último disparo bem-sucedido.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <inheritdoc />
public sealed class AutomationRecordConfiguration
    : IEntityTypeConfiguration<AutomationRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AutomationRecord> b)
    {
        b.ToTable("automations");
        b.HasKey(e => e.Id);
        // scheduler varre (Enabled, NextRunAtTicks) a cada tick.
        b.HasIndex(e => new { e.Enabled, e.NextRunAtTicks });
        b.HasIndex(e => e.CreatedByUserId);
    }
}

/// <inheritdoc />
public sealed class AutomationRunRecordConfiguration
    : IEntityTypeConfiguration<AutomationRunRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AutomationRunRecord> b)
    {
        b.ToTable("automation_runs");
        b.HasKey(e => e.Id);
        b.HasIndex(e => new { e.AutomationId, e.StartedAtTicks });
        b.HasIndex(e => e.Status);
    }
}

/// <inheritdoc />
public sealed class AutomationHookRecordConfiguration
    : IEntityTypeConfiguration<AutomationHookRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AutomationHookRecord> b)
    {
        b.ToTable("automation_hooks");
        b.HasKey(e => e.Id);
        b.HasIndex(e => e.AutomationId);
        b.HasIndex(e => e.TokenHash).IsUnique();
    }
}
