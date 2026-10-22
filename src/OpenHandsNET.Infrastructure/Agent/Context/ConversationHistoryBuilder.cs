using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Infrastructure.Conversations;

namespace OpenHandsNET.Infrastructure.Agent.Context;

/// <summary>
/// Rebuilds the LLM history from the event stream (SPEC-09 resume, SPEC-22
/// replay-consistent compaction): applies <c>Condensation.ForgottenEventIds</c>
/// and <c>PrunedEventIds</c>, re-inserts the latest summary after the system
/// prompt, and appends a <c>SystemPromptEvent</c> when the stream has none.
/// </summary>
public sealed class ConversationHistoryBuilder(IServiceProvider services)
{
    /// <summary>Result of a rebuild: messages + parallel event ids + tail.</summary>
    public sealed record RebuiltHistory(
        List<LlmMessage> Messages,
        List<string?> EventIds,
        string? LastEventId);

    /// <summary>Rebuilds <paramref name="conv"/>'s history (resume-safe).</summary>
    public async Task<RebuiltHistory> BuildAsync(
        Guid conversationId, ConversationRecord conv, IToolRegistry tools,
        CancellationToken ct)
    {
        var events = services.GetRequiredService<IEventService>();
        var all = new List<EventBase>();
        string? cursor = null;
        do
        {
            var page = await events.SearchAsync(conversationId,
                new EventQuery(PageId: cursor, Limit: 100), ct);
            all.AddRange(page.Items);
            cursor = page.NextPageId;
        }
        while (cursor is not null);

        var forgotten = all.OfType<Condensation>()
            .SelectMany(c => c.ForgottenEventIds).ToHashSet();
        var pruned = all.OfType<Condensation>()
            .SelectMany(c => c.PrunedEventIds).ToHashSet();
        var lastSummary = all.OfType<Condensation>()
            .LastOrDefault(c => c.Summary is not null);

        var history = new List<LlmMessage>();
        var ids = new List<string?>();
        string? lastId = null;
        var hasSystem = false;
        foreach (var e in all)
        {
            if (forgotten.Contains(e.Id))
            {
                continue;
            }

            lastId = e.Id;
            switch (e)
            {
                case SystemPromptEvent sp:
                    hasSystem = true;
                    Add(new LlmMessage("system", Flatten(sp.SystemPrompt)), e.Id);
                    break;
                case MessageEvent m:
                    Add(ToLlmMessage(m), e.Id);
                    break;
                case ActionEvent a:
                    Add(LlmMessage.AssistantToolCall(
                        Flatten(a.Thought),
                        [new LlmToolCall(a.ToolCallId, a.ToolName,
                            a.Action?.GetRawText() ?? "{}")]), e.Id);
                    break;
                case ObservationEvent o:
                    Add(LlmMessage.ToolResult(o.ToolCallId,
                        pruned.Contains(o.Id)
                            ? "[Output truncated by condenser]"
                            : o.Observation.GetRawText()), e.Id);
                    break;
                case UserRejectObservation r:
                    Add(LlmMessage.ToolResult(
                        r.ToolCallId,
                        $"User rejected: {r.RejectionReason}"), e.Id);
                    break;
            }
        }

        if (!hasSystem)
        {
            // SPEC-11 RF-002: resolve skills por trigger (always/keyword/task)
            // usando a última mensagem do usuário e as tags da conversa.
            var lastUserText = all.OfType<MessageEvent>()
                .Where(m => m.Source == EventSource.User)
                .Select(m => ToLlmMessage(m).Content)
                .LastOrDefault();
            var skills = services.GetService<Application.Skills.ISkillService>()
                is { } skillSvc
                ? await skillSvc.ResolveForConversationAsync(
                    conversationId, lastUserText, null,
                    conv.CreatedByUserId, conv.OrgId?.ToString(), ct)
                : [];

            // SPEC-25 RF-001/002: repo context + repo skills federadas.
            string? repoContext = null;
            if (services.GetService<Discovery.RepoContextLoader>()
                is { } repoLoader)
            {
                var loaded = await repoLoader.LoadAsync(conv, all, ct);
                repoContext = loaded.RenderedMarkdown;
                skills = MergeRepoSkills(
                    skills, loaded.RepoSkills, lastUserText);
            }

            var prompt = SystemPromptBuilder.Build(
                conv.SelectedRepository,
                tools.All.Select(t => t.Schema).ToList(),
                skills, repoContext);
            var sp = new SystemPromptEvent
            {
                ParentId = lastId,
                SystemPrompt = JsonSerializer.SerializeToElement(
                    new[] { new { type = "text", text = prompt } },
                    EventJson.Options),
                Tools = JsonSerializer.SerializeToElement(
                    tools.All.Select(t => new
                    {
                        name = t.Schema.Name,
                        description = t.Schema.Description,
                        input_schema = t.Schema.InputSchema,
                    }), EventJson.Options),
            };
            lastId = (await events.AppendAsync(conversationId, sp, ct)).Id;
            Insert(0, new LlmMessage("system", prompt), sp.Id);
        }

        if (lastSummary is { Summary: { } summary })
        {
            // O summary re-entra depois do system (ou no início) — posição
            // determinística: min(SummaryOffset, count).
            var at = Math.Min(
                lastSummary.SummaryOffset ?? (hasSystem ? 1 : 0),
                history.Count);
            Insert(Math.Max(hasSystem ? 1 : 0, at),
                new LlmMessage("system",
                    $"## Earlier context (compacted)\n{summary}"),
                lastSummary.Id);
        }

        return new RebuiltHistory(history, ids, lastId);

        // SPEC-25 RF-002: db ∪ repo, dedupe por nome (db ganha), cap 20.
        // Repo skills entram via trigger Keyword(nome) — lastUserText
        // contém o nome → injetada; o catálogo completo sempre aparece
        // na seção Repository context.
        static IReadOnlyList<Domain.Skills.Skill> MergeRepoSkills(
            IReadOnlyList<Domain.Skills.Skill> db,
            IReadOnlyList<Domain.Skills.Skill> repo, string? message)
        {
            if (repo.Count == 0)
            {
                return db;
            }

            var names = db.Select(s => s.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var merged = db.ToList();
            foreach (var s in repo)
            {
                if (names.Contains(s.Name))
                {
                    continue;
                }

                if (message?.Contains(s.Name,
                        StringComparison.OrdinalIgnoreCase) == true)
                {
                    merged.Add(s);
                    names.Add(s.Name);
                }

                if (merged.Count >= 20)
                {
                    break;
                }
            }

            return merged;
        }

        void Add(LlmMessage m, string? id)
        {
            history.Add(m);
            ids.Add(id);
        }

        void Insert(int at, LlmMessage m, string? id)
        {
            history.Insert(at, m);
            ids.Insert(at, id);
        }
    }

    /// <summary>MessageEvent → LlmMessage (também usado pelo runner p/ pendings).</summary>
    internal static LlmMessage ToLlmMessage(MessageEvent m)
    {
        try
        {
            var el = m.LlmMessage;
            var role = el.TryGetProperty("role", out var r)
                ? r.GetString() ?? "user" : "user";
            var content = ContentText(el);
            return new LlmMessage(role, content);
        }
        catch (JsonException)
        {
            return new LlmMessage("user", m.LlmMessage.GetRawText());
        }
    }

    /// <summary>Extrai o texto de content (string | array de parts | raw).</summary>
    private static string? ContentText(JsonElement el)
    {
        if (!el.TryGetProperty("content", out var c))
        {
            return null;
        }

        if (c.ValueKind == JsonValueKind.String)
        {
            return c.GetString();
        }

        if (c.ValueKind != JsonValueKind.Array)
        {
            return c.GetRawText();
        }

        return string.Concat(c.EnumerateArray().Select(b =>
            b.TryGetProperty("text", out var t)
                ? t.GetString() : null));
    }

    private static string? Flatten(JsonElement? el) => el switch
    {
        null => null,
        { ValueKind: JsonValueKind.String } s => s.GetString(),
        { ValueKind: JsonValueKind.Array } arr => string.Concat(
            arr.EnumerateArray().Select(b =>
                b.TryGetProperty("text", out var t) ? t.GetString() : null)),
        { } other => other.GetRawText(),
    };
}