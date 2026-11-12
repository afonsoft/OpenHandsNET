using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Scans;

/// <summary>
/// SPEC-143 RF-004: a code scan — goal + profile + effort fan-out over
/// <see cref="RepositoriesJson"/> via the coordinator conversation
/// (<see cref="CoordinatorConversationId"/>, agent_type
/// <c>scan-coordinator</c>). Status lifecycle: queued → running →
/// completed|failed. <see cref="StatsJson"/> carries orchestration stats
/// (partitions, children, findings, dedup merges, tokens).
/// </summary>
public sealed class ScanRecord : IOrgScoped
{
    /// <summary>PK.</summary>
    public required Guid Id { get; set; }

    /// <inheritdoc cref="IOrgScoped.OrgId" />
    public Guid? OrgId { get; set; }

    /// <summary>User that started the scan (owns the coordinator).</summary>
    public string? CreatedByUserId { get; set; }

    /// <summary>JSON <c>string[]</c> of repositories (<c>owner/repo</c>).</summary>
    public required string RepositoriesJson { get; set; }

    /// <summary>Free-form scan goal (≤4000 chars).</summary>
    public required string Goal { get; set; }

    /// <summary><c>security|performance|dead-code|coverage-gaps|a11y|custom</c>.</summary>
    public required string Profile { get; set; }

    /// <summary><c>low|medium|high</c> → child cap.</summary>
    public required string Effort { get; set; }

    /// <summary><c>queued|running|completed|failed|cancelled</c>.</summary>
    public required string Status { get; set; }

    /// <summary><c>manual|automation|slash</c>.</summary>
    public required string Trigger { get; set; }

    /// <summary>Coordinator conversation (delegation parent).</summary>
    public Guid? CoordinatorConversationId { get; set; }

    /// <summary>Failure detail when <c>failed</c>.</summary>
    public string? Error { get; set; }

    /// <summary>Orchestration stats JSON (partitions/children/findings…).</summary>
    public string? StatsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Ticks mirror of <see cref="CreatedAt"/> — cursor pagination.</summary>
    public long CreatedAtTicks { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>
/// SPEC-143 RF-003: a triaged scan finding. Pipeline:
/// <c>status</c> open|reviewed|dismissed × <c>remediation_state</c>
/// unassigned|assigned|pr_open|merged — reconciled against the linked
/// remediation conversation's PR fields. Text fields pass through
/// <c>SecretRedactor</c> before persistence (NFR).
/// </summary>
public sealed class ScanFindingRecord : IOrgScoped
{
    /// <summary>PK.</summary>
    public required Guid Id { get; set; }

    /// <inheritdoc cref="IOrgScoped.OrgId" />
    public Guid? OrgId { get; set; }

    /// <summary>Owning scan.</summary>
    public required Guid ScanId { get; set; }

    /// <summary><c>owner/repo</c>.</summary>
    public required string Repository { get; set; }

    /// <summary>Repo-relative file path.</summary>
    public required string Path { get; set; }
    public int? StartLine { get; set; }
    public int? EndLine { get; set; }

    /// <summary><c>security|performance|dead_code|coverage|a11y|custom</c>.</summary>
    public required string Kind { get; set; }

    /// <summary><c>critical|high|medium|low|info</c>.</summary>
    public required string Severity { get; set; }
    public double? Confidence { get; set; }
    public string? Title { get; set; }

    /// <summary>Finding body (redacted).</summary>
    public required string Description { get; set; }
    public string? RemediationHint { get; set; }

    /// <summary><c>open|reviewed|dismissed</c>.</summary>
    public required string Status { get; set; }

    /// <summary><c>unassigned|assigned|pr_open|merged</c>.</summary>
    public required string RemediationState { get; set; }

    /// <summary>Remediation conversation (agent_type <c>scan-fix</c>).</summary>
    public Guid? ConversationId { get; set; }

    /// <summary>PR opened by the remediation conversation.</summary>
    public string? PrUrl { get; set; }

    /// <summary><c>high|medium|low</c> effort-vs-impact estimate.</summary>
    public string? Payoff { get; set; }

    /// <summary>Scan-child conversation that emitted the finding.</summary>
    public Guid? SourceConversationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Ticks mirror of <see cref="CreatedAt"/> — cursor pagination.</summary>
    public long CreatedAtTicks { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>EF config for <see cref="ScanRecord"/>.</summary>
public sealed class ScanRecordConfiguration
    : IEntityTypeConfiguration<ScanRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ScanRecord> builder)
    {
        builder.ToTable("scans");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.CreatedByUserId).HasMaxLength(200);
        builder.Property(e => e.Goal).HasMaxLength(4000).IsRequired();
        builder.Property(e => e.Profile).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Effort).HasMaxLength(10).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Trigger).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Error).HasMaxLength(2000);
        builder.HasIndex(e => new { e.OrgId, e.Status });
        builder.HasIndex(e => e.CoordinatorConversationId);
        builder.HasIndex(e => e.CreatedAtTicks).IsDescending();
    }
}

/// <summary>EF config for <see cref="ScanFindingRecord"/>.</summary>
public sealed class ScanFindingRecordConfiguration
    : IEntityTypeConfiguration<ScanFindingRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ScanFindingRecord> builder)
    {
        builder.ToTable("scan_findings");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Repository).HasMaxLength(300).IsRequired();
        builder.Property(e => e.Path).HasMaxLength(1000).IsRequired();
        builder.Property(e => e.Kind).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Severity).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Title).HasMaxLength(300);
        builder.Property(e => e.Status).HasMaxLength(20).IsRequired();
        builder.Property(e => e.RemediationState).HasMaxLength(20).IsRequired();
        builder.Property(e => e.PrUrl).HasMaxLength(1000);
        builder.Property(e => e.Payoff).HasMaxLength(10);
        // SPEC-143 §4: (org,status), (scan_id), (repo,severity), (path).
        builder.HasIndex(e => new { e.OrgId, e.Status });
        builder.HasIndex(e => e.ScanId);
        builder.HasIndex(e => new { e.Repository, e.Severity });
        builder.HasIndex(e => e.Path);
        builder.HasIndex(e => e.RemediationState);
        builder.HasIndex(e => e.CreatedAtTicks).IsDescending();
    }
}