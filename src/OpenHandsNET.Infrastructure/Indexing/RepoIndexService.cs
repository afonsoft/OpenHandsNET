using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenHandsNET.Application.Indexing;
using OpenHandsNET.Infrastructure.Agent;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Indexing;

/// <summary>
/// SPEC-60 RF-001/RF-004: indexação em host worker (clone shallow →
/// chunking → persistência) + busca FTS5/ILIKE + admin de índices.
/// </summary>
public sealed class RepoIndexService(
    AppDbContext db, IServiceScopeFactory scopeFactory,
    IConfiguration config, ILogger<RepoIndexService> logger)
    : IRepoIndexService, IRepoIndexer, ICodeSearchStore
{
    private int MaxFiles => config.GetValue("Index:MaxFiles", 4000);
    private int MaxFileKb => config.GetValue("Index:MaxFileKb", 256);
    private int MaxRepoMb => config.GetValue("Index:MaxRepoMb", 200);

    // ---- IRepoIndexService ---------------------------------------------

    public async Task<IReadOnlyList<RepoIndexInfo>> ListAsync(
        CancellationToken ct) =>
        await db.RepoIndexes.OrderBy(r => r.Repository)
            .Select(r => ToInfo(r)).ToListAsync(ct);

    public async Task<RepoIndexInfo?> GetAsync(Guid id, CancellationToken ct)
    {
        var r = await db.RepoIndexes.FirstOrDefaultAsync(
            x => x.Id == id, ct);
        return r is null ? null : ToInfo(r);
    }

    public async Task<RepoIndexInfo?> GetByRepoAsync(
        string repository, CancellationToken ct)
    {
        var r = await db.RepoIndexes.FirstOrDefaultAsync(
            x => x.Repository == repository, ct);
        return r is null ? null : ToInfo(r);
    }

    public async Task<RepoIndexInfo> AddAsync(
        string repository, string? branch, CancellationToken ct)
    {
        var existing = await db.RepoIndexes.FirstOrDefaultAsync(
            r => r.Repository == repository, ct);
        if (existing is not null)
            return ToInfo(existing);
        var rec = new RepoIndexRecord
        {
            Id = Guid.NewGuid(), OrgId = db.CurrentOrgId,
            Repository = repository,
            Branch = branch ?? "",
            Status = "indexing",
        };
        db.RepoIndexes.Add(rec);
        await db.SaveChangesAsync(ct);
        return ToInfo(rec);
    }

    public async Task<bool> RequestReindexAsync(
        Guid id, CancellationToken ct)
    {
        var rec = await db.RepoIndexes.FirstOrDefaultAsync(
            r => r.Id == id, ct);
        if (rec is null)
            return false;
        rec.Status = "indexing";
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task MarkStaleAsync(string repository, CancellationToken ct)
    {
        var rec = await db.RepoIndexes.FirstOrDefaultAsync(
            r => r.Repository == repository, ct);
        if (rec is { Status: "ready" })
        {
            rec.Status = "stale";
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var rec = await db.RepoIndexes.FirstOrDefaultAsync(
            r => r.Id == id, ct);
        if (rec is null)
            return false;
        db.CodeUnits.RemoveRange(
            db.CodeUnits.Where(u => u.RepoIndexId == id));
        db.RepoIndexes.Remove(rec);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<string?> ReadFileAsync(
        Guid id, string path, CancellationToken ct)
    {
        var units = await db.CodeUnits
            .Where(u => u.RepoIndexId == id && u.Path == path)
            .OrderBy(u => u.StartLine).ToListAsync(ct);
        if (units.Count == 0)
            return null;
        return string.Join('\n', units.Select(u => u.Text));
    }

    // ---- ICodeSearchStore -----------------------------------------------

    public async Task<IReadOnlyList<CodeUnitHit>> SearchAsync(
        Guid repoIndexId, string query, int limit, CancellationToken ct)
    {
        var terms = query.Split(
            [' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
            return [];

        if (db.Database.IsSqlite())
        {
            var fts = string.Join(" AND ",
                terms.Select(t => $"\"{t.Replace("\"", "\"\"")}\"*"));
            try
            {
                var rows = await db.Database
                    .SqlQuery<FtsRow>($"""
                        SELECT u.id AS Id, u.path AS Path,
                               u.start_line AS StartLine,
                               u.end_line AS EndLine, u.kind AS Kind,
                               u.text AS Text, u.hash AS Hash,
                               bm25(code_units_fts) AS Score
                        FROM code_units_fts f
                        JOIN code_units u ON u.id = f.unit_id
                        WHERE f.repo_index_id = {repoIndexId}
                          AND code_units_fts MATCH {fts}
                        ORDER BY Score LIMIT {limit}
                        """).ToListAsync(ct);
                return rows.Select(r => new CodeUnitHit(
                    new CodeUnit(r.Path, r.StartLine, r.EndLine, r.Kind,
                        r.Text, r.Hash), r.Score)).ToList();
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "FTS5 fallback to LIKE");
            }
        }

        // Postgres (e fallback): ILIKE em path/text com ranking simples
        // (hits em path pesam mais).
        var q = db.CodeUnits.Where(u => u.RepoIndexId == repoIndexId);
        var candidates = await q
            .Where(u => terms.All(t =>
                EF.Functions.ILike(u.Text, $"%{t}%")
                || EF.Functions.ILike(u.Path, $"%{t}%")))
            .Take(limit * 4).ToListAsync(ct);
        return candidates.Select(u =>
            {
                var score = terms.Sum(t =>
                    (u.Path.Contains(t,
                        StringComparison.OrdinalIgnoreCase) ? 3.0 : 0)
                    + CountOccurrences(u.Text, t));
                return new CodeUnitHit(ToUnit(u), score);
            })
            .OrderByDescending(h => h.Score).Take(limit).ToList();
    }

    private static int CountOccurrences(string text, string term)
    {
        var count = 0; var i = 0;
        while ((i = text.IndexOf(term, i,
            StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++; i += term.Length;
            if (count > 50) break;
        }
        return count;
    }

    // ---- IRepoIndexer ----------------------------------------------------

    public async Task IndexAsync(Guid repoIndexId, CancellationToken ct)
    {
        RepoIndexRecord? rec;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var sdb = scope.ServiceProvider
                .GetRequiredService<AppDbContext>();
            rec = await sdb.RepoIndexes.FirstOrDefaultAsync(
                r => r.Id == repoIndexId, ct);
            if (rec is null)
                return;
            rec.Status = "indexing";
            rec.Error = null;
            await sdb.SaveChangesAsync(ct);
        }

        var workDir = Path.Combine(
            Path.GetTempPath(), $"ohn-idx-{repoIndexId:N}");
        try
        {
            var url = rec.Repository.StartsWith("http",
                StringComparison.Ordinal)
                ? rec.Repository
                : $"https://github.com/{rec.Repository}.git";
            await RunGit(
                $"clone --depth 1 {(rec.Branch.Length > 0 ? $"--branch {rec.Branch} " : "")}--single-branch '{url}' '{workDir}'",
                null, ct);
            var sha = await RunGit("rev-parse HEAD", workDir, ct);

            var ignores = LoadIndexIgnore(workDir);
            var files = Directory
                .EnumerateFiles(workDir, "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains(
                    $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
                .Select(f => Path.GetRelativePath(workDir, f)
                    .Replace('\\', '/'))
                .Where(p =>
                    CodeChunker.IndexableExtensions.Contains(
                        Path.GetExtension(p))
                    || Path.GetFileName(p).Equals("Dockerfile",
                        StringComparison.OrdinalIgnoreCase))
                .Where(p => !ignores.Any(r => r.IsMatch(p)))
                .Take(MaxFiles).ToList();

            var units = new List<CodeUnitRecord>();
            var fileCount = 0;
            var langs = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;
            foreach (var rel in files)
            {
                var full = Path.Combine(workDir,
                    rel.Replace('/', Path.DirectorySeparatorChar));
                var fi = new FileInfo(full);
                if (fi.Length > MaxFileKb * 1024L)
                    continue;
                totalBytes += fi.Length;
                if (totalBytes > MaxRepoMb * 1024L * 1024L)
                    break;
                string content;
                try { content = await File.ReadAllTextAsync(full, ct); }
                catch { continue; }
                var lines = content.Replace("\r\n", "\n").Split('\n');
                fileCount++;
                var ext = Path.GetExtension(rel);
                langs[ext.Length > 0 ? ext : "(none)"] =
                    langs.GetValueOrDefault(ext) + 1;
                foreach (var (start, end, kind, text) in
                    CodeChunker.Chunk(rel, lines))
                {
                    var redacted = SecretRedactor.Redact(text);
                    units.Add(new CodeUnitRecord
                    {
                        Id = Guid.NewGuid(),
                        OrgId = rec.OrgId,
                        RepoIndexId = repoIndexId,
                        Path = rel,
                        StartLine = start + 1,
                        EndLine = end + 1,
                        Kind = kind,
                        Text = redacted,
                        Hash = Convert.ToHexString(
                            SHA256.HashData(
                                System.Text.Encoding.UTF8.GetBytes(
                                    redacted)))[..16],
                    });
                }
            }

            await using var scope2 = scopeFactory.CreateAsyncScope();
            var sdb2 = scope2.ServiceProvider
                .GetRequiredService<AppDbContext>();
            await using var tx = await sdb2.Database
                .BeginTransactionAsync(ct);
            var fresh = await sdb2.RepoIndexes
                .IgnoreQueryFilters()
                .FirstAsync(r => r.Id == repoIndexId, ct);
            sdb2.CodeUnits.RemoveRange(sdb2.CodeUnits
                .IgnoreQueryFilters()
                .Where(u => u.RepoIndexId == repoIndexId));
            sdb2.CodeUnits.AddRange(units);
            fresh.CommitSha = sha.Trim();
            fresh.Status = "ready";
            fresh.IndexedAt = DateTimeOffset.UtcNow;
            fresh.StatsJson = JsonSerializer.Serialize(new
            {
                files = fileCount,
                units = units.Count,
                bytes = totalBytes,
                languages = langs.OrderByDescending(kv => kv.Value)
                    .ToDictionary(kv => kv.Key, kv => kv.Value),
            });
            await sdb2.SaveChangesAsync(ct);
            await RebuildFtsAsync(sdb2, repoIndexId, units, ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e,
                "index failed for {RepoIndexId}", repoIndexId);
            await using var scope3 = scopeFactory.CreateAsyncScope();
            var sdb3 = scope3.ServiceProvider
                .GetRequiredService<AppDbContext>();
            var failed = await sdb3.RepoIndexes
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Id == repoIndexId, ct);
            if (failed is not null)
            {
                failed.Status = "failed";
                failed.Error = SecretRedactor.Redact(
                    e.Message.Length > 500
                        ? e.Message[..500] : e.Message);
                await sdb3.SaveChangesAsync(ct);
            }
        }
        finally
        {
            try { Directory.Delete(workDir, true); }
            catch { /* best effort */ }
        }
    }

    private static async Task RebuildFtsAsync(
        AppDbContext sdb, Guid repoIndexId,
        List<CodeUnitRecord> units, CancellationToken ct)
    {
        if (!sdb.Database.IsSqlite())
            return;
        await sdb.Database.ExecuteSqlAsync($"""
            DELETE FROM code_units_fts WHERE repo_index_id = {repoIndexId}
            """, ct);
        foreach (var u in units)
        {
            await sdb.Database.ExecuteSqlAsync($"""
                INSERT INTO code_units_fts
                    (unit_id, repo_index_id, path, text)
                VALUES ({u.Id.ToString()}, {repoIndexId.ToString()},
                        {u.Path}, {u.Text})
                """, ct);
        }
    }

    private static List<Regex> LoadIndexIgnore(string workDir)
    {
        var f = Path.Combine(workDir, ".openhands", "indexignore");
        if (!File.Exists(f))
            return [];
        return File.ReadAllLines(f)
            .Select(CodeChunker.GlobToRegex)
            .Where(r => r is not null).Cast<Regex>().ToList();
    }

    private static async Task<string> RunGit(
        string args, string? workDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir ?? "",
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("git not found");
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {args.Split(' ')[0]} failed: "
                + SecretRedactor.Redact(stderr.Trim()));
        }
        return stdout;
    }

    private static RepoIndexInfo ToInfo(RepoIndexRecord r)
    {
        int files = 0, units = 0;
        if (r.StatsJson is { } j)
        {
            try
            {
                var s = JsonDocument.Parse(j).RootElement;
                files = s.TryGetProperty("files", out var f)
                    ? f.GetInt32() : 0;
                units = s.TryGetProperty("units", out var u)
                    ? u.GetInt32() : 0;
            }
            catch { }
        }
        return new RepoIndexInfo(r.Id, r.OrgId, r.Repository, r.Branch,
            r.CommitSha, Enum.Parse<RepoIndexStatus>(r.Status, true),
            r.Error, units, files, r.CreatedAt, r.IndexedAt);
    }

    private static CodeUnit ToUnit(CodeUnitRecord u) => new(
        u.Path, u.StartLine, u.EndLine, u.Kind, u.Text, u.Hash);

    private sealed record FtsRow(
        string Id, string Path, int StartLine, int EndLine,
        string Kind, string Text, string Hash, double Score);
}

/// <summary>Fila em memória de reindex (SPEC-60 RF-001).</summary>
public sealed class RepoIndexQueue : IRepoIndexQueue
{
    private readonly System.Threading.Channels.Channel<Guid> _ch =
        System.Threading.Channels.Channel.CreateUnbounded<Guid>();

    public void Enqueue(Guid repoIndexId) =>
        _ch.Writer.TryWrite(repoIndexId);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) =>
        _ch.Reader.ReadAllAsync(ct);
}

/// <summary>Worker hosted que consome a fila de reindex.</summary>
public sealed class RepoIndexWorker(
    IRepoIndexQueue queue, IServiceScopeFactory scopeFactory,
    ILogger<RepoIndexWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var id in queue.ReadAllAsync(ct))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider
                    .GetRequiredService<IRepoIndexer>()
                    .IndexAsync(id, ct);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "reindex {Id} failed", id);
            }
        }
    }
}
