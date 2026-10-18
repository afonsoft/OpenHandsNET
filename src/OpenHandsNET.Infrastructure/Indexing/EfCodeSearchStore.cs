using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenHandsNET.Application.Indexing;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Indexing;

public sealed class EfCodeSearchStore : ICodeSearchStore
{
    private readonly AppDbContext _db;
    private readonly ILogger<EfCodeSearchStore> _logger;

    public EfCodeSearchStore(AppDbContext db, ILogger<EfCodeSearchStore> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task IndexUnitsAsync(Guid repoIndexId, IEnumerable<CodeUnitDto> units, CancellationToken ct = default)
    {
        var records = units.Select(u => new CodeUnitRecord
        {
            Id = u.Id,
            RepoIndexId = repoIndexId,
            Path = u.Path,
            StartLine = u.StartLine,
            EndLine = u.EndLine,
            Kind = u.Kind,
            SymbolName = u.SymbolName,
            Text = u.Text,
            Hash = u.Hash,
            CreatedAt = DateTimeOffset.UtcNow
        });

        await _db.CodeUnits.AddRangeAsync(records, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SearchCodeResult>> SearchAsync(Guid repoIndexId, string query, int limit = 10, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<SearchCodeResult>();
        }

        var terms = query.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 2)
            .Take(5)
            .ToList();

        var q = _db.CodeUnits.AsNoTracking().Where(c => c.RepoIndexId == repoIndexId);

        if (terms.Count > 0)
        {
            var firstTerm = terms[0];
            q = q.Where(c => c.Text.Contains(firstTerm) || c.Path.Contains(firstTerm) || (c.SymbolName != null && c.SymbolName.Contains(firstTerm)));
        }

        var matched = await q.Take(limit * 3).ToListAsync(ct);

        var scored = matched.Select(c =>
        {
            double score = 0;
            if (c.SymbolName != null && c.SymbolName.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 10.0;
            if (c.Path.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 5.0;

            foreach (var t in terms)
            {
                if (c.Text.Contains(t, StringComparison.OrdinalIgnoreCase)) score += 1.0;
            }

            var snippet = c.Text.Length > 300 ? c.Text[..300] + "..." : c.Text;
            var dto = new CodeUnitDto(c.Id, c.RepoIndexId, c.Path, c.StartLine, c.EndLine, c.Kind, c.SymbolName, c.Text, c.Hash);
            return new SearchCodeResult(dto, score, snippet);
        })
        .OrderByDescending(r => r.Score)
        .Take(limit)
        .ToList();

        return scored;
    }

    public async Task DeleteIndexUnitsAsync(Guid repoIndexId, CancellationToken ct = default)
    {
        var units = await _db.CodeUnits.Where(u => u.RepoIndexId == repoIndexId).ToListAsync(ct);
        if (units.Count > 0)
        {
            _db.CodeUnits.RemoveRange(units);
            await _db.SaveChangesAsync(ct);
        }
    }
}
