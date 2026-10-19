using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace OpenHandsNET.Infrastructure.Sharing;

/// <summary>
/// Persisted public share link (SPEC-56 RF-001). Only the SHA-256 hash of
/// the token is stored — the token itself is the credential and is shown
/// once at creation.
/// </summary>
public sealed class ConversationShareRecord
{
    public required Guid Id { get; set; }
    public required Guid ConversationId { get; set; }

    /// <summary>SHA-256 hex of the 256-bit share token.</summary>
    public required string TokenHash { get; set; }
    public string? CreatedByUserId { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long CreatedAtTicks { get; set; }

    /// <summary>
    /// Espelho numérico de <see cref="ExpiresAt"/> — SQLite não compara
    /// DateTimeOffset; sincronizado no SaveChanges (ver AppDbContext).
    /// </summary>
    public long? ExpiresAtTicks { get; set; }
}

/// <summary>EF config for <see cref="ConversationShareRecord"/>.</summary>
public sealed class ConversationShareRecordConfiguration
    : IEntityTypeConfiguration<ConversationShareRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ConversationShareRecord> builder)
    {
        builder.ToTable("conversation_shares");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.TokenHash).HasMaxLength(64).IsRequired();
        builder.Property(e => e.CreatedByUserId).HasMaxLength(200);
        builder.HasIndex(e => e.TokenHash).IsUnique();
        builder.HasIndex(e => e.ConversationId);
    }
}