using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using OpenHandsNET.Application.Identity;
using OpenHandsNET.Application.Knowledge;
using OpenHandsNET.Application.Memory;
using OpenHandsNET.Infrastructure.Agent;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Knowledge;

/// <summary>SPEC-63: sugestão de conhecimento na fila de revisão.</summary>
public sealed class KnowledgeSuggestionRecord : IOrgScoped
{
    public required Guid Id { get; set; }
    public required Guid? OrgId { get; set; }
    public required string Scope { get; set; }
    public string? RepoPath { get; set; }
    public required string Title { get; set; }
    public required string Content { get; set; }
    /// <summary>entry|merge|conflict.</summary>
    public string Kind { get; set; } = "entry";
    /// <summary>pending|approved|dismissed.</summary>
    public string Status { get; set; } = "pending";
    public string? Reason { get; set; }
    public string SourceConversationIdsJson { get; set; } = "[]";
    public string SourceEntryIdsJson { get; set; } = "[]";
    public string? Resolution { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// SPEC-63 RF-001..003: geração de sugestões, fila de revisão
/// (approve grava via <see cref="IMemoryService"/>), groom de
/// dedup/conflict/stale — read+propose, nunca deleta.
/// </summary>
public sealed class KnowledgeService(
    AppDbContext db, IMemoryService memory, IConfiguration cfg)
    : IKnowledgeService
{
    public async Task<IReadOnlyList<KnowledgeSuggestionInfo>>
        ListSuggestionsAsync(SuggestionStatus? status, string? scope,
            CancellationToken ct)
    {
        var q = db.KnowledgeSuggestions.AsQueryable();
        if (status is not null)
            q = q.Where(s =>
                s.Status == status.Value.ToString().ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(scope))
            q = q.Where(s => s.Scope == scope);
        // SQLite não traduz ORDER BY DateTimeOffset — ordena em memória.
        return (await q.ToListAsync(ct))
            .OrderByDescending(s => s.CreatedAt)
            .Take(200).Select(ToInfo).ToList();
    }

    public async Task<KnowledgeSuggestionInfo> SuggestAsync(
        string title, string content, string scope, string? repoPath,
        SuggestionKind kind, string? reason,
        IReadOnlyList<Guid>? sourceConversationIds,
        IReadOnlyList<Guid>? sourceEntryIds, CancellationToken ct)
    {
        var rec = new KnowledgeSuggestionRecord
        {
            Id = Guid.NewGuid(),
            OrgId = db.CurrentOrgId,
            Scope = scope,
            RepoPath = repoPath,
            Title = title,
            Content = SecretRedactor.Redact(content),
            Kind = kind.ToString().ToLowerInvariant(),
            Reason = reason,
            SourceConversationIdsJson = JsonSerializer.Serialize(
                sourceConversationIds ?? []),
            SourceEntryIdsJson = JsonSerializer.Serialize(
                sourceEntryIds ?? []),
        };
        db.KnowledgeSuggestions.Add(rec);
        await db.SaveChangesAsync(ct);
        return ToInfo(rec);
    }

    public async Task<KnowledgeSuggestionInfo> ApproveAsync(
        Guid id, string? contentOverride, string? actorUserId,
        CancellationToken ct)
    {
        var rec = await RequirePendingAsync(id, ct);
        var content = contentOverride ?? rec.Content;
        // single-tenant (sem org efetiva) grava sob Guid.Empty —
        // mesmo bucket das memory records sem org.
        var orgId = rec.OrgId ?? db.CurrentOrgId ?? Guid.Empty;
        // merge: consolida conteúdo nas entries fonte (marca a menor
        // como apontada — nunca deleta).
        await memory.RememberOrgAsync(
            orgId, Slug(rec.Title), content, actorUserId, ct);
        rec.Status = "approved";
        rec.Resolution = contentOverride is null
            ? "approved" : "approved_with_edit";
        await db.SaveChangesAsync(ct);
        return ToInfo(rec);
    }

    public async Task<KnowledgeSuggestionInfo> DismissAsync(
        Guid id, string? reason, CancellationToken ct)
    {
        var rec = await RequirePendingAsync(id, ct);
        rec.Status = "dismissed";
        rec.Resolution = reason ?? "dismissed";
        await db.SaveChangesAsync(ct);
        return ToInfo(rec);
    }

    public async Task<KnowledgeGroomReport> GroomAsync(
        CancellationToken ct)
    {
        var entries = await db.MemoryRecords
            .Where(m => m.DeletedAt == null)
            .ToListAsync(ct);
        var staleDays = cfg.GetValue("Knowledge:StaleDays", 90);
        var staleBefore = DateTimeOffset.UtcNow
            .AddDays(-staleDays);
        var stale = entries
            .Where(m => m.UpdatedAt < staleBefore)
            .Select(m => m.Id).ToList();

        var duplicates = new List<(Guid, Guid, double)>();
        var conflicts = new List<(Guid, Guid)>();
        var tokenSets = entries.ToDictionary(
            e => e.Id, e => Tokens(e.Text));
        for (var i = 0; i < entries.Count; i++)
        {
            for (var j = i + 1; j < entries.Count; j++)
            {
                var a = entries[i]; var b = entries[j];
                var sim = Jaccard(
                    tokenSets[a.Id], tokenSets[b.Id]);
                if (sim >= 0.7)
                {
                    duplicates.Add((a.Id, b.Id, sim));
                }
                else if (sim >= 0.4 && Negated(a.Text, b.Text))
                {
                    conflicts.Add((a.Id, b.Id));
                }
            }
        }
        return new KnowledgeGroomReport(duplicates, conflicts, stale);
    }

    // ---- internals -----------------------------------------------------

    private async Task<KnowledgeSuggestionRecord> RequirePendingAsync(
        Guid id, CancellationToken ct)
    {
        var rec = await db.KnowledgeSuggestions
            .FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new KeyNotFoundException("suggestion not found");
        if (rec.Status != "pending")
            throw new SuggestionResolvedException();
        return rec;
    }

    private static string Slug(string title)
    {
        var slug = new string(title.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray()).Trim('-');
        return slug.Length > 0 ? slug[..Math.Min(64, slug.Length)]
            : "entry";
    }

    private static HashSet<string> Tokens(string text) =>
        text.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2).ToHashSet();

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var inter = a.Intersect(b).Count();
        return (double)inter / (a.Count + b.Count - inter);
    }

