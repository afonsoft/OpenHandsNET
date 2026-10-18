using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenHandsNET.Application.Ask;
using OpenHandsNET.Application.Indexing;
using OpenHandsNET.Domain.Conversations;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Ask;

public sealed class AskModeService : IAskModeService
{
    private readonly AppDbContext _db;
    private readonly ICodeSearchStore _searchStore;
    private readonly IRepoIndexer _repoIndexer;
    private readonly ILogger<AskModeService> _logger;

    public AskModeService(
        AppDbContext db,
        ICodeSearchStore searchStore,
        IRepoIndexer repoIndexer,
        ILogger<AskModeService> logger)
    {
        _db = db;
        _searchStore = searchStore;
        _repoIndexer = repoIndexer;
        _logger = logger;
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

        // Executa busca lexical/semântica nas unidades de código
        var results = await _searchStore.SearchAsync(index.Id, request.Question, limit: 5, ct);

        foreach (var r in results)
        {
            citations.Add(new AskCitation(
                Path: r.Unit.Path,
                StartLine: r.Unit.StartLine,
                EndLine: r.Unit.EndLine,
                Snippet: r.Snippet
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
                answerBuilder.AppendLine($"- `[{c.Path}:{c.StartLine}-{c.EndLine}]`");
            }

            answerBuilder.AppendLine("\n**Resumo de Análise:**");
            answerBuilder.AppendLine($"A implementação relacionada ao tópico '{request.Question}' está concentrada principalmente nos arquivos listados acima.");
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

        // Cria conversa persistida em modo ativo
        var conv = new OpenHandsNET.Infrastructure.Persistence.Conversations.ConversationRecord
        {
            Id = convId,
            Title = request.Title ?? $"Investigation from Ask Session {request.SessionId:N}",
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _db.Conversations.AddAsync(conv, ct);
        await _db.SaveChangesAsync(ct);

        return new PromoteAskSessionResponse(convId, "READY");
    }
}
