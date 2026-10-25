using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using OpenHandsNET.Application.Insights;
using OpenHandsNET.Infrastructure.Agent;
using OpenHandsNET.Infrastructure.Conversations;
using OpenHandsNET.Infrastructure.Eventing;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Insights;

public sealed class SessionInsightService : ISessionInsightService
{
    private readonly AppDbContext _db;
    private readonly ILogger<SessionInsightService> _logger;

    private readonly IServiceProvider _services;

    public SessionInsightService(
        AppDbContext db,
        ILogger<SessionInsightService> logger,
        IServiceProvider services)
    {
        _db = db;
        _logger = logger;
        _services = services;
    }

    public async Task<SessionInsightDto> AnalyzeSessionAsync(Guid conversationId, bool force = false, CancellationToken ct = default)
    {
        var existing = await _db.SessionInsights
            .FirstOrDefaultAsync(s => s.ConversationId == conversationId, ct);

        if (existing != null && !force)
        {
            return ToDto(existing);
        }

        var conv = await _db.Conversations.FindAsync(new object[] { conversationId }, ct);
        if (conv == null)
        {
            throw new KeyNotFoundException($"Conversation '{conversationId}' not found");
        }

        _logger.LogInformation("Generating session insight for conversation {ConvId}", conversationId);

        // Amostragem de eventos associados para análise heurística
        var events = await _db.Events
            .Where(e => e.ConversationId == conversationId)
            .OrderBy(e => e.TimestampTicks)
            .Take(100)
            .ToListAsync(ct);

        var a = BuildAnalysis(events, conv);

        var insightRecord = existing ?? new SessionInsightRecord
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Outcome = a.Outcome,
            CreatedAt = DateTimeOffset.UtcNow
        };

        insightRecord.OrgId = conv.OrgId;
        insightRecord.IssuesJson = JsonSerializer.Serialize(a.Issues);
        insightRecord.DeadEndsJson = JsonSerializer.Serialize(a.DeadEnds);
        insightRecord.PhaseCostsJson = JsonSerializer.Serialize(a.PhaseCosts);
        insightRecord.LearningsJson = JsonSerializer.Serialize(a.Learnings);
        insightRecord.SuggestedPrompt = a.SuggestedPrompt;
        insightRecord.Model = conv.LlmModel ?? "gpt-4o";

        if (existing == null)
        {
            await _db.SessionInsights.AddAsync(insightRecord, ct);
        }

        await _db.SaveChangesAsync(ct);
        await SuggestLearningsAsync(a.Learnings, conv, conversationId, ct);

