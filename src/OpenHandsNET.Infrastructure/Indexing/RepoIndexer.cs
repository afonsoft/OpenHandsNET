using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using OpenHandsNET.Application.Indexing;
using OpenHandsNET.Infrastructure.Agent;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Indexing;

public sealed class RepoIndexer : IRepoIndexer
{
    private readonly AppDbContext _db;
    private readonly ICodeSearchStore _searchStore;
    private readonly ILogger<RepoIndexer> _logger;

    // Extensões de arquivos permitidos para indexação
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
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
        ILogger<RepoIndexer> logger)
    {
        _db = db;
        _searchStore = searchStore;
        _logger = logger;
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

        // Se estivermos rodando no repositório local corrente, indexamos os arquivos do workspace
        _ = Task.Run(async () =>
        {
            try
            {
                await RunIndexingAsync(existing.Id, repository, branch, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while indexing repository {Repository}", repository);
                existing.Status = "failed";
                existing.FailureReason = SecretRedactor.Redact(ex.Message);
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(CancellationToken.None);
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

    public async Task RunIndexingAsync(Guid indexId, string repository, string branch, CancellationToken ct)
    {
        _logger.LogInformation("Starting repo indexing for {Repo} (IndexId: {Id})", repository, indexId);

        var record = await _db.RepoIndexes.FindAsync(new object[] { indexId }, ct);
        if (record == null) return;

        // Limpa unidades antigas se houver
        await _searchStore.DeleteIndexUnitsAsync(indexId, ct);

        // Varre diretórios do workspace ignorando pastas de build/dependências
        var rootDir = Directory.GetCurrentDirectory();
        var codeUnits = new List<CodeUnitDto>();
        int filesCount = 0;

        var allFiles = Directory.EnumerateFiles(rootDir, "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}") &&
                        AllowedExtensions.Contains(Path.GetExtension(f)));

        foreach (var file in allFiles)
        {
            if (ct.IsCancellationRequested) break;

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
            filesCount++;
        }

        // Persiste unidades no store
        await _searchStore.IndexUnitsAsync(indexId, codeUnits, ct);

        record.Status = "ready";
        record.UpdatedAt = DateTimeOffset.UtcNow;
        record.StatsJson = JsonSerializer.Serialize(new
        {
            files_indexed = filesCount,
            units_indexed = codeUnits.Count,
            completed_at = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Finished repo indexing for {Repo}. Files: {Files}, Units: {Units}", repository, filesCount, codeUnits.Count);
    }

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