using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Alerts;

/// <summary>
/// Fonte de alertas da org (SPEC-151 RF-001): uma entrada por webhook
/// configurado (Sentry/PagerDuty/generic). O signing secret vive
/// por-referência via <c>ISecretStore</c> — nunca em claro na tabela.
/// </summary>
public sealed class AlertSourceRecord : IOrgScoped
{
    public required Guid Id { get; set; }
    public Guid? OrgId { get; set; }

    /// <summary><c>sentry</c> | <c>pagerduty</c> | <c>generic</c>.</summary>
    public required string Provider { get; set; }

    /// <summary>Nome amigável exibido na UI.</summary>
    public required string Name { get; set; }

    /// <summary>Nome do secret com o signing secret/shared token.</summary>
    public required string SecretRef { get; set; }

    /// <summary>
    /// JSON <c>{service|project: "org/repo"}</c> — mapeia o serviço do
    /// payload para o repositório investigado.
    /// </summary>
    public string? RepoMappingJson { get; set; }

    /// <summary>Fonte ativa — webhook rejeita com 404 quando false.</summary>
    public bool Enabled { get; set; }

    /// <summary>Opt-in: criar sessão de investigação automaticamente.</summary>
    public bool AutoCreateSession { get; set; }

    /// <summary>Menor severidade que dispara auto-sessão.</summary>
    public required string TriageSeverityMin { get; set; }

    public string? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Evento de alerta normalizado (SPEC-151 RF-001/RF-002): dedup por
/// <c>(SourceId, ExternalId)</c>, lifecycle <c>new → investigating →
/// pr_open → resolved</c> ou <c>dismissed</c>. Sem repo mapeado =
/// <c>unrouted</c> (Repository null) — roteável via PATCH.
/// </summary>
public sealed class AlertEventRecord : IOrgScoped
{
    public required Guid Id { get; set; }
    public Guid? OrgId { get; set; }
    public required Guid SourceId { get; set; }

    /// <summary>Provider herdado da source (denormalizado p/ filtro).</summary>
    public required string Provider { get; set; }

    /// <summary>Id do issue/incident no provider (dedup com SourceId).</summary>
    public required string ExternalId { get; set; }

    /// <summary><c>issue</c> | <c>incident</c> | <c>resolved</c>.</summary>
    public required string Kind { get; set; }
    public required string Title { get; set; }
    public string? Url { get; set; }

    /// <summary>Severidade canônica (critical|high|medium|low|info).</summary>
    public required string Severity { get; set; }
    public string? Service { get; set; }
    public string? Culprit { get; set; }

    /// <summary>Repo resolvido via mapping (null = unrouted).</summary>
    public string? Repository { get; set; }
    public string? LabelsJson { get; set; }

    /// <summary><c>first_seen</c> informado pelo provider (UTC).</summary>
    public DateTimeOffset FirstSeen { get; set; }

    /// <summary>new|investigating|pr_open|resolved|dismissed.</summary>
    public required string Status { get; set; }
    public Guid? SessionId { get; set; }
    public string? PrUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long CreatedAtTicks { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>EF config for <see cref="AlertSourceRecord"/>.</summary>
public sealed class AlertSourceRecordConfiguration
    : IEntityTypeConfiguration<AlertSourceRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AlertSourceRecord> b)
    {
        b.ToTable("alert_sources");
        b.HasKey(e => e.Id);
        b.Property(e => e.Provider).HasMaxLength(20).IsRequired();
        b.Property(e => e.Name).HasMaxLength(200).IsRequired();
        b.Property(e => e.SecretRef).HasMaxLength(200).IsRequired();
        b.Property(e => e.TriageSeverityMin).HasMaxLength(20).IsRequired();
        b.Property(e => e.CreatedByUserId).HasMaxLength(200);
        b.HasIndex(e => e.OrgId);
    }
}

/// <summary>EF config for <see cref="AlertEventRecord"/>.</summary>
public sealed class AlertEventRecordConfiguration
    : IEntityTypeConfiguration<AlertEventRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AlertEventRecord> b)
    {
        b.ToTable("alert_events");
        b.HasKey(e => e.Id);
        b.Property(e => e.Provider).HasMaxLength(20).IsRequired();
        b.Property(e => e.ExternalId).HasMaxLength(200).IsRequired();
        b.Property(e => e.Kind).HasMaxLength(20).IsRequired();
        b.Property(e => e.Title).HasMaxLength(500).IsRequired();
        b.Property(e => e.Url).HasMaxLength(1000);
        b.Property(e => e.Severity).HasMaxLength(20).IsRequired();
        b.Property(e => e.Service).HasMaxLength(300);
        b.Property(e => e.Culprit).HasMaxLength(500);
        b.Property(e => e.Repository).HasMaxLength(300);
        b.Property(e => e.Status).HasMaxLength(20).IsRequired();
        b.Property(e => e.PrUrl).HasMaxLength(1000);
        b.HasIndex(e => new { e.SourceId, e.ExternalId }).IsUnique();
        b.HasIndex(e => new { e.OrgId, e.Status });
        b.HasIndex(e => e.SessionId);
        b.HasIndex(e => e.CreatedAtTicks).IsDescending();
    }
}