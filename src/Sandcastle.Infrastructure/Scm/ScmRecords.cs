using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Scm;

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

    /// <summary>SPEC-53: base URL do provider (self-managed); null = default.</summary>
    public string? ApiUrl { get; set; }

    /// <summary>SPEC-53: <c>pat|app|oauth|entra_sp</c>.</summary>
    public string? AuthKind { get; set; }

    /// <summary>SPEC-53: extras do provider (project keys, workspace, collection).</summary>
    public string? MetaJson { get; set; }

    /// <summary>SPEC-140 RF-001: auto-review de PR habilitado na conexão.</summary>
    public bool ReviewEnabled { get; set; }

    /// <summary>SPEC-140 RF-001: JSON array de repos com review (vazio/null = todos da conexão).</summary>
    public string? ReviewReposJson { get; set; }

    /// <summary>SPEC-140 RF-001: <c>all_prs</c> (default) | <c>label_only</c>.</summary>
    public string? ReviewTriggerMode { get; set; }

    /// <summary>SPEC-140 RF-003: <c>completed</c> (default) | <c>no_critical</c> | <c>no_findings</c>.</summary>
    public string? ReviewPassCondition { get; set; }

    /// <summary>SPEC-141 RF-001: CI autofix habilitado na conexão.</summary>
    public bool AutofixEnabled { get; set; }

    /// <summary>SPEC-141 RF-001: JSON array de repos inscritos (vazio/null = todos da conexão).</summary>
    public string? AutofixReposJson { get; set; }

    /// <summary>SPEC-141 RF-002: JSON array de globs de check-name elegíveis (vazio/null = global).</summary>
    public string? AutofixCheckPatternsJson { get; set; }

    /// <summary>SPEC-141 RF-002: cap de tentativas por PR (null = <c>Autofix:MaxAttempts</c>).</summary>
    public int? AutofixMaxAttempts { get; set; }

    /// <summary>Usuário que registrou a conexão (atribuição de triggers).</summary>
    public string? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// SPEC-140 RF-002: um run de auto-review (diff → LLM → findings →
/// posting). Dedup por <c>(Repository, PrNumber, HeadSha)</c>; o
/// <see cref="DiffHash"/> captura o conteúdo do diff p/ skip de
/// re-review (paridade Devin re-review skip).
/// </summary>
public sealed class ReviewRunRecord : IOrgScoped
{
    public required Guid Id { get; set; }
    public Guid? OrgId { get; set; }

    /// <summary>Conexão SCM dona do repo.</summary>
    public required Guid ConnectionId { get; set; }

    /// <summary><c>owner/repo</c>.</summary>
    public required string Repository { get; set; }
    public int PrNumber { get; set; }

    /// <summary>Head SHA revisado.</summary>
    public required string HeadSha { get; set; }

    /// <summary>SHA-256 do diff efetivamente revisado (dedup de conteúdo).</summary>
    public string? DiffHash { get; set; }

    /// <summary><c>queued|running|completed|skipped|failed</c>.</summary>
    public required string Status { get; set; }

    /// <summary>Diff truncado pelos caps (MaxFilesPerRun/MaxDiffKb).</summary>
    public bool Partial { get; set; }

    /// <summary><c>webhook|manual|reviewer_request</c>.</summary>
    public required string Trigger { get; set; }

    /// <summary>Usuário que disparou (manual) — null = webhook.</summary>
    public string? RequestedByUserId { get; set; }

    /// <summary>Detalhe do erro quando <c>failed</c>.</summary>
    public string? Error { get; set; }

