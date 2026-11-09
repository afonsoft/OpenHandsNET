using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using OpenHandsNET.Application.Checkpoints;
using OpenHandsNET.Application.Conversations;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Conversations;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Infrastructure.Eventing;
using OpenHandsNET.Infrastructure.Persistence;

using DomainSandbox = OpenHandsNET.Domain.Sandbox.Sandbox;

namespace OpenHandsNET.Infrastructure.Conversations;

/// <summary>
/// SPEC-135: fork + turn undo + diff hunk revert.
///
/// Fork clona os eventos persistidos até o ponto de corte (ULIDs novos,
/// <c>parent_id</c> encadeado para o evento de corte) numa conversa-filha
/// que herda repo/agente/modelo/profile e boota um sandbox próprio via
/// start-task. O checkpoint do workdir da origem é registrado no evento
/// <c>forked_from</c> mas NÃO é copiado — o git-dir de checkpoints vive
/// dentro do container da origem (divergência documentada: a filha
/// materializa o repo do zero no clone padrão).
///
/// Undo marca os eventos do último turno do usuário com
/// <c>truncated_at_ticks</c> (soft-delete auditável — o stream "ativo"
/// filtra; o ledger físico permanece) e reverte o workdir ao checkpoint
/// mais recente ANTES do início do turno (SPEC-43 auto-checkpoint
/// pós-turno ⇒ último ckpt com ts ≤ user-msg ≈ início do turno).
///
/// Revert-hunks gera um patch por hunk do <c>git diff</c> atual do
/// arquivo e aplica <c>git apply -R</c> por hunk — falhas voltam como
/// conflicts sem abortar os demais.
/// </summary>
public sealed class ConversationUxService(
    AppDbContext db,
    IEventService events,
    ICheckpointService ckpts,
    ISandboxService sandboxes,
    ISandboxExecutor executor,
    IStartTaskService tasks) : IConversationUxService
{
    /// <inheritdoc />
    public async Task<(ForkResult?, ForkError?)> ForkAsync(
        Guid id, Guid? orgId, string? userId,
        ForkConversationRequest req, CancellationToken ct)
    {
        var src = await db.Conversations
            .FirstOrDefaultAsync(c => c.Id == id
                && c.ExecutionStatus
                    != nameof(ConversationExecutionStatus.Archived), ct);
        if (src is null)
        {
            return (null, ForkError.NotFound);
        }

        if (src.ExecutionStatus
            == nameof(ConversationExecutionStatus.Running))
        {
            return (null, ForkError.RunActive);
        }

        EventRecord? cut = null;
        if (req.AtEventId is { Length: > 0 } eid)
        {
            cut = await db.Events.FirstOrDefaultAsync(
                r => r.ConversationId == id && r.Id == eid
                    && r.TruncatedAtTicks == null, ct);
            if (cut is null)
            {
                return (null, ForkError.EventNotFound);
            }
        }

        var now = DateTimeOffset.UtcNow;
        var childId = Guid.NewGuid();
        var child = new ConversationRecord
        {
            Id = childId,
            OrgId = orgId ?? src.OrgId,
            CreatedByUserId = userId ?? src.CreatedByUserId,
            Title = req.Title ?? Truncate(src.Title, 80),
            SelectedRepository = src.SelectedRepository,
            AgentType = src.AgentType,
            LlmModel = src.LlmModel,
            SandboxProfile = src.SandboxProfile,
            ConfirmationMode = src.ConfirmationMode,
            PermissionsJson = src.PermissionsJson,
            Mode = src.Mode,
            SecretNamesJson = src.SecretNamesJson,
            McpConfigJson = src.McpConfigJson,
            ParentConversationId = src.Id,
            DelegationDepth = src.DelegationDepth,
            PendingKickoff = false,
            ExecutionStatus = ConversationExecutionStatus.Starting.ToString(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        var task = new StartTaskRecord
        {
            Id = Guid.NewGuid(),
            Status = StartTaskStatus.Working.ToString(),
            AppConversationId = childId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Conversations.Add(child);
        db.StartTasks.Add(task);
        await db.SaveChangesAsync(ct);

        // Eventos até o corte (ou todos), ordem (ticks,id) — mesma do
        // EventStore. Payload é re-serializado com id/parent_id novos.
        var query = db.Events
            .Where(r => r.ConversationId == id && r.TruncatedAtTicks == null);
        if (cut is not null)
        {
            query = query.Where(r => r.TimestampTicks < cut.TimestampTicks
                || (r.TimestampTicks == cut.TimestampTicks
                    && string.Compare(r.Id, cut.Id) <= 0));
        }

        var rows = await query
            .OrderBy(r => r.TimestampTicks).ThenBy(r => r.Id)
            .ToListAsync(ct);

        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var e = Deserialize(r.Payload);
            var newId = Ulid.NewUlid().ToString();
            // parent remapeado quando dentro do clone; senão o evento de
            // corte (link para a linha da origem) ou o parent original.
            var parent = r.ParentId is { } p && idMap.TryGetValue(p, out var np)
                ? np
                : cut?.Id ?? r.ParentId;
            var cloned = e with { Id = newId, ParentId = parent };
            idMap[r.Id] = newId;
            await events.AppendAsync(childId, cloned, ct);
        }

        // Marca a proveniência no stream da filha — o badge do client
        // lê parent_conversation_id; este evento registra o corte e o
        // checkpoint mais recente da origem (referência para o usuário).
        string? ckptHash = null;
        if (src.SandboxId is { } sid
            && await sandboxes.GetAsync(sid, ct) is { } sb
            && sb.Status == Domain.Sandbox.SandboxStatus.Running
            && await ckpts.IsAvailableAsync(sb, ct))
        {
            var latest = await ckpts.ListAsync(sb, ct);
            ckptHash = latest.Count > 0 ? latest[0].Hash : null;
        }

        await events.AppendAsync(childId, new ConversationStateUpdateEvent
        {
            Key = "forked_from",
            Value = JsonSerializer.SerializeToElement(new
            {
                conversation_id = src.Id,
                at_event_id = cut?.Id,
                events_cloned = rows.Count,
                source_checkpoint = ckptHash,
            }, EventJson.Options),
        }, ct);

        await tasks.EnqueueAsync(task.Id, ct);
        return (new ForkResult(childId, task.Id, src.Id), null);
    }

    /// <inheritdoc />
    public async Task<(UndoResult?, UndoError?)> UndoAsync(
        Guid id, CancellationToken ct)
    {
        var conv = await db.Conversations
            .FirstOrDefaultAsync(c => c.Id == id
                && c.ExecutionStatus
                    != nameof(ConversationExecutionStatus.Archived), ct);
        if (conv is null)
        {
            return (null, UndoError.NotFound);
        }

        if (conv.ExecutionStatus
            == nameof(ConversationExecutionStatus.Running))
        {
            return (null, UndoError.RunActive);
        }

        // Último turno do usuário = última MessageEvent source=user ainda
        // ativa. Turnos já desfeitos são invisíveis (TruncatedAtTicks).
        var msg = await db.Events
            .Where(r => r.ConversationId == id && r.TruncatedAtTicks == null
                && r.Kind == "MessageEvent" && r.Source == "user")
            .OrderByDescending(r => r.TimestampTicks).ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);
        if (msg is null)
        {
            return (null, UndoError.NoTurn);
        }

        // Checkpoint do início do turno: mais novo snapshot com ts ≤ a
        // mensagem do usuário (auto-checkpoints são pós-turno).
        var msgUnix = msg.TimestampTicks / TimeSpan.TicksPerSecond;
        DomainSandbox? sb = conv.SandboxId is { } sid
            ? await sandboxes.GetAsync(sid, ct) : null;
        CheckpointItem? pre = null;
        IReadOnlyList<string> conflicts = [];
        if (sb?.Status == Domain.Sandbox.SandboxStatus.Running
            && await ckpts.IsAvailableAsync(sb, ct))
        {
            var items = await ckpts.ListAsync(sb, ct);
            pre = items.Where(c => c.Timestamp <= msgUnix)
                .OrderByDescending(c => c.Timestamp)
                .FirstOrDefault();
        }

        // Soft-delete do turno: eventos ≥ (ticks,id) da mensagem.
        var cutTicks = msg.TimestampTicks;
        var cutId = msg.Id;
        var truncatedAt = DateTimeOffset.UtcNow.UtcTicks;
        var n = await db.Events
            .Where(r => r.ConversationId == id && r.TruncatedAtTicks == null
                && (r.TimestampTicks > cutTicks
                    || (r.TimestampTicks == cutTicks
                        && string.Compare(r.Id, cutId) >= 0)))
            .ExecuteUpdateAsync(s => s.SetProperty(
                r => r.TruncatedAtTicks, truncatedAt), ct);

        if (pre is not null)
        {
            // Drift aqui = exatamente o que o turno mudou — reportado no
            // files_changed, revertido incondicionalmente (guardrail §9
            // só exige o guard de run-ativa, já aplicado acima).
            (conflicts, _) = await ckpts.DriftAsync(sb!, pre.Hash, ct);
            await ckpts.RevertAsync(id, sb!, pre.Hash, ct);
            await events.AppendAsync(id, new FilesChangedEvent
            {
                SourceTool = "turn_undo",
                Paths = conflicts.Select(p => new FileChange(p, "modify"))
                    .ToList(),
            }, CancellationToken.None);
        }

        await events.AppendAsync(id, new ConversationStateUpdateEvent
        {
            Key = "turn_undo",
            Value = JsonSerializer.SerializeToElement(new
            {
                truncated_events = n,
                turn_event_id = msg.Id,
                checkpoint = pre?.Hash,
            }, EventJson.Options),
        }, CancellationToken.None);

        return (new UndoResult(n, pre?.Hash, msg.Id, conflicts), null);
    }

    /// <inheritdoc />
    public async Task<(RevertHunksResult?, RevertHunksError?)>
        RevertHunksAsync(Guid sandboxId, string path,
            IReadOnlyList<int> hunkIndexes, CancellationToken ct)
    {
        var sb = await sandboxes.GetAsync(sandboxId, ct);
        if (sb is null)
        {
            return (null, RevertHunksError.NotFound);
        }

        if (sb.Status != Domain.Sandbox.SandboxStatus.Running)
        {
            return (null, RevertHunksError.SandboxNotRunning);
        }

        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/')
            || path.Contains("..", StringComparison.Ordinal))
        {
            return (null, RevertHunksError.InvalidPath);
        }

        const string wt = Checkpoints.CheckpointService.WorkTree;
        var q = $"'{path.Replace("'", "'\\''")}'";

        // Guardrail §9: só tracked do workdir — untracked/fora do repo
        // nunca é tocado.
        var tracked = await executor.ExecAsync(sb,
            $"cd '{wt}' && git ls-files --error-unmatch -- {q}",
            TimeSpan.FromSeconds(15), ct);
        if (tracked.ExitCode != 0)
        {
            return (null, RevertHunksError.NotTracked);
        }

        var diff = await executor.ExecAsync(sb,
            $"cd '{wt}' && git diff -- {q}",
            TimeSpan.FromSeconds(30), ct);
        if (diff.ExitCode != 0)
        {
            return (null, RevertHunksError.ExecFailed);
        }

        var (header, hunks) = HunkPatch.Split(diff.StdOut);
        var applied = new List<int>();
        var conflicts = new List<int>();
        foreach (var idx in hunkIndexes.Distinct().OrderBy(i => i))
        {
            if (idx < 0 || idx >= hunks.Count)
            {
                conflicts.Add(idx);
                continue;
            }

            var patch = HunkPatch.Build(header, hunks[idx]);
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(patch));
            var r = await executor.ExecAsync(sb,
                $"cd '{wt}' && printf '%s' '{b64}' | base64 -d | " +
                "git apply -R --whitespace=nowarn -",
                TimeSpan.FromSeconds(20), ct);
            (r.ExitCode == 0 ? applied : conflicts).Add(idx);
        }

        if (applied.Count > 0 && sb.ConversationId is { } convId)
        {
            await events.AppendAsync(convId, new FilesChangedEvent
            {
                SourceTool = "hunk_revert",
                Paths = [new FileChange(path, "modify")],
            }, CancellationToken.None);
        }

        return (new RevertHunksResult(applied, conflicts), null);
    }

    private static EventBase Deserialize(string payload) =>
        JsonSerializer.Deserialize<EventBase>(payload, EventJson.Options)
            ?? throw new InvalidOperationException("undecodable event");

    private static string? Truncate(string? s, int max) =>
        s is null || s.Length <= max ? s : s[..max];
}

/// <summary>
/// Utilitário puro: quebra um unified diff single-file em header
/// (linhas <c>diff --git … +++ b/…</c>) e hunks (<c>@@ … @@</c> +
/// corpo), e remonta um patch aplicável com um único hunk.
/// </summary>
public static class HunkPatch
{
    /// <summary>Split: header + lista de hunks (cada hunk = texto completo).</summary>
    public static (string Header, IReadOnlyList<string> Hunks) Split(
        string diff)
    {
        var header = new StringBuilder();
        var hunks = new List<string>();
        StringBuilder? cur = null;
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                cur = new StringBuilder().AppendLine(line);
                hunks.Add(cur.ToString());
                // corpo acumulado em dicionário separado (ver abaixo)
                continue;
            }

            if (cur is null)
            {
                header.AppendLine(line);
            }
            else
            {
                hunks[^1] += line + '\n';
            }
        }

        return (header.ToString(), hunks);
    }

    /// <summary>Patch aplicável: header do arquivo + um hunk.</summary>
    public static string Build(string header, string hunk) =>
        header + hunk;
}