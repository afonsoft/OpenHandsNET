using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using OpenHandsNET.Application.Tickets;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Tickets;

/// <summary>Linha de ticket_connection (SPEC-55).</summary>
public sealed class TicketConnectionRecord : IOrgScoped
{
    public required Guid Id { get; set; }
    public Guid? OrgId { get; set; }

    /// <summary>linear | jira.</summary>
    public required string Kind { get; set; }
    public required string ApiUrl { get; set; }
    public required string TokenSecretRef { get; set; }
    public string? WebhookSecretRef { get; set; }
    public string? WorkspaceId { get; set; }
    public string? TriggerConfigJson { get; set; }
    public string? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public TicketConnection ToModel() => new(
        Id, OrgId,
        TicketKindExtensions.Parse(Kind) ?? TicketKind.Linear,
        ApiUrl, TokenSecretRef, WebhookSecretRef, WorkspaceId,
        TriggerConfigJson, CreatedByUserId, CreatedAt);
}

/// <summary>Mapeamento projeto → repo/branch.</summary>
public sealed class TicketProjectConfigRecord
{
    public required Guid Id { get; set; }
    public required Guid ConnectionId { get; set; }
    public required string ProjectKey { get; set; }
    public string? SelectedRepository { get; set; }
    public string? SelectedBranch { get; set; }

    public TicketProjectConfig ToModel() => new(
        Id, ConnectionId, ProjectKey, SelectedRepository,
        SelectedBranch);
}

/// <summary>EF config.</summary>
public sealed class TicketConnectionRecordConfiguration
    : IEntityTypeConfiguration<TicketConnectionRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TicketConnectionRecord> b)
    {
        b.ToTable("ticket_connections");
        b.HasKey(e => e.Id);
        b.Property(e => e.Kind).HasMaxLength(20).IsRequired();
        b.Property(e => e.ApiUrl).HasMaxLength(500).IsRequired();
        b.Property(e => e.TokenSecretRef).HasMaxLength(200).IsRequired();
        b.Property(e => e.WebhookSecretRef).HasMaxLength(200);
        b.Property(e => e.WorkspaceId).HasMaxLength(200);
        b.HasIndex(e => new { e.Kind, e.WorkspaceId });
    }
}

/// <summary>EF config.</summary>
public sealed class TicketProjectConfigRecordConfiguration
    : IEntityTypeConfiguration<TicketProjectConfigRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TicketProjectConfigRecord> b)
    {
        b.ToTable("ticket_project_configs");
        b.HasKey(e => e.Id);
        b.Property(e => e.ProjectKey).HasMaxLength(100).IsRequired();
        b.Property(e => e.SelectedRepository).HasMaxLength(300);
        b.Property(e => e.SelectedBranch).HasMaxLength(300);
        b.HasIndex(e => new { e.ConnectionId, e.ProjectKey }).IsUnique();
    }
}