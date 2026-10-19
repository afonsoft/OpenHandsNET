using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace OpenHandsNET.Infrastructure.Eventing;

/// <summary>
/// EF Core mapping for the append-only <c>events</c> table (SPEC-02 RF-002).
/// </summary>
public sealed class EventRecordConfiguration : IEntityTypeConfiguration<EventRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<EventRecord> builder)
    {
        builder.ToTable("events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasMaxLength(64);
        builder.Property(e => e.ConversationId).IsRequired();
        builder.Property(e => e.Kind).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Source).HasMaxLength(16).IsRequired();
        builder.Property(e => e.TimestampTicks).IsRequired();
        builder.Property(e => e.ParentId).HasMaxLength(64);
        builder.Property(e => e.Payload).IsRequired();
        builder.HasIndex(e => new { e.ConversationId, e.TimestampTicks });
        builder.HasIndex(e => new { e.ConversationId, e.Kind });
    }
}