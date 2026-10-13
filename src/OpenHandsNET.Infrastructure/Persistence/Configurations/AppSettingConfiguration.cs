using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenHandsNET.Domain.Configuration;

namespace OpenHandsNET.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="AppSetting"/>.
/// </summary>
public sealed class AppSettingConfiguration : IEntityTypeConfiguration<AppSetting>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AppSetting> builder)
    {
        builder.ToTable("app_settings");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Key).HasMaxLength(200).IsRequired();
        builder.HasIndex(e => e.Key).IsUnique();
        builder.Property(e => e.Value);
        builder.Property(e => e.UpdatedAt).IsRequired();
    }
}
