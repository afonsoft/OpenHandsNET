using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Refactors;

/// <summary>
/// SPEC-144 §4: a batch refactor plan — goal + repos + strategy/mode
/// producing <see cref="PlanJson"/> (<c>refactor_plan_v1</c>) executed as
/// topologically-ordered <see cref="RefactorBatchRecord"/> rows. Status
/// lifecycle: <c>planning → awaiting_approval → running → completed |
/// failed | cancelled</c> (<c>paused</c> interleaved). Conversations:
/// <see cref="PlannerConversationId"/> (agent_type
/// <c>refactor-planner</c>) anchors the plan; <see
/// cref="CoordinatorConversationId"/> (agent_type
/// <c>refactor-coordinator</c>) parents every batch child.
/// </summary>
public sealed class RefactorPlanRecord : IOrgScoped
{
    /// <summary>PK.</summary>
    public required Guid Id { get; set; }

    /// <inheritdoc cref="IOrgScoped.OrgId" />
    public Guid? OrgId { get; set; }

    /// <summary>User that requested the refactor (owns the planner).</summary>
    public string? CreatedByUserId { get; set; }

    /// <summary>JSON <c>string[]</c> of repositories (<c>owner/repo</c>).</summary>
    public required string RepositoriesJson { get; set; }

    /// <summary>Free-form refactor goal (≤4000 chars).</summary>
    public required string Goal { get; set; }

    /// <summary><c>directory|import-graph</c>.</summary>
    public required string Strategy { get; set; }

    /// <summary>Effective mode <c>independent|stacked</c> (post-planner).</summary>
    public required string Mode { get; set; }

    /// <summary>Mode as requested (null-safe copy kept for audit).</summary>
    public required string RequestedMode { get; set; }

    /// <summary>Bounded batch-child fan-out (≤ Refactor:MaxConcurrencyCap).</summary>
    public int MaxConcurrency { get; set; }

    /// <summary>Soft target of files per batch from the request.</summary>
    public int? BatchSizeHint { get; set; }

    /// <summary><c>refactor_plan_v1</c> JSON (batches + warnings).</summary>
    public string? PlanJson { get; set; }

    /// <summary>
    /// <c>planning|awaiting_approval|running|paused|completed|failed|cancelled</c>.
    /// </summary>
    public required string Status { get; set; }

    /// <summary>Planner conversation (agent_type refactor-planner).</summary>
    public Guid? PlannerConversationId { get; set; }

    /// <summary>Coordinator conversation (delegation parent).</summary>
    public Guid? CoordinatorConversationId { get; set; }

    /// <summary>Failure detail when <c>failed</c>.</summary>
    public string? Error { get; set; }

    /// <summary>Orchestration stats JSON (waves/children/merges…).</summary>
    public string? StatsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Ticks mirror of <see cref="CreatedAt"/> — cursor pagination.</summary>
    public long CreatedAtTicks { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>
/// SPEC-144 §4: one batch of a refactor plan — files + dependency
/// pointers (by batch <see cref="N"/>), verify gate, worker child and PR
/// tracking. State machine documented on
/// <see cref="Application.Refactors.RefactorBatchStates"/>.
/// </summary>
public sealed class RefactorBatchRecord
{
    /// <summary>PK.</summary>
    public required Guid Id { get; set; }

    /// <summary>Owning plan.</summary>
    public required Guid PlanId { get; set; }

    /// <summary>1-based ordinal inside the plan (topo order).</summary>
    public required int N { get; set; }

    /// <summary><c>owner/repo</c> this batch's files live in.</summary>
    public required string Repository { get; set; }

    /// <summary>JSON <c>string[]</c> of repo-relative file paths.</summary>
    public required string FilesJson { get; set; }

    /// <summary>JSON <c>int[]</c> of batch Ns that must merge first.</summary>
    public required string DependsOnJson { get; set; }

    /// <summary>Planner-authored batch description.</summary>
    public string? Description { get; set; }

    /// <summary><c>low|med|high</c>.</summary>
    public required string Risk { get; set; }

    /// <summary>Build+test gate command run inside the child sandbox.</summary>
    public string? VerifyCommand { get; set; }

    /// <summary>State (see <see cref="Application.Refactors.RefactorBatchStates"/>).</summary>
    public required string State { get; set; }

    /// <summary>PR opened by the batch child.</summary>
    public string? PrUrl { get; set; }

    /// <summary>PR number — webhook/reconcile matching.</summary>
    public int? PrNumber { get; set; }

    /// <summary>Worker child conversation (agent_type refactor-worker).</summary>
    public Guid? ConversationId { get; set; }

    /// <summary>Executor-level dispatch count (retry increments).</summary>
    public int Attempts { get; set; }

    /// <summary>SHA-256 of the redacted verify log tail (hex).</summary>
    public string? VerifyLogDigest { get; set; }

    /// <summary>Failure detail when <c>failed</c>.</summary>
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>EF config for <see cref="RefactorPlanRecord"/>.</summary>
public sealed class RefactorPlanRecordConfiguration
    : IEntityTypeConfiguration<RefactorPlanRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RefactorPlanRecord> builder)
    {
        builder.ToTable("refactor_plans");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.CreatedByUserId).HasMaxLength(200);
        builder.Property(e => e.Goal).HasMaxLength(4000).IsRequired();
        builder.Property(e => e.Strategy).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Mode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.RequestedMode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Error).HasMaxLength(2000);
        builder.HasIndex(e => new { e.OrgId, e.Status });
        builder.HasIndex(e => e.CoordinatorConversationId);
        builder.HasIndex(e => e.CreatedAtTicks).IsDescending();
    }
}

/// <summary>EF config for <see cref="RefactorBatchRecord"/>.</summary>
public sealed class RefactorBatchRecordConfiguration
    : IEntityTypeConfiguration<RefactorBatchRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RefactorBatchRecord> builder)
    {
        builder.ToTable("refactor_batches");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.N).IsRequired();
        builder.Property(e => e.Repository).HasMaxLength(300).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(500);
        builder.Property(e => e.Risk).HasMaxLength(10).IsRequired();
        builder.Property(e => e.VerifyCommand).HasMaxLength(500);
        builder.Property(e => e.State).HasMaxLength(20).IsRequired();
        builder.Property(e => e.PrUrl).HasMaxLength(1000);
        builder.Property(e => e.VerifyLogDigest).HasMaxLength(64);
        builder.Property(e => e.Error).HasMaxLength(1000);
        builder.HasIndex(e => e.PlanId);
        builder.HasIndex(e => new { e.PlanId, e.N }).IsUnique();
        builder.HasIndex(e => e.State);
        // webhook/reconcile resolve (repo, pr_number) → batch.
        builder.HasIndex(e => new { e.Repository, e.PrNumber });
    }
}