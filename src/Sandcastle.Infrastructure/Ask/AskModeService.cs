using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Ask;
using Sandcastle.Application.Indexing;
using Sandcastle.Application.Wiki;
using Sandcastle.Domain.Conversations;
using Sandcastle.Infrastructure.Indexing;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Wiki;

namespace Sandcastle.Infrastructure.Ask;

public sealed class AskModeService : IAskModeService
{
    private readonly AppDbContext _db;
    private readonly ICodeSearchStore _searchStore;
    private readonly IOptions<WikiOptions> _wikiOptions;
    private readonly ILogger<AskModeService> _logger;

    public AskModeService(
        AppDbContext db,
        ICodeSearchStore searchStore,
        ILogger<AskModeService> logger,
        IOptions<WikiOptions>? wikiOptions = null)
    {
        _db = db;
        _searchStore = searchStore;
        _logger = logger;
        _wikiOptions = wikiOptions
            ?? Microsoft.Extensions.Options.Options.Create(new WikiOptions());
    }

    public async Task<AskResponse> AskAsync(AskRequest request, CancellationToken ct = default)
    {
        var branch = request.Branch ?? "main";
        var index = await _db.RepoIndexes
            .FirstOrDefaultAsync(r => r.Repository == request.Repository && r.Branch == branch && r.OrgId == request.OrgId, ct);

        var citations = new List<AskCitation>();
        var answerBuilder = new StringBuilder();

        if (index == null || index.Status != "ready")
        {
            return new AskResponse(
                SessionId: Guid.NewGuid(),
                Answer: $"O repositório '{request.Repository}' (branch: '{branch}') ainda não foi indexado. Dispare a indexação primeiro.",
                Citations: citations,
                CanPromoteToAgent: false);
        }

        // SPEC-145 RF-001: permalink do provider — base da conexão SCM
        // quando houver; só emitido com CommitSha indexado.
        var connection = await RepoConnectionLookup.FindCoveringAsync(
            _db, request.Repository, request.OrgId, ct);

        // Executa busca lexical/semântica nas unidades de código
        var results = await _searchStore.SearchAsync(index.Id, request.Question, limit: 5, ct);

        foreach (var r in results)
        {
            citations.Add(new AskCitation(
                Path: r.Unit.Path,
                StartLine: r.Unit.StartLine,
                EndLine: r.Unit.EndLine,
                Snippet: r.Snippet,
                Permalink: WikiPermalink.Build(
                    connection, request.Repository, index.CommitSha,
                    r.Unit.Path, r.Unit.StartLine, r.Unit.EndLine)
            ));
        }

        answerBuilder.AppendLine($"Com base no índice do repositório `{request.Repository}`:\n");
        if (citations.Count == 0)
        {
            answerBuilder.AppendLine("Nenhum trecho de código relevante foi encontrado diretamente para sua pergunta.");
        }
        else
        {
            answerBuilder.AppendLine("Foram localizados os seguintes componentes relacionados:");
            foreach (var c in citations)
            {
                var label = $"`[{c.Path}:{c.StartLine}-{c.EndLine}]`";
                answerBuilder.AppendLine(c.Permalink is { } link
                    ? $"- [{c.Path}:{c.StartLine}-{c.EndLine}]({link})"
                    : $"- {label}");
            }

            answerBuilder.AppendLine("\n**Resumo de Análise:**");
            answerBuilder.AppendLine($"A implementação relacionada ao tópico '{request.Question}' está concentrada principalmente nos arquivos listados acima.");
        }

        // SPEC-145 RF-003: páginas da wiki como contexto adicional
        // (prompt enrichment — feature flag Wiki:AskEnrichment).
        if (_wikiOptions.Value.AskEnrichment)
        {
            var wikiRun = await _db.WikiRuns
                .Where(w => w.Repository == request.Repository
                    && w.Branch == branch
                    && w.OrgId == request.OrgId
                    && w.Status == WikiRunStatuses.Completed)
                .OrderByDescending(w => w.CreatedAtTicks)
                .FirstOrDefaultAsync(ct);
            if (wikiRun is not null)
            {
                var pages = await _db.WikiPages
                    .Where(p => p.WikiRunId == wikiRun.Id && !p.Stale)
                    .OrderBy(p => p.SortOrder)
                    .Select(p => new { p.PathKey, p.Title })
                    .ToListAsync(ct);
                if (pages.Count > 0)
                {
                    answerBuilder.AppendLine("\n**Wiki do repositório (contexto adicional):**");
                    foreach (var p in pages.Take(10))
                    {
                        answerBuilder.AppendLine($"- `{p.PathKey}` — {p.Title}");
                    }
                }
            }
        }

        return new AskResponse(
            SessionId: Guid.NewGuid(),
            Answer: answerBuilder.ToString(),
            Citations: citations,
            CanPromoteToAgent: citations.Count > 0);
    }

    public async Task<PromoteAskSessionResponse> PromoteToAgentAsync(PromoteAskSessionRequest request, CancellationToken ct = default)
    {
        var convId = Guid.NewGuid();
        _logger.LogInformation("Promoting ask session {SessionId} to agent conversation {ConvId}", request.SessionId, convId);

        var conv = new Sandcastle.Infrastructure.Conversations.ConversationRecord
        {
            Id = convId,
            Title = request.Title ?? $"Investigation from Ask Session {request.SessionId:N}",
            ExecutionStatus = "READY",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _db.Conversations.AddAsync(conv, ct);
        await _db.SaveChangesAsync(ct);

        return new PromoteAskSessionResponse(convId, "READY");
    }
}