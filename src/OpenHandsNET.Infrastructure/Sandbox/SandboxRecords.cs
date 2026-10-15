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
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>Origem do sandbox: <c>cold</c> (criado sob demanda) ou <c>warm</c> (pool).</summary>
    public string Pool { get; set; } = "cold";

    /// <summary>SPEC-17: chave do pool que originou o container (imagem).</summary>
    public string? PoolKey { get; set; }

    /// <summary>SPEC-17: resultado do blueprint — <c>ok|failed|none</c>.</summary>
    public string BlueprintStatus { get; set; } = "none";

    /// <summary>SPEC-17: cauda (4 KB) do log de provisionamento.</summary>
    public string? BlueprintLogTail { get; set; }

    /// <summary>
    /// SPEC-17: session key do agente in-container, protegida por
    /// DataProtection — nunca plaintext, nunca serializada.
    /// </summary>
    public string? SessionKeyProtected { get; set; }

    /// <summary>
    /// SPEC-17 RF-004: override de imagem vindo do <c>openhandsnet.yaml</c> —
    /// aplicado quando um novo sandbox é criado para a conversa.
    /// </summary>
    public string? BlueprintImage { get; set; }

    /// <summary>
    /// SPEC-17 RF-004: env resolvido pelo blueprint (JSON) — merge no
    /// <c>spec.Env</c> efetivo no próximo start do container.
    /// </summary>
    public string? BlueprintEnvJson { get; set; }

    /// <summary>Espelho numérico de <see cref="CreatedAt"/> para ORDER BY.</summary>
    public long CreatedAtTicks { get; set; }

    /// <summary>Espelho numérico de <see cref="UpdatedAt"/> para WHERE.</summary>
    public long UpdatedAtTicks { get; set; }

    /// <summary>Espelho numérico de <see cref="ExpiresAt"/> para WHERE.</summary>
    public long? ExpiresAtTicks { get; set; }

    public Domain.Sandbox.Sandbox ToDomain() => new(
        Id, SpecId, ConversationId, ContainerId,
        Enum.Parse<SandboxStatus>(Status, ignoreCase: true),
        AgentUrl is null ? null : new Uri(AgentUrl),
        CreatedAt, ExpiresAt, LastError, Pool,
        PoolKey, BlueprintStatus, BlueprintLogTail);
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
    public int PidsLimit { get; set; }
    public string? Profile { get; set; }

    /// <summary>SPEC-38: opt-in public ports (JSON array), null = nenhuma.</summary>
    public string? PublicPortsJson { get; set; }

    public SandboxSpec ToDomain() => new(
        Id, Name, Image, MemoryMb, CpuLimit, TimeoutMinutes,
        EnvJson is null ? null
            : System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, string>>(EnvJson),
        MountWorkspace, PidsLimit, Profile,
        PublicPortsJson is null ? null
            : System.Text.Json.JsonSerializer
                .Deserialize<List<int>>(PublicPortsJson));

    public static SandboxSpecRecord From(SandboxSpec s) => new()
    {
        Id = s.Id, Name = s.Name, Image = s.Image, MemoryMb = s.MemoryMb,
        CpuLimit = s.CpuLimit, TimeoutMinutes = s.TimeoutMinutes,
        EnvJson = s.Env is null ? null
            : System.Text.Json.JsonSerializer.Serialize(s.Env),
        MountWorkspace = s.MountWorkspace,
        PidsLimit = s.PidsLimit,
        Profile = s.Profile,
        PublicPortsJson = s.PublicPorts is null ? null
            : System.Text.Json.JsonSerializer.Serialize(s.PublicPorts),
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
