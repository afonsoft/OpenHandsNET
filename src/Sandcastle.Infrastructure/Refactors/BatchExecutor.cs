using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Delegation;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Refactors;
using Sandcastle.Domain.Events;
using Sandcastle.Infrastructure.Agent;
using Sandcastle.Infrastructure.Agent.SystemPrompt;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Refactors;

/// <summary>
/// <see cref="IBatchExecutor"/> — SPEC-144 RF-002. One orchestration pass
/// per plan (re-entrant: merge webhooks and retries re-enqueue it):
/// <list type="number">
/// <item>dependency eval — <c>pending</c> → <c>ready</c> when every
///   <c>depends_on</c> batch is <c>merged</c>; a <c>failed</c> dep marks
///   the dependent <c>blocked</c> (a dep retry walks it back).</item>
/// <item>fan-out — <c>ready</c> batches delegate <c>refactor-worker</c>
///   children under the coordinator conversation while active &lt;
///   <c>max_concurrency</c> (clamped to <see
///   cref="RefactorOptions.MaxConcurrencyCap"/>); the prompt carries
///   branch <c>refactor/{plan_id}/{n}</c>, the files, the goal, the
///   verify command, the fix-loop cap and the PR base (stacked =
///   predecessor head).</item>
/// <item>collect — terminal child → <c>verifying</c> → parse
///   <c>refactor_batch_v1</c>: verify_passed + PR url →
///   <c>pr_open</c>; verify failed / no output / no PR →
///   <c>failed</c> (digest of the redacted log tail recorded).</item>
/// <item>quiesce — nothing active → exit; merge webhooks/retries
///   re-enqueue the pass. All merged → plan <c>completed</c>.</item>
/// </list>
/// </summary>
public sealed class BatchExecutor(
    AppDbContext db,
    IDelegationService delegation,
    IOptions<RefactorOptions> options,
    ILogger<BatchExecutor> logger,
    IAuditService? audit = null) : IBatchExecutor
{
    /// <summary>Terminal child statuses (enum names + delegate literals).</summary>
    internal static readonly HashSet<string> TerminalConversationStatuses =
        new(StringComparer.Ordinal)
        {
            nameof(Domain.Conversations.ConversationExecutionStatus.Stopped),
            nameof(Domain.Conversations.ConversationExecutionStatus.Finished),
            nameof(Domain.Conversations.ConversationExecutionStatus.Error),
            nameof(Domain.Conversations.ConversationExecutionStatus.Archived),
            "COMPLETED", "FAILED", "STOPPED", "CANCELLED",
        };

    /// <summary>Branch name for a batch — <c>refactor/{plan}/{n}</c>.</summary>
    internal static string BranchFor(Guid planId, int n) =>
        $"refactor/{planId:N}/{n}";

    /// <inheritdoc />
    public async Task RunAsync(Guid planId, CancellationToken ct)
    {
        var plan = await db.RefactorPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == planId, ct);
        if (plan is null
            || plan.Status != RefactorPlanStatuses.Running
            || plan.CoordinatorConversationId is not { } coordinatorId)
        {
            return;
        }

        var o = options.Value;
        var maxConcurrency = Math.Clamp(
            plan.MaxConcurrency, 1, Math.Max(1, o.MaxConcurrencyCap));
        var poll = TimeSpan.FromSeconds(
            Math.Clamp(o.PollIntervalSeconds, 2, 120));
        var childTimeout = TimeSpan.FromMinutes(
            Math.Max(1, o.ChildTimeoutMinutes));

        var stats = new Dictionary<string, object?>
        {
            ["dispatched"] = 0,
            ["succeeded"] = 0,
            ["failed"] = 0,
        };

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (await IsPlanStoppedAsync(planId, ct))
                {
                    return;
                }

                var batches = await db.RefactorBatches
                    .Where(b => b.PlanId == planId)
                    .OrderBy(b => b.N)
                    .ToListAsync(ct);

                // 1) dependency eval: pending → ready|blocked.
                EvaluateDependencies(batches);
                var active = batches
                    .Where(b => RefactorBatchStates.Active.Contains(b.State))
                    .ToList();
                var ready = batches
                    .Where(b => b.State == RefactorBatchStates.Ready)
                    .OrderBy(b => b.N)
                    .ToList();

                // 2) fan-out while slots are open.
                while (ready.Count > 0 && active.Count < maxConcurrency)
                {
                    var batch = ready[0];
                    ready.RemoveAt(0);
                    try
                    {
                        var res = await delegation.DelegateAsync(
                            coordinatorId,
                            new DelegateTaskRequest(
                                BuildWorkerPrompt(plan, batch, batches, o),
                                SelectedRepository: batch.Repository,
                                AgentType: RefactorAgentTypes.Worker),
                            ct);
                        batch.ConversationId = res.ChildConversationId;
                        batch.State = RefactorBatchStates.Running;
                        batch.Attempts++;
                        batch.UpdatedAt = DateTimeOffset.UtcNow;
                        stats["dispatched"] = (int)stats["dispatched"]! + 1;
                        active.Add(batch);
                        await db.SaveChangesAsync(ct);
                        await AuditAsync("refactor.batch_dispatched", plan,
                            new
                            {
                                batch = batch.N,
                                conversation = res.ChildConversationId,
                                attempt = batch.Attempts,
                            }, ct);
                    }
                    catch (InvalidOperationException e)
                    {
                        // Sibling-cap race — try again next pass.
                        logger.LogDebug(e,
                            "refactor plan {Id}: delegate gated", planId);
                        break;
                    }
                    catch (Exception e)
                        when (e is not OperationCanceledException)
                    {
                        await FailBatchAsync(batch,
                            $"delegate: {e.Message}", plan, ct);
                        stats["failed"] = (int)stats["failed"]! + 1;
                    }
                }

                // 3) quiesce — nothing running and nothing dispatchable:
                // pending/blocked wait on merges or retries (which
                // re-enqueue this pass).
                if (active.Count == 0)
                {
                    await MaybeCompleteAsync(plan, batches, stats, ct);
                    return;
                }

                await Task.Delay(poll, ct);

                // 4) collect terminal children.
                var ids = active
                    .Where(b => b.ConversationId is not null)
                    .Select(b => b.ConversationId!.Value).ToList();
                var convs = await db.Conversations.IgnoreQueryFilters()
                    .Where(c => ids.Contains(c.Id))
                    .ToDictionaryAsync(c => c.Id, ct);

                var now = DateTimeOffset.UtcNow;
                foreach (var batch in active.ToList())
                {
                    if (batch.ConversationId is not { } cid
                        || !convs.TryGetValue(cid, out var conv))
                    {
                        continue;
                    }

                    var terminal = TerminalConversationStatuses
                        .Contains(conv.ExecutionStatus);
                    var timedOut = batch.UpdatedAt + childTimeout < now;
                    if (!terminal && !timedOut)
                    {
                        continue;
                    }

                    batch.State = RefactorBatchStates.Verifying;
                    batch.UpdatedAt = now;
                    var (ok, error) = await CollectBatchAsync(
                        batch, conv, timedOut, ct);
                    if (ok)
                    {
                        stats["succeeded"] =
                            (int)stats["succeeded"]! + 1;
                    }
                    else
                    {
                        stats["failed"] = (int)stats["failed"]! + 1;
                        await AuditAsync("refactor.batch_failed", plan,
                            new { batch = batch.N, error }, ct);
                    }
                    active.Remove(batch);
                }
                await db.SaveChangesAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogError(e, "refactor plan {Id} executor failed",
                planId);
            try
            {
                var fresh = await db.RefactorPlans.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(p => p.Id == planId,
                        CancellationToken.None);
                if (fresh is not null)
                {
                    fresh.StatsJson = JsonSerializer.Serialize(stats);
                    await db.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception inner)
            {
                logger.LogWarning(inner,
                    "refactor plan {Id} stats write failed", planId);
            }
        }
    }

    /// <summary>
    /// pending → ready when every depends_on batch merged; pending|ready
    /// → blocked when a dep failed (dep retry walks it back to pending —
    /// see <see cref="RefactorService.RetryBatchAsync"/>).
    /// </summary>
    internal static void EvaluateDependencies(
        List<RefactorBatchRecord> batches)
    {
        var byN = batches.ToDictionary(b => b.N);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var b in batches)
            {
                var deps = RefactorService.DeserializeInts(b.DependsOnJson);
                if (b.State == RefactorBatchStates.Pending
                    && deps.Count > 0 && deps.All(d =>
                        byN.TryGetValue(d, out var dep)
                        && dep.State == RefactorBatchStates.Merged))
                {
                    b.State = RefactorBatchStates.Ready;
                    b.UpdatedAt = DateTimeOffset.UtcNow;
                    changed = true;
                }
                else if ((b.State == RefactorBatchStates.Pending
                        || b.State == RefactorBatchStates.Ready)
                    && deps.Any(d =>
                        byN.TryGetValue(d, out var dep)
                        && dep.State == RefactorBatchStates.Failed))
                {
                    b.State = RefactorBatchStates.Blocked;
                    b.UpdatedAt = DateTimeOffset.UtcNow;
                    changed = true;
                }
            }
        }
    }

    /// <summary>
    /// Collect a terminal batch child: parse <c>refactor_batch_v1</c> +
    /// read the PR fields it produced. verify_passed + PR →
    /// <c>pr_open</c>; everything else → <c>failed</c>.
    /// </summary>
    private async Task<(bool Ok, string? Error)> CollectBatchAsync(
        RefactorBatchRecord batch,
        Conversations.ConversationRecord conv,
        bool timedOut, CancellationToken ct)
    {
        if (timedOut)
        {
            return await FailBatchAsync(batch, "child timeout", null, ct);
        }

        var output = await ParseChildOutputAsync(conv.Id, ct);
        if (output is null)
        {
            return await FailBatchAsync(batch,
                "missing refactor_batch_v1 output", null, ct);
        }

        batch.VerifyLogDigest = output.LogTail is { Length: > 0 } tail
            ? Digest(SecretRedactor.Redact(tail))
            : null;

        if (!output.VerifyPassed)
        {
            return await FailBatchAsync(batch,
                output.LogTail is { Length: > 0 } t
                    ? $"verify failed: {Truncate(SecretRedactor.Redact(t), 200)}"
                    : "verify failed", null, ct);
        }

        // Verify passed — the PR must exist (worker contract).
        var (prUrl, prNumber) = PrOf(conv);
        if (prUrl is null)
        {
            return await FailBatchAsync(batch,
                "worker finished without opening a PR", null, ct);
        }

        batch.State = RefactorBatchStates.PrOpen;
        batch.PrUrl = prUrl;
        batch.PrNumber = prNumber;
        batch.Error = null;
        batch.UpdatedAt = DateTimeOffset.UtcNow;
        return (true, null);
    }

    /// <summary>
    /// Extracts the batch child's <c>refactor_batch_v1</c> from its last
    /// agent messages (≤8, newest first — same convention as scans).
    /// </summary>
    private async Task<RefactorBatchOutput?> ParseChildOutputAsync(
        Guid conversationId, CancellationToken ct)
    {
        var payloads = await db.Events.IgnoreQueryFilters()
            .Where(e => e.ConversationId == conversationId
                && e.Kind == "MessageEvent" && e.Source == "agent"
                && e.TruncatedAtTicks == null)
            .OrderByDescending(e => e.TimestampTicks)
            .Take(8)
            .Select(e => e.Payload)
            .ToListAsync(ct);

        foreach (var payload in payloads)
        {
            var text = ScanText(payload);
            if (text is null)
            {
                continue;
            }

            var (output, error) = RefactorBatchOutputParser.Parse(text);
            if (error is null)
            {
                return output;
            }
        }
        return null;
    }

    /// <summary>llm_message.content text of a MessageEvent payload.</summary>
    private static string? ScanText(string payload)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<MessageEvent>(
                payload, EventJson.Options);
            if (msg is null
                || !msg.LlmMessage.TryGetProperty(
                    "content", out var content))
            {
                return null;
            }

            if (content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }
            if (content.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var sb = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var t)
                    && t.ValueKind == JsonValueKind.String)
                {
                    sb.AppendLine(t.GetString());
                }
            }
            return sb.Length == 0 ? null : sb.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>(url, number) of the first PR on the child conversation.</summary>
    private static (string? Url, int? Number) PrOf(
        Conversations.ConversationRecord conv)
    {
        var prs = conv.GetPullRequests();
        if (prs.Count > 0)
        {
            return (prs[0].Url, prs[0].Number);
        }
        return (conv.PullRequestUrl, conv.PullRequestNumber);
    }

    /// <summary>
    /// Worker prompt: branch + files + goal + verify gate + fix-loop cap
    /// + PR instructions (base = predecessor head when stacked).
    /// </summary>
    internal static string BuildWorkerPrompt(
        RefactorPlanRecord plan, RefactorBatchRecord batch,
        IReadOnlyList<RefactorBatchRecord> all, RefactorOptions o)
    {
        var tpl = EmbeddedPrompts.Get("refactor-worker");
        var branch = BranchFor(plan.Id, batch.N);
        var baseBranch = plan.Mode == RefactorModes.Stacked
            && RefactorService.DeserializeInts(batch.DependsOnJson)
                is { Count: > 0 } deps
            && deps.Max() is var pred && pred != batch.N
            ? BranchFor(plan.Id, pred)
            : "the repository default branch";
        var files = string.Join('\n',
            RefactorService.DeserializeStrings(batch.FilesJson)
                .Select(f => $"- {f}"));
        return tpl
            .Replace("{goal}", plan.Goal, StringComparison.Ordinal)
            .Replace("{repository}", batch.Repository,
                StringComparison.Ordinal)
            .Replace("{plan_id}", plan.Id.ToString("N"),
                StringComparison.Ordinal)
            .Replace("{n}", batch.N.ToString(), StringComparison.Ordinal)
            .Replace("{branch}", branch, StringComparison.Ordinal)
            .Replace("{base_branch}", baseBranch, StringComparison.Ordinal)
            .Replace("{files}", files, StringComparison.Ordinal)
            .Replace("{verify_cmd}", batch.VerifyCommand
                ?? "(no verify command inferred — run the repo's "
                + "standard build/test gate)", StringComparison.Ordinal)
            .Replace("{max_loops}",
                Math.Max(1, o.MaxVerifyFixLoops).ToString(),
                StringComparison.Ordinal);
    }

    private async Task<(bool Ok, string? Error)> FailBatchAsync(
        RefactorBatchRecord batch, string error,
        RefactorPlanRecord? plan, CancellationToken ct)
    {
        batch.State = RefactorBatchStates.Failed;
        batch.Error = error.Length > 900 ? error[..900] : error;
        batch.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return (false, error);
    }

    /// <summary>Non-saving variant for callers mid-transaction.</summary>
    private (bool Ok, string? Error) FailBatchNoSave(
        RefactorBatchRecord batch, string error)
    {
        batch.State = RefactorBatchStates.Failed;
        batch.Error = error.Length > 900 ? error[..900] : error;
        batch.UpdatedAt = DateTimeOffset.UtcNow;
        return (false, error);
    }

    /// <summary>
    /// Plan completion eval: every batch merged → completed (terminal);
    /// anything else stays running — blocked/failed batches wait on
    /// retries or a human cancel (§9: humans merge PRs, never auto-merge).
    /// </summary>
    private async Task MaybeCompleteAsync(
        RefactorPlanRecord plan, List<RefactorBatchRecord> batches,
        Dictionary<string, object?> stats, CancellationToken ct)
    {
        if (batches.Count > 0
            && batches.All(b => b.State == RefactorBatchStates.Merged))
        {
            var fresh = await db.RefactorPlans.IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.Id == plan.Id, ct);
            if (fresh is not null
                && fresh.Status == RefactorPlanStatuses.Running)
            {
                fresh.Status = RefactorPlanStatuses.Completed;
                fresh.FinishedAt = DateTimeOffset.UtcNow;
                fresh.StatsJson = JsonSerializer.Serialize(stats);
                await db.SaveChangesAsync(ct);
                await AuditAsync("refactor.completed", fresh, stats, ct);
            }
            return;
        }

        var p = await db.RefactorPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == plan.Id, ct);
        if (p is not null)
        {
            p.StatsJson = JsonSerializer.Serialize(stats);
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<bool> IsPlanStoppedAsync(
        Guid planId, CancellationToken ct)
    {
        var status = await db.RefactorPlans.IgnoreQueryFilters()
            .Where(p => p.Id == planId)
            .Select(p => p.Status)
            .FirstOrDefaultAsync(ct);
        return status != RefactorPlanStatuses.Running;
    }

    /// <summary>SHA-256 hex digest of a redacted verify log tail.</summary>
    internal static string Digest(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];

    private async Task AuditAsync(string action, RefactorPlanRecord plan,
        object? metadata, CancellationToken ct)
    {
        if (audit is null)
        {
            return;
        }

        try
        {
            await audit.RecordAsync(action,
                actorUserId: null, orgId: plan.OrgId,
                targetType: "refactor_plan",
                targetId: plan.Id.ToString(),
                metadata: metadata, cancellationToken: ct);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "refactor audit {Action} skipped", action);
        }
    }
}