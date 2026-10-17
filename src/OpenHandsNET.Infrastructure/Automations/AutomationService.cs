using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenHandsNET.Application.Automations;
using OpenHandsNET.Application.Conversations;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Domain.Conversations;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Automations;

/// <summary>
/// SPEC-47: automations — uma execução = start-task de conversa
/// (<see cref="IAppConversationService.CreateForUserAsync"/>) com as
/// credenciais do owner; runs <c>running</c> reconciliam contra o
/// ExecutionStatus da conversa linkada.
/// </summary>
public sealed class AutomationService(
    AppDbContext db,
    IAppConversationService conversations,
    IEventService events,
    IAuditService? audit = null,
    ILogger<AutomationService>? logger = null) : IAutomationService
{
    /// <summary>Tamanho máximo do prompt (SPEC-47 RF-003).</summary>
    public const int MaxPromptChars = 8192;

    /// <summary>Intervalo mínimo aceito (minutos).</summary>
    public const int MinIntervalMinutes = 5;

    private static AutomationInfo ToInfo(AutomationRecord r) => new(
        r.Id, r.OrgId, r.CreatedByUserId, r.Name, r.Prompt, r.LlmModel,
        r.SelectedRepository, r.ScheduleKind, r.ScheduleTimeUtc,
        r.IntervalMinutes, r.Weekday, r.Enabled, r.NextRunAt, r.LastRunAt,
        r.CreatedAt, r.UpdatedAt);

    private static AutomationRunInfo ToRunInfo(AutomationRunRecord r) => new(
        r.Id, r.AutomationId, r.StartedAt, r.FinishedAt, r.Status,
        r.ConversationId, r.Error);

    /// <summary>Validação de upsert (SPEC-47 RF-003).</summary>
    public static string? Validate(
        string? name, string? prompt, string? scheduleKind,
        string? scheduleTimeUtc, int? intervalMinutes,
        DateTimeOffset? nextRunAt)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "name required";
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "prompt required";
        }

        if (prompt.Length > MaxPromptChars)
        {
            return $"prompt exceeds {MaxPromptChars} chars";
        }

        if (!AutomationScheduleKinds.All.Contains(scheduleKind ?? ""))
        {
            return "schedule_kind must be interval|daily|weekly|once";
        }

        switch (scheduleKind)
        {
            case AutomationScheduleKinds.Interval
                when (intervalMinutes ?? 0) < MinIntervalMinutes:
                return $"interval_minutes must be >= {MinIntervalMinutes}";
            case AutomationScheduleKinds.Daily
                when string.IsNullOrWhiteSpace(scheduleTimeUtc):
                return "schedule_time_utc (HH:mm) required for daily";
            case AutomationScheduleKinds.Weekly
                when string.IsNullOrWhiteSpace(scheduleTimeUtc):
                return "schedule_time_utc (HH:mm) required for weekly";
            case AutomationScheduleKinds.Once when nextRunAt is null:
                return "next_run_at required for once";
        }

        return null;
    }

    private void Reschedule(AutomationRecord a, DateTimeOffset now)
    {
        a.NextRunAt = a.Enabled
            ? AutomationSchedule.ComputeNextRun(
                a.ScheduleKind, a.ScheduleTimeUtc, a.IntervalMinutes,
                a.Weekday, a.NextRunAt, now)
            : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AutomationInfo>> ListAsync(
        Guid ownerUserId, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var rows = await db.Automations.AsNoTracking()
            .Where(a => a.CreatedByUserId == owner)
            .OrderBy(a => a.Name).ThenBy(a => a.Id)
            .ToListAsync(ct);
        return rows.Select(ToInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<AutomationInfo?> GetAsync(
        Guid id, Guid ownerUserId, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var r = await db.Automations.AsNoTracking()
            .FirstOrDefaultAsync(
                a => a.Id == id && a.CreatedByUserId == owner, ct);
        return r is null ? null : ToInfo(r);
    }

    /// <inheritdoc />
    public async Task<(AutomationInfo? Info, string? Error)> CreateAsync(
        AutomationUpsert req, Guid ownerUserId, Guid? orgId,
        CancellationToken ct)
    {
        var kind = req.ScheduleKind?.Trim().ToLowerInvariant() ?? "";
        var error = Validate(req.Name, req.Prompt, kind,
            req.ScheduleTimeUtc, req.IntervalMinutes, req.NextRunAt);
        if (error is not null)
        {
            return (null, error);
        }

        var now = DateTimeOffset.UtcNow;
        var rec = new AutomationRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            CreatedByUserId = ownerUserId.ToString(),
            Name = req.Name!.Trim(),
            Prompt = req.Prompt!,
            LlmModel = req.LlmModel,
            SelectedRepository = req.SelectedRepository,
            ScheduleKind = kind,
            ScheduleTimeUtc = req.ScheduleTimeUtc,
            IntervalMinutes = req.IntervalMinutes ?? 0,
            Weekday = req.Weekday,
            Enabled = req.Enabled ?? true,
            NextRunAt = req.NextRunAt,
            CreatedAt = now, UpdatedAt = now,
        };
        Reschedule(rec, now);
        db.Automations.Add(rec);
        await db.SaveChangesAsync(ct);
        return (ToInfo(rec), null);
    }

    /// <inheritdoc />
    public async Task<(AutomationInfo? Info, string? Error)> UpdateAsync(
        Guid id, AutomationUpsert req, Guid ownerUserId,
        CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var rec = await db.Automations.FirstOrDefaultAsync(
            a => a.Id == id && a.CreatedByUserId == owner, ct);
        if (rec is null)
        {
            return (null, "not found");
        }

        var kind = req.ScheduleKind?.Trim().ToLowerInvariant() ?? rec.ScheduleKind;
        var time = req.ScheduleTimeUtc ?? rec.ScheduleTimeUtc;
        var interval = req.IntervalMinutes ?? rec.IntervalMinutes;
        var next = req.NextRunAt ?? rec.NextRunAt;
        var error = Validate(
            req.Name ?? rec.Name, req.Prompt ?? rec.Prompt, kind, time,
            interval, kind == AutomationScheduleKinds.Once ? next : next);
        if (error is not null)
        {
            return (null, error);
        }

        if (req.Name is not null) { rec.Name = req.Name.Trim(); }
        if (req.Prompt is not null) { rec.Prompt = req.Prompt; }
        if (req.LlmModel is not null) { rec.LlmModel = req.LlmModel; }
        if (req.SelectedRepository is not null)
        {
            rec.SelectedRepository = req.SelectedRepository;
        }
        rec.ScheduleKind = kind;
        rec.ScheduleTimeUtc = time;
        rec.IntervalMinutes = interval;
        if (req.Weekday is not null) { rec.Weekday = req.Weekday; }
        if (req.NextRunAt is not null) { rec.NextRunAt = req.NextRunAt; }
        if (req.Enabled is not null) { rec.Enabled = req.Enabled.Value; }
        rec.UpdatedAt = DateTimeOffset.UtcNow;
        Reschedule(rec, rec.UpdatedAt);
        await db.SaveChangesAsync(ct);
        return (ToInfo(rec), null);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        Guid id, Guid ownerUserId, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var rec = await db.Automations.FirstOrDefaultAsync(
            a => a.Id == id && a.CreatedByUserId == owner, ct);
        if (rec is null)
        {
            return false;
        }

        db.Automations.Remove(rec);
        await db.AutomationRuns
            .Where(r => r.AutomationId == id).ExecuteDeleteAsync(ct);
        await db.AutomationHooks
            .Where(h => h.AutomationId == id).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<(AutomationRunInfo? Run, bool Fired)> RunNowAsync(
        Guid id, Guid ownerUserId, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var rec = await db.Automations.FirstOrDefaultAsync(
            a => a.Id == id && a.CreatedByUserId == owner, ct);
        if (rec is null)
        {
            return (null, false);
        }

        var run = await FireAsync(rec, actor: "user", ip: null, ct);
        return (ToRunInfo(run), run.Status != "skipped");
    }

    /// <summary>
    /// Núcleo do disparo: guard de concorrência + start-task como owner
    /// + run row + reagenda + audit (SPEC-47 RF-002/RF-006).
    /// </summary>
    internal async Task<AutomationRunRecord> FireAsync(
        AutomationRecord rec, string actor, string? ip,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // Concurrency guard: nunca 2 runs simultâneas da mesma automation.
        var hasRunning = await db.AutomationRuns.AnyAsync(
            r => r.AutomationId == rec.Id && r.Status == "running", ct);
        if (hasRunning)
        {
            var skipped = new AutomationRunRecord
            {
                Id = Guid.NewGuid(), AutomationId = rec.Id,
                StartedAt = now, StartedAtTicks = now.UtcTicks,
                FinishedAt = now,
                Status = "skipped", Error = "previous run still running",
            };
            db.AutomationRuns.Add(skipped);
            await db.SaveChangesAsync(ct);
            logger?.LogInformation(
                "automation {Id} skipped — previous run still running", rec.Id);
            return skipped;
        }

        var run = new AutomationRunRecord
        {
            Id = Guid.NewGuid(), AutomationId = rec.Id,
            StartedAt = now, StartedAtTicks = now.UtcTicks,
            Status = "running",
        };
        db.AutomationRuns.Add(run);
        try
        {
            var ownerId = Guid.Parse(rec.CreatedByUserId);
            var task = await conversations.CreateForUserAsync(
                ownerId, rec.OrgId,
                new CreateConversationRequest(
                    new InitialMessage("user", [rec.Prompt], Run: true),
                    rec.SelectedRepository, null, rec.LlmModel, null),
                ct);
            run.ConversationId = task.AppConversationId;
            run.FinishedAt = DateTimeOffset.UtcNow;

            // SPEC-47 RF-006: origem automation:{id} no stream da conversa.
            if (run.ConversationId is { } convId)
            {
                await events.AppendAsync(convId, new AgentDiagnosticEvent
                {
                    Severity = "info", Code = "automation",
                    Message = $"fired by automation:{rec.Id} ({rec.Name})",
                }, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Status = "failed";
            run.FinishedAt = DateTimeOffset.UtcNow;
            // RF-006: quota negada → error=quota prefixado.
            run.Error = ex is PlanLimitExceededException
                ? $"quota: {ex.Message}"
                : ex.Message;
        }

        rec.LastRunAt = now;
        // once → esgota após o primeiro disparo.
        if (rec.ScheduleKind == AutomationScheduleKinds.Once)
        {
            rec.Enabled = false;
            rec.NextRunAt = null;
        }
        else
        {
            Reschedule(rec, now);
        }
        rec.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        if (audit is not null)
        {
            await audit.RecordAsync("automation_fired",
                actorUserId: actor == "user" ? rec.CreatedByUserId : null,
                orgId: rec.OrgId, targetType: "automation",
                targetId: rec.Id.ToString(), ip: ip,
                metadata: new
                {
                    run_id = run.Id, run.Status,
                    run.ConversationId, actor,
                }, cancellationToken: CancellationToken.None);
            await audit.RecordAsync("automation_run_created",
                actorUserId: rec.CreatedByUserId, orgId: rec.OrgId,
                targetType: "automation_run", targetId: run.Id.ToString(),
                ip: ip, metadata: new
                {
                    rec.Name, run.Status, run.ConversationId, actor,
                }, cancellationToken: CancellationToken.None);
        }

        return run;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AutomationRunInfo>> ListRunsAsync(
        Guid id, Guid ownerUserId, int take, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var owned = await db.Automations.AsNoTracking().AnyAsync(
            a => a.Id == id && a.CreatedByUserId == owner, ct);
        if (!owned)
        {
            return [];
        }

        var rows = await db.AutomationRuns.AsNoTracking()
            .Where(r => r.AutomationId == id)
            .OrderByDescending(r => r.StartedAtTicks)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct);
        return rows.Select(ToRunInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AutomationRunInfo>> ListAllRunsAsync(
        Guid ownerUserId, DateTimeOffset? from, DateTimeOffset? to,
        int take, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var ownedIds = db.Automations.AsNoTracking()
            .Where(a => a.CreatedByUserId == owner)
            .Select(a => a.Id);
        var q = db.AutomationRuns.AsNoTracking()
            .Where(r => ownedIds.Contains(r.AutomationId));
        if (from is not null) { q = q.Where(r => r.StartedAt >= from); }
        if (to is not null) { q = q.Where(r => r.StartedAt <= to); }
        var rows = await q.OrderByDescending(r => r.StartedAtTicks)
            .Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
        return rows.Select(ToRunInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<(AutomationHookInfo? Hook, string? Token)> CreateHookAsync(
        Guid automationId, Guid ownerUserId, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var owned = await db.Automations.AnyAsync(
            a => a.Id == automationId && a.CreatedByUserId == owner, ct);
        if (!owned)
        {
            return (null, null);
        }

        // Prefixo identificável p/ secret-scanning; hash cobre a string
        // completa apresentada no trigger.
        var token = "ohn_hook_" + Convert.ToHexString(
            RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var rec = new AutomationHookRecord
        {
            Id = Guid.NewGuid(), AutomationId = automationId,
            TokenHash = HashToken(token),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.AutomationHooks.Add(rec);
        await db.SaveChangesAsync(ct);
        return (new AutomationHookInfo(
            rec.Id, rec.AutomationId, rec.CreatedAt, rec.LastUsedAt), token);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteHookAsync(
        Guid hookId, Guid ownerUserId, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var hook = await db.AutomationHooks.FirstOrDefaultAsync(
            h => h.Id == hookId, ct);
        if (hook is null)
        {
            return false;
        }

        var owned = await db.Automations.AnyAsync(
            a => a.Id == hook.AutomationId && a.CreatedByUserId == owner, ct);
        if (!owned)
        {
            return false;
        }

        db.AutomationHooks.Remove(hook);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<AutomationRunInfo?> FireByTokenAsync(
        string token, string? ip, CancellationToken ct)
    {
        var hook = await db.AutomationHooks.FirstOrDefaultAsync(
            h => h.TokenHash == HashToken(token), ct);
        if (hook is null)
        {
            return null;
        }

        var rec = await db.Automations.FirstOrDefaultAsync(
            a => a.Id == hook.AutomationId, ct);
        if (rec is null)
        {
            return null;
        }

        hook.LastUsedAt = DateTimeOffset.UtcNow;
        var run = await FireAsync(rec, actor: "hook", ip: ip, ct);
        await db.SaveChangesAsync(ct);
        return ToRunInfo(run);
    }

    /// <inheritdoc />
    public async Task TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        var ticks = now.UtcTicks;
        var due = await db.Automations
            .Where(a => a.Enabled && a.NextRunAtTicks != null
                && a.NextRunAtTicks <= ticks)
            .ToListAsync(ct);
        foreach (var a in due)
        {
            try
            {
                await FireAsync(a, actor: "schedule", ip: null, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Falha numa automation não bloqueia as demais (RF-005 NFR).
                logger?.LogWarning(ex,
                    "automation {Id} tick fire failed", a.Id);
                try
                {
                    a.NextRunAt = AutomationSchedule.ComputeNextRun(
                        a.ScheduleKind, a.ScheduleTimeUtc,
                        a.IntervalMinutes, a.Weekday, a.NextRunAt, now);
                    await db.SaveChangesAsync(ct);
                }
                catch (Exception inner)
                    when (inner is not OperationCanceledException)
                {
                    logger?.LogWarning(inner,
                        "automation {Id} reschedule failed", a.Id);
                }
            }
        }

        // Reconcile: runs running → status da conversa linkada.
        var running = await db.AutomationRuns
            .Where(r => r.Status == "running").ToListAsync(ct);
        if (running.Count == 0)
        {
            return;
        }

        var convIds = running.Where(r => r.ConversationId != null)
            .Select(r => r.ConversationId!.Value).ToList();
        var statuses = await db.Conversations.AsNoTracking()
            .Where(c => convIds.Contains(c.Id))
            .Select(c => new { c.Id, c.ExecutionStatus })
            .ToDictionaryAsync(x => x.Id, x => x.ExecutionStatus, ct);
        var changed = false;
        foreach (var r in running)
        {
            if (r.ConversationId is not { } cid
                || !statuses.TryGetValue(cid, out var st))
            {
                continue;
            }

            var done = st switch
            {
                nameof(ConversationExecutionStatus.Finished) => "succeeded",
                nameof(ConversationExecutionStatus.Error)
                    or nameof(ConversationExecutionStatus.Archived) =>
                    "failed",
                _ => null,
            };
            if (done is null)
            {
                continue;
            }

            r.Status = done;
            r.FinishedAt ??= DateTimeOffset.UtcNow;
            if (done == "failed" && r.Error is null)
            {
                r.Error = $"conversation {st.ToLowerInvariant()}";
            }
            changed = true;
        }
        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)))
            .ToLowerInvariant();
}