        return ToDto(insightRecord);
    }

    private sealed record Analysis(
        List<SessionIssue> Issues, List<string> DeadEnds,
        List<string> Learnings, List<PhaseCost> PhaseCosts,
        string Outcome, string? SuggestedPrompt);

    private static Analysis BuildAnalysis(
        List<EventRecord> events, ConversationRecord conv)
    {
        var issues = new List<SessionIssue>();
        var deadEnds = new List<string>();
        var learnings = new List<string>();

        var flagged = events.Where(ev =>
            ev.Payload.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            ev.Payload.Contains("fail", StringComparison.OrdinalIgnoreCase));
        var hasError = flagged.Any();
        issues.AddRange(flagged.Select(ev =>
            new SessionIssue("command_error", "medium",
                SecretRedactor.Redact(
                    ev.Payload[..Math.Min(120, ev.Payload.Length)]))));

        var outcome = conv.ExecutionStatus switch
        {
            "COMPLETED" or "ready" => hasError ? "partial" : "success",
            "FAILED" or "error" => "failed",
            _ => "unclear"
        };

        if (hasError)
        {
            deadEnds.Add("Tentativa de execução de comando ou compilação gerou erro antes da retentativa");
            learnings.Add("Recomenda-se validar pré-requisitos de ambiente e dependências antes de comandos destrutivos");
        }
        else
        {
            learnings.Add("Sequência de passos executada de forma linear sem regressões");
        }

        var phaseCosts = new List<PhaseCost>
        {
            new("planning", 1500, 2.5),
            new("tool_execution", 4200, 8.0),
            new("verification", 1800, 3.2),
        };

        var suggestedPrompt = hasError
            ? $"Seja mais explícito ao definir os arquivos-alvo e adicione a instrução 'verifique com dotnet build antes de concluir' para evitar erros na tarefa original: {conv.Title}"
            : null;

        return new Analysis(issues, deadEnds, learnings, phaseCosts,
            outcome, suggestedPrompt);
    }

    // SPEC-63 RF-001a: learnings da sessão → fila de sugestões
    // (fire-and-forget; nunca falha a análise).
    private async Task SuggestLearningsAsync(
        List<string> learnings, ConversationRecord conv,
        Guid conversationId, CancellationToken ct)
    {
        try
        {
            var knowledge = _services?.GetService(
                typeof(Application.Knowledge.IKnowledgeService))
                as Application.Knowledge.IKnowledgeService;
            if (knowledge is not null)
            {
                foreach (var learning in learnings)
                {
                    await knowledge.SuggestAsync(
                        $"learning: {conv.Title ?? conversationId.ToString()[..8]}",
                        learning, "org", conv.SelectedRepository,
                        Application.Knowledge.SuggestionKind.Entry,
                        "session-insight", [conversationId], null, ct);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "knowledge suggestion hook failed for {Id}", conversationId);
        }
    }

    public async Task<SessionInsightDto?> GetSessionInsightAsync(Guid conversationId, CancellationToken ct = default)
    {
        var record = await _db.SessionInsights
            .FirstOrDefaultAsync(s => s.ConversationId == conversationId, ct);

        return record == null ? null : ToDto(record);
    }

    public async Task<InsightsStatsDto> GetStatsAsync(Guid? orgId = null, CancellationToken ct = default)
    {
        var query = _db.SessionInsights.AsNoTracking();
        if (orgId.HasValue)
        {
            query = query.Where(s => s.OrgId == orgId.Value);
        }

        var all = await query.ToListAsync(ct);
        int total = all.Count;
        int success = all.Count(s => s.Outcome == "success");
        int partial = all.Count(s => s.Outcome == "partial");
        int failed = all.Count(s => s.Outcome == "failed");
        int unclear = all.Count(s => s.Outcome == "unclear");

        double rate = total > 0 ? (double)(success + partial) / total : 1.0;

        var topIssues = new Dictionary<string, int>();
        foreach (var issuesJson in all
            .Where(r => !string.IsNullOrEmpty(r.IssuesJson))
            .Select(r => r.IssuesJson!))
        {
            try
            {
                var issues = JsonSerializer.Deserialize<List<SessionIssue>>(issuesJson);
                if (issues != null)
                {
                    foreach (var kind in issues.Select(i => i.Kind))
                    {
                        topIssues[kind] = topIssues.GetValueOrDefault(kind) + 1;
                    }
                }
            }
            catch
            {
                // Ignora parse malformado em stats
            }
        }

        return new InsightsStatsDto(total, success, partial, failed, unclear, rate, topIssues);
    }

    private static SessionInsightDto ToDto(SessionInsightRecord r)
    {
        var issues = string.IsNullOrEmpty(r.IssuesJson) ? new List<SessionIssue>() : JsonSerializer.Deserialize<List<SessionIssue>>(r.IssuesJson) ?? new List<SessionIssue>();
        var deadEnds = string.IsNullOrEmpty(r.DeadEndsJson) ? new List<string>() : JsonSerializer.Deserialize<List<string>>(r.DeadEndsJson) ?? new List<string>();
        var phaseCosts = string.IsNullOrEmpty(r.PhaseCostsJson) ? new List<PhaseCost>() : JsonSerializer.Deserialize<List<PhaseCost>>(r.PhaseCostsJson) ?? new List<PhaseCost>();
        var learnings = string.IsNullOrEmpty(r.LearningsJson) ? new List<string>() : JsonSerializer.Deserialize<List<string>>(r.LearningsJson) ?? new List<string>();

        return new SessionInsightDto(
            r.Id,
            r.ConversationId,
            r.OrgId,
            r.Outcome,
            issues,
            deadEnds,
            phaseCosts,
            r.SuggestedPrompt,
            learnings,
            r.Model,
            r.CreatedAt);
    }
}