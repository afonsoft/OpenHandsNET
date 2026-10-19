using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.Conversations;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Application.Indexing;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Indexing;

/// <summary>
/// SPEC-60 RF-002: pipeline Ask — code_search sobre o índice → LLM com
/// obrigação de citações <c>path:Lx-Ly</c> → mensagens persistidas.
/// Read-only por construção (não executa tools).
/// </summary>
public sealed partial class AskService(
    AppDbContext db, ICodeSearchStore search, IRepoIndexService indexes,
    IAgentLlmProvider llm, IAppConversationService conversations,
    ICurrentUserAccessor current) : IAskService
{
    private const int TopUnits = 8;

    public async Task<AskSessionInfo> CreateAsync(
        string repository, string question, CancellationToken ct)
    {
        var index = await indexes.GetByRepoAsync(repository, ct)
            ?? throw new IndexRequiredException(repository);
        if (index.Status is not RepoIndexStatus.Ready
            and not RepoIndexStatus.Stale)
        {
            throw new IndexNotReadyException(repository, index.Status);
        }

        var rec = new AskSessionRecord
        {
            Id = Guid.NewGuid(), OrgId = db.CurrentOrgId,
            RepoIndexId = index.Id, Repository = repository,
            Title = question.Length > 80 ? question[..80] : question,
            CreatedBy = current.User?.UserId.ToString(),
        };
        db.AskSessions.Add(rec);
        await db.SaveChangesAsync(ct);
        await RunTurnAsync(rec, index.Id, question, ct);
        return ToInfo(rec);
    }

    public async Task<AskSessionInfo> AskAsync(
        Guid sessionId, string question, CancellationToken ct)
    {
        var rec = await db.AskSessions.FirstOrDefaultAsync(
            s => s.Id == sessionId, ct)
            ?? throw new KeyNotFoundException("ask session not found");
        await RunTurnAsync(rec, rec.RepoIndexId!.Value, question, ct);
        return ToInfo(rec);
    }

    public async Task<AskSessionInfo?> GetAsync(
        Guid sessionId, CancellationToken ct)
    {
        var rec = await db.AskSessions.FirstOrDefaultAsync(
            s => s.Id == sessionId, ct);
        return rec is null ? null : ToInfo(rec);
    }

    public async Task<IReadOnlyList<AskSessionInfo>> ListAsync(
        CancellationToken ct) =>
        (await db.AskSessions.OrderByDescending(s => s.CreatedAt)
            .Take(50).ToListAsync(ct))
            .Select(ToInfo).ToList();

    public async Task<Guid> PromoteAsync(
        Guid sessionId, CancellationToken ct)
    {
        var rec = await db.AskSessions.FirstOrDefaultAsync(
            s => s.Id == sessionId, ct)
            ?? throw new KeyNotFoundException("ask session not found");
        var messages = DeserializeMessages(rec.MessagesJson);
        var plan = BuildPlanDocument(rec.Repository, messages);
        var task = await conversations.CreateForUserAsync(
            Guid.Parse(rec.CreatedBy ?? Guid.Empty.ToString()),
            rec.OrgId,
            new CreateConversationRequest(
                new InitialMessage("user", [plan], Run: false),
                SelectedRepository: rec.Repository,
                AgentType: "plan", LlmModel: null, SandboxSpecId: null),
            ct);
        rec.PromotedConversationId = task.AppConversationId;
        await db.SaveChangesAsync(ct);
        return rec.PromotedConversationId!.Value;
    }

    // ---- pipeline -------------------------------------------------------

    private async Task RunTurnAsync(
        AskSessionRecord rec, Guid repoIndexId, string question,
        CancellationToken ct)
    {
        var hits = await search.SearchAsync(
            repoIndexId, question, TopUnits, ct);
        var context = hits.Count == 0
            ? "(no code units matched)"
            : string.Join("\n\n", hits.Select((h, i) =>
                $"[{i + 1}] {h.Unit.Path}:{h.Unit.StartLine}-{h.Unit.EndLine}\n"
                + h.Unit.Text));

        var prompt = $"""
            You are answering a question about the repository
            {rec.Repository} in read-only Ask mode.
            Rules:
            - Answer ONLY from the code excerpts below.
            - Cite every claim as path:line-start-line-end, e.g.
              src/Foo.cs:10-42. Only cite excerpts that appear below.
            - If the excerpts don't answer the question, say what is
              missing instead of guessing.

            QUESTION: {question}

            CODE EXCERPTS:
            {context}
            """;

        var sb = new System.Text.StringBuilder();
        await foreach (var chunk in llm.ChatStreamAsync(
            [LlmMessage.User(prompt)], [],
            new LlmOptions(Temperature: 0.1), ct))
        {
            if (chunk is TextDelta d)
                sb.Append(d.Text);
        }

        var text = sb.ToString();
        var citations = CitationRegex().Matches(text)
            .Select(m => new AskCitation(
                m.Groups[1].Value,
                int.Parse(m.Groups[2].Value),
                int.Parse(m.Groups[3].Value)))
            .Where(c => hits.Any(h =>
                h.Unit.Path.EndsWith(c.Path.Split('/')[^1],
                    StringComparison.OrdinalIgnoreCase)
                || c.Path.EndsWith(h.Unit.Path,
                    StringComparison.OrdinalIgnoreCase)
                || h.Unit.Path.Equals(c.Path,
                    StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(c => (c.Path, c.StartLine))
            .ToList();

        var messages = DeserializeMessages(rec.MessagesJson);
        messages.Add(new AskMessage("user", question, [],
            DateTimeOffset.UtcNow));
        messages.Add(new AskMessage("assistant", text, citations,
            DateTimeOffset.UtcNow));
        rec.MessagesJson = JsonSerializer.Serialize(messages);
        await db.SaveChangesAsync(ct);
    }

    private static string BuildPlanDocument(
        string repository, List<AskMessage> messages)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("# Plan — promoted from Ask session\n\n");
        sb.Append($"Repository: `{repository}`\n\n");
        sb.Append("## Ask transcript\n\n");
        foreach (var m in messages)
        {
            sb.Append($"**{m.Role}**: {m.Text}\n\n");
            if (m.Citations.Count > 0)
            {
                sb.Append("Citations: ");
                sb.AppendJoin(", ", m.Citations.Select(
                    c => $"`{c.Path}:{c.StartLine}-{c.EndLine}`"));
                sb.Append("\n\n");
            }
        }
        sb.Append(
            "## Instructions\n\nImplement the changes discussed above.");
        return sb.ToString();
    }

    private static List<AskMessage> DeserializeMessages(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<AskMessage>>(json)
                ?? [];
        }
        catch { return []; }
    }

    private static AskSessionInfo ToInfo(AskSessionRecord r) => new(
        r.Id, r.OrgId, r.Repository, r.Title,
        DeserializeMessages(r.MessagesJson),
        r.PromotedConversationId, r.CreatedAt);

    [GeneratedRegex(
        @"([\w./\\-]+\.(?:cs|fs|vb|py|js|ts|jsx|tsx|go|rs|java|kt|rb|cpp|cc|h|hpp|c|sql|sh|md|razor|cshtml|yaml|yml|json|toml|xml|proto|tf)):(\d+)(?:-(\d+))?")]
    private static partial Regex CitationRegex();
}

public sealed class IndexRequiredException(string repo)
    : Exception($"repository not indexed: {repo}")
{
    public string Repository { get; } = repo;
}

public sealed class IndexNotReadyException(
    string repo, RepoIndexStatus status)
    : Exception($"index not ready: {repo} ({status})")
{
    public string Repository { get; } = repo;
    public RepoIndexStatus Status { get; } = status;
}
