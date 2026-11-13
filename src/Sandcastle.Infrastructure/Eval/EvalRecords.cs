using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Eval;

/// <summary>
/// SPEC-152 RF-001: an eval run — one benchmark pass of a suite
/// (<see cref="Suite"/>) pinned to a model/route. Status lifecycle:
/// <c>queued → running → completed|failed|cancelled</c>.
/// <see cref="StatsJson"/> carries orchestration stats (wall time,
/// per-verdict counts, tokens/cost rollups).
/// </summary>
public sealed class EvalRunRecord : IOrgScoped
{
    /// <summary>PK.</summary>
    public required Guid Id { get; set; }

    /// <inheritdoc cref="IOrgScoped.OrgId" />
    public Guid? OrgId { get; set; }

    /// <summary>User that started the run (owns the eval conversations).</summary>
    public string? CreatedByUserId { get; set; }

    /// <summary><c>swe-lite|terminal-bench-2|custom</c>.</summary>
    public required string Suite { get; set; }

    /// <summary>Number of tasks accepted into the run (post-n clamp).</summary>
    public int NTasks { get; set; }

    /// <summary>Model pin applied to every eval conversation (null = router).</summary>
    public string? Model { get; set; }

    /// <summary>
    /// Route axis recorded for the scorecard (SPEC-149 route label) —
    /// e.g. an experiment/candidate name; the run groups by
    /// <see cref="Model"/> + <see cref="RouteOverride"/>.
    /// </summary>
    public string? RouteOverride { get; set; }

    /// <summary><c>queued|running|completed|failed|cancelled</c>.</summary>
    public required string Status { get; set; }

    /// <summary>Tasks with verdict <c>resolved</c>.</summary>
    public int ResolvedN { get; set; }

    /// <summary>Tasks with verdict <c>failed</c>.</summary>
    public int FailedN { get; set; }

    /// <summary>Tasks with verdict <c>error</c>.</summary>
    public int ErrorN { get; set; }

    /// <summary>Tasks with verdict <c>timeout</c>.</summary>
    public int TimeoutN { get; set; }

    /// <summary><c>resolved_n / n_tasks * 100</c> (0 while running).</summary>
    public double ScorePct { get; set; }

    /// <summary>Dry-run estimate persisted at create (USD).</summary>
    public decimal? EstimatedCostUsd { get; set; }

    /// <summary>Sum of per-task <c>cost_usd</c> after completion.</summary>
    public decimal? ActualCostUsd { get; set; }

    /// <summary>Suite=custom jsonl path | dataset dir resolved at create.</summary>
    public string? DatasetPath { get; set; }

    /// <summary>Orchestration stats JSON (wall time, token rollup…).</summary>
    public string? StatsJson { get; set; }

    /// <summary>Failure detail when <c>failed</c>.</summary>
    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Ticks mirror of <see cref="CreatedAt"/> — cursor pagination.</summary>
    public long CreatedAtTicks { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>
/// SPEC-152 RF-001: per-task eval result. <see cref="Status"/> walks
/// <c>pending → running → done</c>; <see cref="Verdict"/> is the terminal
/// outcome (<c>resolved|failed|error|timeout</c>).
/// <see cref="TaskJson"/> freezes the <c>eval_task_v1</c> snapshot so the
/// result stays reproducible even if the dataset moves.
/// </summary>
public sealed class EvalResultRecord : IOrgScoped
{
    /// <summary>PK.</summary>
    public required Guid Id { get; set; }

    /// <inheritdoc cref="IOrgScoped.OrgId" />
    public Guid? OrgId { get; set; }

    /// <summary>Owning run.</summary>
    public required Guid RunId { get; set; }

    /// <summary>Task key inside the suite (<c>eval_task_v1.task_id</c>).</summary>
    public required string TaskId { get; set; }

    /// <summary><c>pending|running|done</c>.</summary>
    public required string Status { get; set; }

    /// <summary><c>resolved|failed|error|timeout</c> — null until done.</summary>
    public string? Verdict { get; set; }

    /// <summary>eval_mode conversation id (agent_type <c>eval</c>).</summary>
    public Guid? SessionId { get; set; }

    /// <summary>Task wall time in seconds (setup+agent+checker).</summary>
    public double? DurationS { get; set; }

    /// <summary>Prompt + completion tokens billed (TokenEvent sum).</summary>
    public long Tokens { get; set; }

    /// <summary>Prompt tokens billed (estimate granularity).</summary>
    public long PromptTokens { get; set; }

    /// <summary>Completion tokens billed (estimate granularity).</summary>
    public long CompletionTokens { get; set; }

    /// <summary>USD billed (TokenEvent sum).</summary>
    public decimal CostUsd { get; set; }

    /// <summary>SHA-256 hex of the redacted evidence tail (checker+agent log).</summary>
    public string? LogDigest { get; set; }

    /// <summary>Failure detail (setup/checker/infra errors).</summary>
    public string? Error { get; set; }

    /// <summary>Frozen <c>eval_task_v1</c> snapshot (JSON).</summary>
    public string? TaskJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Ticks mirror of <see cref="CreatedAt"/> — ordering.</summary>
    public long CreatedAtTicks { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>EF config for <see cref="EvalRunRecord"/>.</summary>
public sealed class EvalRunRecordConfiguration
    : IEntityTypeConfiguration<EvalRunRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<EvalRunRecord> builder)
    {
        builder.ToTable("eval_runs");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.CreatedByUserId).HasMaxLength(200);
        builder.Property(e => e.Suite).HasMaxLength(40).IsRequired();
        builder.Property(e => e.Model).HasMaxLength(200);
        builder.Property(e => e.RouteOverride).HasMaxLength(200);
        builder.Property(e => e.Status).HasMaxLength(20).IsRequired();
        builder.Property(e => e.DatasetPath).HasMaxLength(1000);
        builder.Property(e => e.Error).HasMaxLength(2000);
        builder.HasIndex(e => new { e.OrgId, e.Status });
        builder.HasIndex(e => new { e.OrgId, e.Suite });
        builder.HasIndex(e => e.CreatedAtTicks).IsDescending();
    }
}

/// <summary>EF config for <see cref="EvalResultRecord"/>.</summary>
public sealed class EvalResultRecordConfiguration
    : IEntityTypeConfiguration<EvalResultRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<EvalResultRecord> builder)
    {
        builder.ToTable("eval_results");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.TaskId).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Verdict).HasMaxLength(20);
        builder.Property(e => e.LogDigest).HasMaxLength(128);
        builder.Property(e => e.Error).HasMaxLength(2000);
        builder.HasIndex(e => new { e.OrgId, e.Verdict });
        builder.HasIndex(e => e.RunId);
        builder.HasIndex(e => new { e.RunId, e.TaskId }).IsUnique();
        builder.HasIndex(e => e.SessionId);
        builder.HasIndex(e => e.CreatedAtTicks).IsDescending();
    }
}