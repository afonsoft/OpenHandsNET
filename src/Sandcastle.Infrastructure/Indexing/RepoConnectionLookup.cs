using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Scm;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Scm;

namespace Sandcastle.Infrastructure.Indexing;

/// <summary>
/// Lookup compartilhado de conexão SCM que cobre um repo (SPEC-145):
/// usado pelo clone provider e pelo permalink builder da wiki.
/// </summary>
internal static class RepoConnectionLookup
{
    /// <summary>Primeira conexão (org-scoped quando orgId vem) cobrindo o repo.</summary>
    internal static async Task<ScmConnection?> FindCoveringAsync(
        AppDbContext db, string repository, Guid? orgId,
        CancellationToken ct)
    {
        var query = db.ScmConnections.IgnoreQueryFilters().AsQueryable();
        if (orgId is { } oid)
        {
            query = query.Where(c => c.OrgId == oid);
        }
        var rows = await query.ToListAsync(ct);
        return rows.Select(ScmConnectionService.ToModel)
            .Where(c => c.Repos.Count == 0
                || c.Repos.Contains(repository,
                    StringComparer.OrdinalIgnoreCase))
            .OrderBy(c => c.CreatedAt)
            .FirstOrDefault();
    }
}