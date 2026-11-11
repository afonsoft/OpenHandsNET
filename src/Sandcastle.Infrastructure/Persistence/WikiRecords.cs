using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Sandcastle.Infrastructure.Persistence;

/// <summary>
/// SPEC-145 RF-002: uma execução de geração de wiki por repo/branch —
/// fila in-process (<c>queued→running→completed|failed</c>), páginas em
/// <see cref="WikiPageRecord"/> vinculadas pelo run.
/// </summary>
public sealed class WikiRunRecord : IOrgScoped
{
    public required Guid Id { get; set; }
    public Guid? OrgId { get; set; }
    public required string Repository { get; set; }
    public required string Branch { get; set; }
    public string? CommitSha { get; set; }
    /// <summary><c>low</c> | <c>medium</c> | <c>high</c>.</summary>
    public required string Effort { get; set; }
    /// <summary>queued | running | completed | failed</summary>
    public required string Status { get; set; }
    /// <summary>Re-gera automaticamente em push (wiki.json auto_update).</summary>
    public bool AutoUpdate { get; set; }
    public string? StatsJson { get; set; }
    public string? Error { get; set; }
    public string? RequestedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Ticks de <see cref="CreatedAt"/> — SQLite não ordena DateTimeOffset.</summary>
    public long CreatedAtTicks { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>Ticks de <see cref="FinishedAt"/> — SQLite não ordena DateTimeOffset.</summary>
    public long? FinishedAtTicks { get; set; }
}

/// <summary>
/// SPEC-145 RF-002: página de wiki gerada — markdown com mermaid e
/// citações <c>[path:Lx](permalink)</c> validadas contra o índice;
/// <see cref="ContentHash"/> cobre os arquivos-fonte (diff-aware stale).
/// </summary>
public sealed class WikiPageRecord : IOrgScoped
{
    public required Guid Id { get; set; }
    public Guid? OrgId { get; set; }
    public required Guid WikiRunId { get; set; }
    public required string Repository { get; set; }
    public required string Branch { get; set; }
    /// <summary>Grupo de topo (overview|architecture|modules|data-flow|deployment|conventions).</summary>
    public required string Topic { get; set; }
    /// <summary>Chave única do page dentro de (repo, branch) — ex. <c>modules/domain</c>.</summary>
    public required string PathKey { get; set; }
    public required string Title { get; set; }
    public required string ContentMd { get; set; }
    /// <summary>JSON array dos arquivos-fonte cobertos.</summary>
    public string? SourceFilesJson { get; set; }
    /// <summary>SHA-256 dos arquivos-fonte cobertos (diff-aware).</summary>
    public string? ContentHash { get; set; }
    public bool Stale { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Ticks de <see cref="CreatedAt"/> — SQLite não ordena DateTimeOffset.</summary>
    public long CreatedAtTicks { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Ticks de <see cref="UpdatedAt"/> — SQLite não ordena DateTimeOffset.</summary>
    public long UpdatedAtTicks { get; set; }
}

/// <inheritdoc />
public sealed class WikiRunConfiguration : IEntityTypeConfiguration<WikiRunRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<WikiRunRecord> b)
    {
        b.ToTable("wiki_runs");
        b.HasKey(e => e.Id);
        b.HasIndex(e => new { e.OrgId, e.Repository, e.Branch });
        b.HasIndex(e => e.CreatedAt);
    }
}

/// <inheritdoc />
public sealed class WikiPageConfiguration : IEntityTypeConfiguration<WikiPageRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<WikiPageRecord> b)
    {
        b.ToTable("wiki_pages");
        b.HasKey(e => e.Id);
        b.HasIndex(e => e.WikiRunId);
        b.HasIndex(e => new { e.Repository, e.Branch, e.PathKey });
        b.HasIndex(e => new { e.OrgId, e.Repository });
    }
}