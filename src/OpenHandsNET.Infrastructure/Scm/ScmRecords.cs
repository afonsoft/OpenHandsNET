using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Scm;

/// <summary>
/// Org-level SCM connection (SPEC-52 RF-002). PEM e webhook secret vivem
/// como referências (<see cref="PemSecretRef"/>/<see cref="WebhookSecretRef"/>)
/// resolvidas via <c>ISecretStore</c> — nunca em claro no DB.
/// </summary>
public sealed class ScmConnectionRecord : IOrgScoped
{
    public required Guid Id { get; set; }
    public Guid? OrgId { get; set; }

    /// <summary><c>github</c> (gitlab|bitbucket|azuredevops — SPEC-53).</summary>
    public required string Kind { get; set; }

    /// <summary>GitHub App installation id (string p/ tolerar ids grandes).</summary>
    public string? InstallationId { get; set; }

    /// <summary>Nome do secret que guarda o PEM da app key.</summary>
    public string? PemSecretRef { get; set; }

    /// <summary>Nome do secret que guarda o webhook secret.</summary>
    public string? WebhookSecretRef { get; set; }

    /// <summary>JSON array de repos cobertos ("owner/repo"); vazio/null = todos da instalação.</summary>
    public string? ReposJson { get; set; }

    /// <summary>GitHub App id (manifest flow / BYO app).</summary>
    public string? AppId { get; set; }

    /// <summary>Slug público do App (UI/links).</summary>
    public string? AppSlug { get; set; }

    /// <summary>Nome do secret que guarda o client_secret do App (manifest flow).</summary>
    public string? ClientSecretRef { get; set; }

    /// <summary>Usuário que registrou a conexão (atribuição de triggers).</summary>
    public string? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Entrega de webhook já processada — idempotência por
/// <see cref="DeliveryId"/> único (SPEC-52 RF-004).
/// </summary>
public sealed class WebhookDeliveryRecord
{
    public required Guid Id { get; set; }

    /// <summary><c>X-GitHub-Delivery</c> (ou equivalente do provider).</summary>
    public required string DeliveryId { get; set; }

    /// <summary><c>github</c> (providers futuros — SPEC-53).</summary>
    public required string Kind { get; set; }

    /// <summary>SHA-256 do payload bruto (dedup defensiva + auditoria).</summary>
    public string? PayloadSha { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}

/// <summary>EF config for <see cref="ScmConnectionRecord"/>.</summary>
public sealed class ScmConnectionRecordConfiguration
    : IEntityTypeConfiguration<ScmConnectionRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ScmConnectionRecord> builder)
    {
        builder.ToTable("scm_connections");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Kind).HasMaxLength(20).IsRequired();
        builder.Property(e => e.InstallationId).HasMaxLength(40);
        builder.Property(e => e.PemSecretRef).HasMaxLength(200);
        builder.Property(e => e.WebhookSecretRef).HasMaxLength(200);
        builder.Property(e => e.AppId).HasMaxLength(40);
        builder.Property(e => e.AppSlug).HasMaxLength(200);
        builder.Property(e => e.ClientSecretRef).HasMaxLength(200);
        builder.Property(e => e.CreatedByUserId).HasMaxLength(200);
        builder.HasIndex(e => new { e.OrgId, e.Kind });
    }
}

/// <summary>EF config for <see cref="WebhookDeliveryRecord"/>.</summary>
public sealed class WebhookDeliveryRecordConfiguration
    : IEntityTypeConfiguration<WebhookDeliveryRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<WebhookDeliveryRecord> builder)
    {
        builder.ToTable("webhook_deliveries");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.DeliveryId).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Kind).HasMaxLength(20).IsRequired();
        builder.Property(e => e.PayloadSha).HasMaxLength(64);
        builder.HasIndex(e => new { e.Kind, e.DeliveryId }).IsUnique();
    }
}