    /// <summary>Stats do run (files_reviewed, tokens, dropped_findings…).</summary>
    public string? StatsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Ticks de <see cref="CreatedAt"/> — cursor pagination.</summary>
    public long CreatedAtTicks { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>
/// SPEC-140 RF-004: finding validado contra o diff e persistido
/// (link ao comentário postado em <see cref="CommentUrl"/> quando houver).
/// </summary>
public sealed class ReviewFindingRecord : IOrgScoped
{
    public required Guid Id { get; set; }
    public Guid? OrgId { get; set; }
    public required Guid ReviewRunId { get; set; }
    public required string Repository { get; set; }
    public int PrNumber { get; set; }

    /// <summary><c>bug|security|perf|style|investigate|note</c>.</summary>
    public required string Kind { get; set; }

    /// <summary><c>critical|important|suggestion|nit</c>.</summary>
    public required string Severity { get; set; }
    public double? Confidence { get; set; }
    public required string Path { get; set; }
    public int? Line { get; set; }
    public string? Title { get; set; }
    public required string Body { get; set; }
    public string? SuggestedPatch { get; set; }

    /// <summary><c>open|fixed|dismissed|stale</c>.</summary>
    public required string Status { get; set; }

    /// <summary>URL do comentário/review no provider.</summary>
    public string? CommentUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// SPEC-141 RF-004: um run de CI autofix — loop
/// (logs → classificação → conversa de fix → push → poll) até verde,
/// cap, blocked ou cancel. Dedup por <c>(Repository, PrNumber, HeadSha,
/// CheckName)</c>; um run ativo por PR. <see cref="AttemptCount"/> é o
/// cap efetivo consumido (máx <c>AutofixMaxAttempts</c>).
/// </summary>
public sealed class AutofixRunRecord : IOrgScoped
{
    public required Guid Id { get; set; }
    public Guid? OrgId { get; set; }

    /// <summary>Conexão SCM dona do repo.</summary>
    public required Guid ConnectionId { get; set; }

    /// <summary><c>owner/repo</c> (repo base do PR).</summary>
    public required string Repository { get; set; }
    public int PrNumber { get; set; }

    /// <summary>Head SHA do check que disparou (atualizado a cada ciclo red).</summary>
    public required string HeadSha { get; set; }

    /// <summary>Branch head do PR (alvo do push do fix).</summary>
    public string? HeadBranch { get; set; }

    /// <summary>Nome do check em falha (atualizado a cada ciclo red).</summary>
    public required string CheckName { get; set; }

    /// <summary><c>check_run|check_suite|workflow_run|status|manual</c>.</summary>
    public required string CheckKind { get; set; }

    /// <summary>Check run id (logs via Checks API).</summary>
    public long? CheckRunId { get; set; }

    /// <summary>Workflow run id (logs via Actions API).</summary>
    public long? WorkflowRunId { get; set; }

    /// <summary>URL do check no provider (details/html/target).</summary>
    public string? CheckUrl { get; set; }

    /// <summary><c>queued|running|waiting_check|completed|failed|blocked|cancelled</c>.</summary>
    public required string Status { get; set; }

    /// <summary>Tentativas consumidas (1..cap).</summary>
    public int AttemptCount { get; set; }

    /// <summary>Conversa de fix mais recente (agent_type=autofix).</summary>
    public Guid? ConversationId { get; set; }

    /// <summary><c>webhook|manual</c>.</summary>
    public required string Trigger { get; set; }

    /// <summary>Usuário que disparou (manual) — null = webhook.</summary>
    public string? RequestedByUserId { get; set; }

    /// <summary>Detalhe do erro/outcome quando terminal.</summary>
    public string? Error { get; set; }

    /// <summary>Stats do run (modelo, attempts, outcome, shas).</summary>
    public string? StatsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Ticks de <see cref="CreatedAt"/> — cursor pagination.</summary>
    public long CreatedAtTicks { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>
/// SPEC-141 RF-004: uma tentativa de fix dentro de um run —
/// <c>head_sha_in</c> (falha) → conversa de fix →
/// <c>head_sha_out</c> (push detectado) → outcome.
/// </summary>
public sealed class AutofixAttemptRecord : IOrgScoped
{
    public required Guid Id { get; set; }
    public Guid? OrgId { get; set; }
    public required Guid RunId { get; set; }

    /// <summary>Nº da tentativa (1..cap).</summary>
    public int Number { get; set; }

    /// <summary>Head que falhou (entrada do fix).</summary>
    public required string HeadShaIn { get; set; }

    /// <summary>Head empurrado pelo fix (detectado no poll).</summary>
    public string? HeadShaOut { get; set; }

    /// <summary>JSON dos checks em falha observados [{name,kind,id,conclusion}].</summary>
    public string? FailedChecksJson { get; set; }

    /// <summary><c>fixed|still_failing|blocked|error|cancelled</c>.</summary>
    public string? Outcome { get; set; }

    /// <summary>Trecho redigido do log de falha (diagnóstico persistido).</summary>
    public string? LogDigest { get; set; }

    /// <summary>Conversa de fix da tentativa.</summary>
    public Guid? ConversationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
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
        builder.Property(e => e.ApiUrl).HasMaxLength(500);
        builder.Property(e => e.AuthKind).HasMaxLength(20);
        builder.Property(e => e.ReviewTriggerMode).HasMaxLength(20);
        builder.Property(e => e.ReviewPassCondition).HasMaxLength(20);
        builder.Property(e => e.CreatedByUserId).HasMaxLength(200);
        builder.HasIndex(e => new { e.OrgId, e.Kind });
    }
}

/// <summary>EF config for <see cref="ReviewRunRecord"/>.</summary>
public sealed class ReviewRunRecordConfiguration
    : IEntityTypeConfiguration<ReviewRunRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ReviewRunRecord> builder)
    {
        builder.ToTable("review_runs");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Repository).HasMaxLength(300).IsRequired();
        builder.Property(e => e.HeadSha).HasMaxLength(80).IsRequired();
        builder.Property(e => e.DiffHash).HasMaxLength(64);
        builder.Property(e => e.Status).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Trigger).HasMaxLength(20).IsRequired();
        builder.Property(e => e.RequestedByUserId).HasMaxLength(200);
        builder.Property(e => e.Error).HasMaxLength(2000);
        builder.HasIndex(e => new { e.Repository, e.PrNumber, e.HeadSha });
        builder.HasIndex(e => new { e.OrgId, e.Status });
        builder.HasIndex(e => e.ConnectionId);
        builder.HasIndex(e => e.CreatedAtTicks).IsDescending();
    }
}

