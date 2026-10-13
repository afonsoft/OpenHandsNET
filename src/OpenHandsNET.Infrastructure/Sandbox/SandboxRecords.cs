using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenHandsNET.Domain.Sandbox;

namespace OpenHandsNET.Infrastructure.Sandbox;

/// <summary>Persisted sandbox row (state reconciled with Docker on read).</summary>
public sealed class SandboxRecord
{
    public required Guid Id { get; set; }
    public required Guid SpecId { get; set; }
    public Guid? ConversationId { get; set; }
    public string? ContainerId { get; set; }
    public required string Status { get; set; }
    public string? AgentUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>Espelho numérico de <see cref="CreatedAt"/> para ORDER BY.</summary>
    public long CreatedAtTicks { get; set; }

    /// <summary>Espelho numérico de <see cref="ExpiresAt"/> para WHERE.</summary>
    public long? ExpiresAtTicks { get; set; }

    public Domain.Sandbox.Sandbox ToDomain() => new(
        Id, SpecId, ConversationId, ContainerId,
        Enum.Parse<SandboxStatus>(Status, ignoreCase: true),
        AgentUrl is null ? null : new Uri(AgentUrl),
        CreatedAt, ExpiresAt, LastError);
}

/// <summary>Persisted sandbox spec row (env serialized as JSON).</summary>
public sealed class SandboxSpecRecord
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Image { get; set; }
    public int MemoryMb { get; set; }
    public double CpuLimit { get; set; }
    public int TimeoutMinutes { get; set; }
    public string? EnvJson { get; set; }
    public bool MountWorkspace { get; set; }

    public SandboxSpec ToDomain() => new(
        Id, Name, Image, MemoryMb, CpuLimit, TimeoutMinutes,
        EnvJson is null ? null
            : System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, string>>(EnvJson),
        MountWorkspace);

    public static SandboxSpecRecord From(SandboxSpec s) => new()
    {
        Id = s.Id, Name = s.Name, Image = s.Image, MemoryMb = s.MemoryMb,
        CpuLimit = s.CpuLimit, TimeoutMinutes = s.TimeoutMinutes,
        EnvJson = s.Env is null ? null
            : System.Text.Json.JsonSerializer.Serialize(s.Env),
        MountWorkspace = s.MountWorkspace,
    };
}

/// <summary>EF config for <see cref="SandboxRecord"/>.</summary>
public sealed class SandboxRecordConfiguration : IEntityTypeConfiguration<SandboxRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SandboxRecord> builder)
    {
        builder.ToTable("sandboxes");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Status).HasMaxLength(20).IsRequired();
        builder.Property(e => e.ContainerId).HasMaxLength(80);
        builder.HasIndex(e => e.ConversationId);
        builder.HasIndex(e => e.Status);
    }
}

/// <summary>EF config for <see cref="SandboxSpecRecord"/>.</summary>
public sealed class SandboxSpecRecordConfiguration : IEntityTypeConfiguration<SandboxSpecRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SandboxSpecRecord> builder)
    {
        builder.ToTable("sandbox_specs");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Image).HasMaxLength(500).IsRequired();
    }
}
