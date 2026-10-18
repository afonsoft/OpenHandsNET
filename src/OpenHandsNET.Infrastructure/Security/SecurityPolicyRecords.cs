using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace OpenHandsNET.Infrastructure.Security;

/// <summary>SPEC-48 RF-001: org security policy row — preset + overrides.</summary>
public sealed class SecurityPolicyRecord
{
    /// <summary>Row id.</summary>
    public required Guid Id { get; set; }

    /// <summary>Owning organization (one policy per org).</summary>
    public required Guid OrgId { get; set; }

    /// <summary><c>permissive</c> | <c>balanced</c> (default) | <c>strict</c>.</summary>
    public required string Preset { get; set; }

    /// <summary><c>pattern</c> | <c>llm</c> | <c>off</c> | null (preset/inherit).</summary>
    public string? AnalyzerOverride { get; set; }

    /// <summary>Force <c>ask</c> on <c>mcp__*</c> tools; null = preset default.</summary>
    public bool? AskMcpTools { get; set; }

    /// <summary><c>low</c> | <c>medium</c> | <c>high</c> | null (preset default).</summary>
    public string? MinConfirmRisk { get; set; }

    /// <summary>Fail-closed resume on toolset drift; null = preset/global.</summary>
    public bool? ResumeStrict { get; set; }

    /// <summary>SPEC-59: <c>off</c> | <c>pattern</c> | <c>llm</c>;
    /// null = <c>pattern</c>.</summary>
    public string? InputGuardMode { get; set; }

    /// <summary>SPEC-59: role OmniRoute p/ guard LLM (opcional).</summary>
    public string? InputGuardLlmRole { get; set; }

    /// <summary>Last actor that wrote the policy.</summary>
    public Guid? UpdatedByUserId { get; set; }

    /// <summary>Creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last update timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>EF configuration — <c>security_policies</c>, unique org.</summary>
public sealed class SecurityPolicyRecordConfiguration
    : IEntityTypeConfiguration<SecurityPolicyRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SecurityPolicyRecord> builder)
    {
        builder.ToTable("security_policies");
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.OrgId).IsUnique();
        builder.Property(e => e.Preset).HasMaxLength(32).IsRequired();
        builder.Property(e => e.AnalyzerOverride).HasMaxLength(16);
        builder.Property(e => e.MinConfirmRisk).HasMaxLength(16);
    }
}