/// <summary>EF config for <see cref="ReviewFindingRecord"/>.</summary>
public sealed class ReviewFindingRecordConfiguration
    : IEntityTypeConfiguration<ReviewFindingRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ReviewFindingRecord> builder)
    {
        builder.ToTable("review_findings");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Repository).HasMaxLength(300).IsRequired();
        builder.Property(e => e.Kind).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Severity).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Path).HasMaxLength(1000).IsRequired();
        builder.Property(e => e.Title).HasMaxLength(300);
        builder.Property(e => e.Status).HasMaxLength(20).IsRequired();
        builder.Property(e => e.CommentUrl).HasMaxLength(1000);
        builder.HasIndex(e => new { e.Repository, e.PrNumber });
        builder.HasIndex(e => e.ReviewRunId);
        builder.HasIndex(e => e.Status);
    }
}

/// <summary>EF config for <see cref="AutofixRunRecord"/>.</summary>
public sealed class AutofixRunRecordConfiguration
    : IEntityTypeConfiguration<AutofixRunRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AutofixRunRecord> builder)
    {
        builder.ToTable("autofix_runs");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Repository).HasMaxLength(300).IsRequired();
        builder.Property(e => e.HeadSha).HasMaxLength(80).IsRequired();
        builder.Property(e => e.HeadBranch).HasMaxLength(300);
        builder.Property(e => e.CheckName).HasMaxLength(300).IsRequired();
        builder.Property(e => e.CheckKind).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Trigger).HasMaxLength(20).IsRequired();
        builder.Property(e => e.RequestedByUserId).HasMaxLength(200);
        builder.Property(e => e.Error).HasMaxLength(2000);
        builder.HasIndex(e => new { e.Repository, e.PrNumber, e.HeadSha });
        builder.HasIndex(e => new { e.OrgId, e.Status });
        builder.HasIndex(e => e.ConnectionId);
        builder.HasIndex(e => e.CreatedAtTicks).IsDescending();
    }
}

/// <summary>EF config for <see cref="AutofixAttemptRecord"/>.</summary>
public sealed class AutofixAttemptRecordConfiguration
    : IEntityTypeConfiguration<AutofixAttemptRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AutofixAttemptRecord> builder)
    {
        builder.ToTable("autofix_attempts");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.HeadShaIn).HasMaxLength(80).IsRequired();
        builder.Property(e => e.HeadShaOut).HasMaxLength(80);
        builder.Property(e => e.Outcome).HasMaxLength(20);
        builder.HasIndex(e => e.RunId);
        builder.HasIndex(e => new { e.RunId, e.Number });
        builder.HasIndex(e => e.HeadShaOut);
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