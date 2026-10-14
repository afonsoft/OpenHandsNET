using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenHandsNET.Application.Conversations;
using OpenHandsNET.Domain.Conversations;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Infrastructure.Persistence;
using OpenHandsNET.Application.Sandbox;

namespace OpenHandsNET.Infrastructure.Conversations;

/// <summary>
/// EF-backed <see cref="IAppConversationService"/>; cursor pagination identical
/// to the event store (created_at ticks + id tie-break, base64).
/// </summary>
public sealed class AppConversationService(
    AppDbContext db,
    IStartTaskService tasks,
    ISandboxService sandboxes,
    OpenHandsNET.Application.Events.IEventService events,
    Sandbox.WorkspaceProvisioner provisioner,
    OpenHandsNET.Application.Agent.IAgentRunnerHost runnerHost,
    OpenHandsNET.Application.Agent.IBrowserService? browser = null)
    : IAppConversationService
{
    /// <inheritdoc />
    public async Task<AppConversationStartTask> CreateAsync(
        CreateConversationRequest request, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var conv = new ConversationRecord
        {
            Id = Guid.NewGuid(),
            Title = Truncate(request.InitialMessage is null
                ? null : Flatten(request.InitialMessage), 80),
            SelectedRepository = request.SelectedRepository,
            AgentType = request.AgentType,
            LlmModel = request.LlmModel,
            SandboxProfile = Domain.Sandbox.SandboxProfiles
                .IsValid(request.SandboxProfile)
                    ? request.SandboxProfile : null,
            ConfirmationMode = request.ConfirmationMode,
            // Sinaliza ao worker que o agente deve iniciar ao chegar em READY.
            PendingKickoff = request.InitialMessage?.Run == true,
            ExecutionStatus = ConversationExecutionStatus.Starting.ToString(),
            CreatedAt = now, UpdatedAt = now,
        };
        var task = new StartTaskRecord
        {
            Id = Guid.NewGuid(), Status = StartTaskStatus.Working.ToString(),
            AppConversationId = conv.Id, CreatedAt = now, UpdatedAt = now,
        };
        db.Conversations.Add(conv);
        db.StartTasks.Add(task);
        await db.SaveChangesAsync(ct);

        // A mensagem inicial entra no stream imediatamente (upstream parity).
        if (request.InitialMessage is { } initial)
        {
            await events.AppendAsync(conv.Id, new MessageEvent
            {
                Source = EventSource.User,
                LlmMessage = System.Text.Json.JsonSerializer.SerializeToElement(new
                {
                    role = initial.Role,
                    content = initial.Content,
                }, EventJson.Options),
            }, ct);
        }

        await tasks.EnqueueAsync(task.Id, ct);
        return task.ToDomain();
    }

    /// <inheritdoc />
    public async Task<ConversationPage> SearchAsync(
        ConversationQuery query, CancellationToken ct)
    {
        var q = db.Conversations
            .Where(r => r.ExecutionStatus != nameof(ConversationExecutionStatus.Archived));
        if (query.TitleEq is not null)
        {
            q = q.Where(r => r.Title == query.TitleEq);
        }

        if (query.CreatedAtGte is not null)
        {
            var gte = query.CreatedAtGte.Value.UtcTicks;
            q = q.Where(r => r.CreatedAtTicks >= gte);
        }

        if (CursorPaginator.TryDecode(query.PageId, out var ticks, out var id))
        {
            q = q.Where(r => r.CreatedAtTicks < ticks
                || (r.CreatedAtTicks == ticks && string.Compare(r.Id.ToString(), id) < 0));
        }

        var limit = Math.Clamp(query.Limit, 1, 100);
        var rows = await q
            .OrderByDescending(r => r.CreatedAtTicks).ThenByDescending(r => r.Id)
            .Take(limit + 1)
            .ToListAsync(ct);
        var hasMore = rows.Count > limit;
        var page = hasMore ? rows.Take(limit).ToList() : rows;
        var last = page.LastOrDefault();
        return new ConversationPage(
            page.Select(r => r.ToDomain()).ToList(),
            hasMore && last is not null
                ? CursorPaginator.Encode(last.CreatedAt.UtcTicks, last.Id.ToString())
                : null);
    }

    /// <inheritdoc />
    public Task<long> CountAsync(ConversationQuery query, CancellationToken ct)
    {
        var q = db.Conversations
            .Where(r => r.ExecutionStatus != nameof(ConversationExecutionStatus.Archived));
        if (query.TitleEq is not null)
        {
            q = q.Where(r => r.Title == query.TitleEq);
        }

        if (query.CreatedAtGte is not null)
        {
            var gte = query.CreatedAtGte.Value.UtcTicks;
            q = q.Where(r => r.CreatedAtTicks >= gte);
        }

        return q.LongCountAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AppConversation>> GetBatchAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Take(100).ToHashSet();
        var rows = await db.Conversations.Where(r => wanted.Contains(r.Id)).ToListAsync(ct);
        var byId = rows.ToDictionary(r => r.Id);
        return ids.Where(byId.ContainsKey).Select(id => byId[id].ToDomain()).ToList();
    }

    /// <inheritdoc />
    public async Task<AppConversation?> GetAsync(Guid id, CancellationToken ct) =>
        (await db.Conversations.FindAsync([id], ct))?.ToDomain();

    /// <inheritdoc />
    public async Task<AppConversation?> UpdateAsync(
        Guid id, string? title, IReadOnlyList<string>? tags, bool? pinned,
        CancellationToken ct)
    {
        var record = await db.Conversations.FindAsync([id], ct);
        if (record is null)
        {
            return null;
        }

        if (title is not null)
        {
            record.Title = title;
        }

        if (tags is not null)
        {
            record.TagsJson = System.Text.Json.JsonSerializer.Serialize(tags);
        }

        if (pinned is not null)
        {
            record.Pinned = pinned.Value;
        }

        record.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return record.ToDomain();
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var record = await db.Conversations.FindAsync([id], ct);
        if (record is null)
        {
            return false;
        }

        record.ExecutionStatus = ConversationExecutionStatus.Archived.ToString();
        record.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        if (record.SandboxId is not null)
        {
            await sandboxes.DeleteAsync(record.SandboxId.Value, ct);
        }

        // SPEC-16 RF-006: workspace só sai do host junto com a conversa.
        provisioner.RemoveWorkspace(record.Id);
        // SPEC-18 RF-003: fecha o contexto de browser da conversa.
        if (browser is not null)
        {
            await browser.CloseAsync(record.Id, ct);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<SwitchResult> SwitchProfileAsync(
        Guid id, string? profile, CancellationToken ct)
    {
        var record = await db.Conversations.FindAsync([id], ct);
        if (record is null || record.ExecutionStatus
                == nameof(ConversationExecutionStatus.Archived))
        {
            return SwitchResult.Fail(SwitchError.NotFound);
        }

        var normalized = string.IsNullOrWhiteSpace(profile) ? null : profile;
        if (normalized is not null
            && !Domain.Sandbox.SandboxProfiles.IsValid(normalized))
        {
            return SwitchResult.Fail(SwitchError.InvalidProfile);
        }

        // Run ativo ou pipeline de start em curso — trocar agora corromperia
        // o estado (ExecutionStatus.Running também cobre idle pós-READY, por
        // isso o sinal exato é o runner host + transição Starting).
        if (runnerHost.IsRunning(id)
            || record.ExecutionStatus == nameof(ConversationExecutionStatus.Starting))
        {
            return SwitchResult.Fail(SwitchError.RunActive);
        }

        record.SandboxProfile = normalized;
        record.UpdatedAt = DateTimeOffset.UtcNow;

        if (record.SandboxId is { } sandboxId)
        {
            // Sandbox vive com profile velho — remove e re-enfileira start
            // (workspace no host sobrevive; clone é idempotente).
            try
            {
                await sandboxes.DeleteAsync(sandboxId, ct);
            }
            catch (Exception)
            {
                return SwitchResult.Fail(SwitchError.SandboxError);
            }

            record.SandboxId = null;
            record.ExecutionStatus =
                ConversationExecutionStatus.Starting.ToString();
            var task = new StartTaskRecord
            {
                Id = Guid.NewGuid(),
                Status = StartTaskStatus.Working.ToString(),
                AppConversationId = record.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.StartTasks.Add(task);
            await db.SaveChangesAsync(ct);
            await tasks.EnqueueAsync(task.Id, ct);
        }
        else
        {
            await db.SaveChangesAsync(ct);
        }

        await events.AppendAsync(record.Id, new ConversationStateUpdateEvent
        {
            Key = "sandbox_profile",
            Value = System.Text.Json.JsonSerializer.SerializeToElement(
                normalized),
        }, ct);
        return SwitchResult.Ok(record.ToDomain());
    }

    /// <inheritdoc />
    public async Task<SwitchResult> SwitchModelAsync(
        Guid id, string model, CancellationToken ct)
    {
        var record = await db.Conversations.FindAsync([id], ct);
        if (record is null || record.ExecutionStatus
                == nameof(ConversationExecutionStatus.Archived))
        {
            return SwitchResult.Fail(SwitchError.NotFound);
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return SwitchResult.Fail(SwitchError.InvalidModel);
        }

        if (runnerHost.IsRunning(id))
        {
            return SwitchResult.Fail(SwitchError.RunActive);
        }

        record.LlmModel = model.Trim();
        record.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await events.AppendAsync(record.Id, new ConversationStateUpdateEvent
        {
            Key = "llm_model",
            Value = System.Text.Json.JsonSerializer.SerializeToElement(
                record.LlmModel),
        }, ct);
        return SwitchResult.Ok(record.ToDomain());
    }

    /// <inheritdoc />
    public async Task<SendMessageError?> ValidateSendMessageAsync(
        Guid id, CancellationToken ct)
    {
        var record = await db.Conversations.FindAsync([id], ct);
        if (record is null)
        {
            return SendMessageError.NotFound;
        }

        if (record.ExecutionStatus == nameof(ConversationExecutionStatus.Archived))
        {
            return SendMessageError.Gone;
        }

        if (record.SandboxId is null)
        {
            return SendMessageError.ConflictSandboxNotRunning;
        }

        var sandbox = await sandboxes.GetAsync(record.SandboxId.Value, ct);

        // SPEC-16 RF-002: mensagem para sandbox Paused/Stopped acorda o
        // runtime no caminho (docker unpause/start).
        if (sandbox?.Status is Domain.Sandbox.SandboxStatus.Paused
            or Domain.Sandbox.SandboxStatus.Stopped)
        {
            sandbox = await sandboxes.ResumeAsync(record.SandboxId.Value, ct);
        }

        if (sandbox?.Status == Domain.Sandbox.SandboxStatus.Running)
        {
            // SPEC-16 RF-004: atividade renova o lease do timeout watcher.
            var s = await db.Sandboxes.FindAsync([record.SandboxId.Value], ct);
            var spec = s is null ? null
                : await db.SandboxSpecs.FindAsync([s.SpecId], ct);
            if (s is not null)
            {
                s.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(
                    spec?.TimeoutMinutes ?? 60);
                await db.SaveChangesAsync(ct);
            }

            return null;
        }

        return sandbox?.Status switch
        {
            Domain.Sandbox.SandboxStatus.Error or Domain.Sandbox.SandboxStatus.Missing
                => SendMessageError.Gone,
            _ => SendMessageError.ConflictSandboxNotRunning,
        };
    }

    /// <summary>Cursor codec delegado ao <see cref="CursorPaginator"/> compartilhado.</summary>
    internal static bool TryDecodeCursor(string? cursor, out long ticks, out string id) =>
        CursorPaginator.TryDecode(cursor, out ticks, out id);

    private static string Flatten(InitialMessage msg) =>
        string.Join(' ', msg.Content.Select(c =>
            c is System.Text.Json.JsonElement el && el.ValueKind ==
                System.Text.Json.JsonValueKind.Object &&
                el.TryGetProperty("text", out var t) ? t.GetString() : c?.ToString())
            .Where(s => !string.IsNullOrEmpty(s))!);

    private static string? Truncate(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max];
}
