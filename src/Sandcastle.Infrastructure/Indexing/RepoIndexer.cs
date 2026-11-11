using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Indexing;
using Sandcastle.Infrastructure.Agent;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Indexing;

public sealed class RepoIndexer : IRepoIndexer
{
    private readonly AppDbContext _db;
    private readonly ICodeSearchStore _searchStore;
    private readonly IRepoContentProvider _contentProvider;
    private readonly IOptions<IndexingOptions> _options;
    private readonly ILogger<RepoIndexer> _logger;

    // Extensões de arquivos permitidos para indexação
    internal static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".ts", ".js", ".tsx", ".jsx", ".py", ".go", ".rs", ".java",
        ".kt", ".cpp", ".c", ".h", ".hpp", ".rb", ".php", ".sh", ".sql", ".md",
        ".json", ".yaml", ".yml", ".proto", ".html", ".css"
    };

    // Regex para identificar declarações de métodos, classes, interfaces ou funções
    private static readonly Regex SymbolRegex = new(
        @"^\s*(?:public|private|protected|internal|static|async|abstract|virtual|override|class|interface|record|struct|enum|fn|def|function|func)\s+([a-zA-Z0-9_<>]+)",
        RegexOptions.Compiled | RegexOptions.Multiline,
        TimeSpan.FromSeconds(1));

    public RepoIndexer(
        AppDbContext db,
        ICodeSearchStore searchStore,
        IRepoContentProvider contentProvider,
        ILogger<RepoIndexer> logger,
        IOptions<IndexingOptions>? options = null)
    {
        _db = db;
        _searchStore = searchStore;
        _contentProvider = contentProvider;
        _logger = logger;
        _options = options ?? Microsoft.Extensions.Options.Options
            .Create(new IndexingOptions());
    }

    public async Task<RepoIndexStatusDto> TriggerIndexAsync(string repository, string branch, Guid? orgId = null, CancellationToken ct = default)
    {
        var existing = await _db.RepoIndexes
            .FirstOrDefaultAsync(r => r.Repository == repository && r.Branch == branch && r.OrgId == orgId, ct);

        if (existing == null)
        {
            existing = new RepoIndexRecord
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                Repository = repository,
                Branch = branch,
                Status = "indexing",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _db.RepoIndexes.AddAsync(existing, ct);
        }
        else
        {
            existing.Status = "indexing";
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            existing.FailureReason = null;
        }

        await _db.SaveChangesAsync(ct);
        var indexId = existing.Id;

        // Job em background — indexa o CONTEÚDO do repo (clone shallow ou
        // checkout local) e persiste o CommitSha resolvido (SPEC-145).
        _ = Task.Run(async () =>
        {
            try
            {
                await RunIndexingAsync(indexId, repository, branch, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while indexing repository {Repository}", repository);
                var failed = await _db.RepoIndexes.FindAsync(indexId);
                if (failed is not null)
                {
                    failed.Status = "failed";
                    failed.FailureReason = SecretRedactor.Redact(ex.Message);
                    failed.UpdatedAt = DateTimeOffset.UtcNow;
                    await _db.SaveChangesAsync(CancellationToken.None);
                }
            }
        }, CancellationToken.None);

        return ToDto(existing);
    }

    public async Task<RepoIndexStatusDto?> GetStatusAsync(Guid repoIndexId, CancellationToken ct = default)
    {
        var record = await _db.RepoIndexes.FindAsync(new object[] { repoIndexId }, ct);
        return record is null ? null : ToDto(record);
    }

    public async Task<RepoIndexStatusDto?> GetStatusByRepoAsync(string repository, string branch, Guid? orgId = null, CancellationToken ct = default)
    {
        var record = await _db.RepoIndexes
            .FirstOrDefaultAsync(r => r.Repository == repository && r.Branch == branch && r.OrgId == orgId, ct);
        return record is null ? null : ToDto(record);
    }

    /// <summary>
    /// SPEC-145 RF-001 — pipeline real: adquire o workdir do repo via
    /// <see cref="IRepoContentProvider"/> (clone shallow do remoto ou
    /// checkout local), persiste o <see cref="RepoIndexRecord.CommitSha"/>
    /// resolvido e indexa os arquivos daquela árvore.
    /// </summary>
    public async Task RunIndexingAsync(Guid indexId, string repository, string branch, CancellationToken ct)
    {
        _logger.LogInformation("Starting repo indexing for {Repo}@{Branch} (IndexId: {Id})",
            repository, branch, indexId);

        var record = await _db.RepoIndexes.FindAsync(new object[] { indexId }, ct);
        if (record == null) return;

        await using var lease = await _contentProvider.AcquireAsync(
            new RepoContentRequest(repository, branch, record.OrgId), ct)
            ?? throw new InvalidOperationException(
                $"no content provider can serve '{repository}'");

        record.CommitSha = lease.CommitSha;

        // Limpa unidades antigas se houver
        await _searchStore.DeleteIndexUnitsAsync(indexId, ct);

        // Varre a árvore do workdir ignorando pastas de build/dependências
        var rootDir = lease.RootPath;
        var codeUnits = new List<CodeUnitDto>();
        var files = new List<string>();
        var maxFiles = _options.Value.MaxFiles;

        var allFiles = EnumerateRepoFiles(rootDir)
            .Where(f => AllowedExtensions.Contains(Path.GetExtension(f)));

        foreach (var file in allFiles)
        {
            if (ct.IsCancellationRequested) break;
            if (maxFiles > 0 && files.Count >= maxFiles) break;

            var relPath = Path.GetRelativePath(rootDir, file).Replace('\\', '/');
            string content;
            try
            {
                content = await File.ReadAllTextAsync(file, ct);
            }
            catch
            {
                continue;
            }

            // Sanitiza com SecretRedactor antes de persistir
            var safeContent = SecretRedactor.Redact(content);
            var chunks = ChunkFile(indexId, relPath, safeContent);
            codeUnits.AddRange(chunks);
            files.Add(relPath);
        }

        // Persiste unidades no store
        await _searchStore.IndexUnitsAsync(indexId, codeUnits, ct);

        record.Status = "ready";
        record.UpdatedAt = DateTimeOffset.UtcNow;
        record.StatsJson = JsonSerializer.Serialize(new
        {
            files_indexed = files.Count,
            units_indexed = codeUnits.Count,
            provider = lease.ProviderKind,
            commit_sha = lease.CommitSha,
            completed_at = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Finished repo indexing for {Repo}@{Branch} ({Sha}). Files: {Files}, Units: {Units}",
            repository, branch, lease.CommitSha, files.Count, codeUnits.Count);
    }

    /// <summary>Arquivos do workdir excluindo .git/build/deps.</summary>
    internal static IEnumerable<string> EnumerateRepoFiles(string rootDir) =>
        Directory.EnumerateFiles(rootDir, "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"));

    public static List<CodeUnitDto> ChunkFile(Guid indexId, string path, string content)
    {
        var units = new List<CodeUnitDto>();
        if (string.IsNullOrWhiteSpace(content)) return units;

        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        if (lines.Length == 0) return units;

        const int chunkSize = 50;
        const int overlap = 10;

        for (int i = 0; i < lines.Length; i += (chunkSize - overlap))
        {
            int startLine = i + 1;
            int count = Math.Min(chunkSize, lines.Length - i);
            int endLine = i + count;

            var chunkLines = lines.Skip(i).Take(count).ToArray();
            var chunkText = string.Join("\n", chunkLines);

            // Identifica se contém definição de símbolo
            string? symbolName = null;
            var match = SymbolRegex.Match(chunkText);
            if (match.Success && match.Groups.Count > 1)
            {
                symbolName = match.Groups[1].Value.Trim();
            }

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chunkText)))[..16];

            units.Add(new CodeUnitDto(
                Id: Guid.NewGuid(),
                RepoIndexId: indexId,
                Path: path,
                StartLine: startLine,
                EndLine: endLine,
                Kind: symbolName != null ? "symbol" : "chunk",
                SymbolName: symbolName,
                Text: chunkText,
                Hash: hash
            ));

            if (endLine >= lines.Length) break;
        }

        return units;
    }

    private static RepoIndexStatusDto ToDto(RepoIndexRecord r) => new(
        r.Id,
        r.OrgId,
        r.Repository,
        r.Branch,
        r.CommitSha,
        r.Status,
        r.StatsJson,
        r.FailureReason,
        r.CreatedAt,
        r.UpdatedAt);
}