using System.IO.Compression;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using OpenHandsNET.Application.Sharing;
using OpenHandsNET.Infrastructure.Agent;
using OpenHandsNET.Infrastructure.Conversations;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Sharing;

/// <summary>
/// SPEC-56 RF-004 — trajectory zip streamed to the caller's output
/// stream (response body): <c>events.jsonl</c> (all, redacted),
/// <c>metadata.json</c>, <c>workspace.diff</c> (best-effort) and a
/// generated <c>summary.md</c>. Never writes to disk.
/// </summary>
public sealed class TrajectoryExporter(
    AppDbContext db,
    OpenHandsNET.Application.Contracts.ISecretStore secrets,
    OpenHandsNET.Application.Sandbox.ISandboxService sandboxes,
    OpenHandsNET.Application.Git.IGitService git,
    ILogger<TrajectoryExporter> logger) : ITrajectoryExporter
{
    /// <inheritdoc />
    public async Task WriteZipAsync(
        Guid conversationId, Guid actorUserId, Stream output,
        CancellationToken ct)
    {
        var conv = await db.Conversations
            .Where(c => c.Id == conversationId)
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(ct)
            ?? throw new ShareForbiddenException("conversation not found");
        if (!await CanReadAsync(conv, actorUserId, ct))
        {
            throw new ShareForbiddenException("not allowed");
        }

        var secretValues = await ResolveSecretValuesAsync(conv, ct);
        var records = await db.Events
            .Where(e => e.ConversationId == conversationId)
            .OrderBy(e => e.TimestampTicks).ThenBy(e => e.Id)
            .ToListAsync(ct);

        using var zip = new ZipArchive(output, ZipArchiveMode.Create,
            leaveOpen: true);

        await WriteEventsJsonlAsync(zip, records, secretValues, ct);
        await WriteEntryAsync(zip, "metadata.json",
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                conversation_id = conv.Id,
                title = conv.Title,
                selected_repository = conv.SelectedRepository,
                agent_type = conv.AgentType,
                llm_model = conv.LlmModel,
                execution_status = conv.ExecutionStatus,
                mode = conv.Mode,
                org_id = conv.OrgId,
                created_by = conv.CreatedByUserId,
                created_at = conv.CreatedAt,
                updated_at = conv.UpdatedAt,
                event_count = records.Count,
                exported_at = DateTimeOffset.UtcNow,
                format = "openhandsnet-trajectory/v1",
            }, JsonOptions), ct);
        await WriteEntryAsync(zip, "workspace.diff",
            Encoding.UTF8.GetBytes(
                await TryWorkspaceDiffAsync(conv, ct)), ct);
        await WriteEntryAsync(zip, "summary.md",
            Encoding.UTF8.GetBytes(BuildSummary(conv, records)), ct);
    }

    // ------------------------------------------------------------------

    private static async Task WriteEventsJsonlAsync(
        ZipArchive zip, List<Eventing.EventRecord> records,
        IReadOnlyList<string> secretValues, CancellationToken ct)
    {
        var entry = zip.CreateEntry("events.jsonl");
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        foreach (var e in records)
        {
            ct.ThrowIfCancellationRequested();
            // Defesa em profundidade: payload re-redigido no export mesmo
            // já passando pelo redactor no append (SPEC-56 guardrails).
            await writer.WriteLineAsync(
                SecretRedactor.Redact(e.Payload, secretValues));
        }
    }

    private async Task<string> TryWorkspaceDiffAsync(
        ConversationRecord conv, CancellationToken ct)
    {
        if (conv.SandboxId is not { } sandboxId)
        {
            return "# no sandbox attached to this conversation\n";
        }

        try
        {
            var sandbox = await sandboxes.GetAsync(sandboxId, ct);
            if (sandbox is null)
            {
                return "# sandbox no longer available\n";
            }

            return await git.GetRawDiffAsync(sandbox, null, ct)
                ?? "# empty diff\n";
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex,
                "workspace diff unavailable for {ConversationId}", conv.Id);
            return "# workspace diff unavailable (sandbox not running)\n";
        }
    }

    private static string BuildSummary(
        ConversationRecord conv, List<Eventing.EventRecord> records)
    {
        var byKind = records
            .GroupBy(e => e.Kind)
            .ToDictionary(g => g.Key, g => g.Count());
        var sb = new StringBuilder();
        sb.AppendLine($"# {conv.Title ?? "Untitled conversation"}");
        sb.AppendLine();
        sb.AppendLine($"- Conversation: `{conv.Id}`");
        sb.AppendLine($"- Repository: {conv.SelectedRepository ?? "—"}");
        sb.AppendLine($"- Agent: {conv.AgentType ?? "default"}"
            + $" · Model: {conv.LlmModel ?? "default"}");
        sb.AppendLine($"- Status: {conv.ExecutionStatus}");
        sb.AppendLine($"- Created: {conv.CreatedAt:u}");
        sb.AppendLine($"- Events: {records.Count}");
        sb.AppendLine();
        sb.AppendLine("## Event kinds");
        sb.AppendLine();
        foreach (var (kind, count) in byKind.OrderByDescending(
                     kv => kv.Value))
        {
            sb.AppendLine($"- `{kind}` × {count}");
        }

        sb.AppendLine();
        sb.AppendLine("_Exported by OpenHandsNET (SPEC-56)._");
        return sb.ToString();
    }

    private static async Task WriteEntryAsync(
        ZipArchive zip, string name, byte[] content, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name);
        await using var stream = entry.Open();
        await stream.WriteAsync(content, ct);
    }

    private async Task<bool> CanReadAsync(
        ConversationRecord conv, Guid actorUserId, CancellationToken ct)
    {
        if (conv.CreatedByUserId == actorUserId.ToString())
        {
            return true;
        }

        return conv.OrgId is { } orgId
            && await db.OrgMembers.AnyAsync(
                m => m.OrgId == orgId && m.UserId == actorUserId, ct);
    }

    private async Task<IReadOnlyList<string>> ResolveSecretValuesAsync(
        ConversationRecord conv, CancellationToken ct)
    {
        if (conv.SecretNamesJson is null)
        {
            return [];
        }

        var names = JsonSerializer
            .Deserialize<List<string>>(conv.SecretNamesJson) ?? [];
        var values = new List<string>();
        foreach (var name in names)
        {
            try
            {
                if (conv.CreatedByUserId is { } uid
                    && await secrets.ResolveAsync(
                        "user", uid, name, ct) is { } uv)
                {
                    values.Add(uv);
                }

                if (conv.OrgId is { } orgId
                    && await secrets.ResolveAsync(
                        "org", orgId.ToString(), name, ct) is { } ov)
                {
                    values.Add(ov);
                }
            }
            catch
            {
                // Best-effort — placeholders {{secret:*}} ainda são
                // redigidos independente da resolução.
            }
        }

        return values;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}