    private static bool Negated(string a, string b)
    {
        string[] negs = ["não", "not ", "never", "nunca", "desabilit",
            "disable", "without", "sem "];
        var na = negs.Count(n => a.Contains(n,
            StringComparison.OrdinalIgnoreCase));
        var nb = negs.Count(n => b.Contains(n,
            StringComparison.OrdinalIgnoreCase));
        return (na % 2) != (nb % 2);
    }

    private static KnowledgeSuggestionInfo ToInfo(
        KnowledgeSuggestionRecord r) => new(
        r.Id, r.OrgId, r.Scope, r.RepoPath, r.Title, r.Content,
        Enum.Parse<SuggestionKind>(r.Kind, true),
        Enum.Parse<SuggestionStatus>(r.Status, true),
        r.Reason,
        JsonSerializer.Deserialize<List<Guid>>(
            r.SourceConversationIdsJson) ?? [],
        JsonSerializer.Deserialize<List<Guid>>(
            r.SourceEntryIdsJson) ?? [],
        r.Resolution, r.CreatedAt);
}

public sealed class SuggestionResolvedException()
    : Exception("suggestion already resolved");

/// <summary>
/// SPEC-63 RF-003: groom diário — gera o relatório e cria sugestões
/// <c>merge</c>/<c>conflict</c> na fila (nunca deleta).
/// </summary>
public sealed class KnowledgeGroomer(
    IServiceScopeFactory scopeFactory,
    ILogger<KnowledgeGroomer> logger,
    IConfiguration cfg) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = cfg.GetValue("Knowledge:GroomIntervalHours", 24);
        using var timer = new PeriodicTimer(
            TimeSpan.FromHours(Math.Clamp(period, 1, 24 * 30)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope =
                    scopeFactory.CreateAsyncScope();
                var svc = scope.ServiceProvider
                    .GetRequiredService<IKnowledgeService>();
                var report = await svc.GroomAsync(stoppingToken);
                foreach (var (a, b, sim) in report.Duplicates)
                {
                    await svc.SuggestAsync(
                        "merge: duplicate memories",
                        $"Entries {a} and {b} look duplicate "
                            + $"(similarity {sim:F2}) — consider merge.",
                        "org", null, SuggestionKind.Merge,
                        "groom", null, [a, b], stoppingToken);
                }
                foreach (var (a, b) in report.Conflicts)
                {
                    await svc.SuggestAsync(
                        "conflict: contradictory memories",
                        $"Entries {a} and {b} may contradict "
                            + "each other — human review needed.",
                        "org", null, SuggestionKind.Conflict,
                        "groom", null, [a, b], stoppingToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "knowledge groom failed");
            }
        }
    }
}