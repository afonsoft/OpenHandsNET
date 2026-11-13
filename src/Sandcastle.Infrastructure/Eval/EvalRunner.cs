using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Conversations;
using Sandcastle.Application.Eval;
using Sandcastle.Application.Events;
using Sandcastle.Application.Sandbox;
using Sandcastle.Domain.Events;
using Sandcastle.Domain.Sandbox;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Eval;

/// <summary>
/// SPEC-152 RF-002 — per-run orchestrator. For each task: ephemeral
/// sandbox (via the normal start-task pipeline) → setup_cmds → agent
/// conversation (<c>agent_type=eval</c>, hermetic, model pinned) →
/// checker inside the same sandbox → verdict + evidence persisted.
/// Paralelismo <c>Eval:MaxConcurrency</c> com
/// <see cref="IDbContextFactory{TContext}"/> — um DbContext por
/// task/operação (DbContext não é thread-safe); cancelamento por
/// poll no status da run + deadline por task
/// (<c>timeout_s|Eval:TaskTimeout</c>).
/// </summary>
public sealed class EvalRunner(
    IDbContextFactory<AppDbContext> dbFactory,
    IAppConversationService conversations,
    ISandboxService sandboxes,
    ISandboxExecutor executor,
    IEventService events,
    IEvalSuiteResolver suites,
    IOptions<EvalOptions> options,
    ILogger<EvalRunner> logger) : IEvalRunner
{
    private readonly EvalOptions _opt = options.Value;

    /// <summary>Statuses que encerram o poll do agente.</summary>
    private static readonly HashSet<string> ConvTerminal = new(
        StringComparer.Ordinal)
    {
        Domain.Conversations.ConversationExecutionStatus.Finished
            .ToString(),
        Domain.Conversations.ConversationExecutionStatus.Error
            .ToString(),
        Domain.Conversations.ConversationExecutionStatus.Archived
            .ToString(),
    };

    /// <inheritdoc />
    public async Task RunAsync(Guid runId, CancellationToken ct)
    {
        List<(Guid ResultId, EvalTaskV1 Task)> work;
        {
            await using var db = await dbFactory
                .CreateDbContextAsync(ct);
            var run = await db.EvalRuns.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Id == runId, ct);
            if (run is null
                || run.Status != EvalRunStatuses.Queued)
            {
                return; // idempotente — cancel/double-enqueue é no-op.
            }

            IReadOnlyList<EvalTaskV1> tasks;
            try
            {
                var adapter = suites.Get(run.Suite)
                    ?? throw new EvalSuiteException(
                        $"unknown suite '{run.Suite}'");
                tasks = await adapter.LoadAsync(
                    new EvalSuiteLoadContext(
                        run.DatasetPath, _opt.DatasetCacheDir), ct);
            }
            catch (Exception ex)
                when (ex is not OperationCanceledException)
            {
                run.Status = EvalRunStatuses.Failed;
                run.Error = Truncate(ex.Message, 1900);
                run.FinishedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                return;
            }

            if (tasks.Count == 0)
            {
                run.Status = EvalRunStatuses.Failed;
                run.Error = "suite returned no tasks";
                run.FinishedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                return;
            }

            // Materializa result rows (dedup por (run,task) — re-entry).
            var selected = tasks.Take(run.NTasks).ToList();
            var have = (await db.EvalResults.IgnoreQueryFilters()
                .Where(r => r.RunId == runId)
                .Select(r => r.TaskId).ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var task in selected.Where(
                t => !have.Contains(t.TaskId)))
            {
                db.EvalResults.Add(new EvalResultRecord
                {
                    Id = Guid.NewGuid(),
                    OrgId = run.OrgId,
                    RunId = runId,
                    TaskId = task.TaskId,
                    Status = EvalResultStatuses.Pending,
                    TaskJson = EvalTaskParser.ToJson(task),
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }

            await db.SaveChangesAsync(ct);

            // Cancel race: flip queued→running apenas se ainda queued —
            // um cancel entre a leitura e aqui vence (run não revive).
            var flipped = await db.EvalRuns.IgnoreQueryFilters()
                .Where(r => r.Id == runId
                    && r.Status == EvalRunStatuses.Queued)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status,
                        EvalRunStatuses.Running)
                    .SetProperty(r => r.StartedAt,
                        DateTimeOffset.UtcNow), ct);
            if (flipped == 0)
            {
                // Cancelada mid-load — pending recém-materializadas
                // viram error/"run cancelled".
                await db.EvalResults.IgnoreQueryFilters()
                    .Where(r => r.RunId == runId
                        && r.Status == EvalResultStatuses.Pending)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.Status,
                            EvalResultStatuses.Done)
                        .SetProperty(r => r.Verdict, EvalVerdicts.Error)
                        .SetProperty(r => r.Error, "run cancelled")
                        .SetProperty(r => r.FinishedAt,
                            DateTimeOffset.UtcNow), ct);
                return;
            }

            var pending = await db.EvalResults.IgnoreQueryFilters()
                .Where(r => r.RunId == runId
                    && r.Status == EvalResultStatuses.Pending)
                .OrderBy(r => r.CreatedAtTicks)
                .Select(r => new { r.Id, r.TaskId })
                .ToListAsync(ct);
            var taskById = selected.ToDictionary(
                t => t.TaskId, StringComparer.Ordinal);
            work = pending
                .Select(p => (p.Id, taskById[p.TaskId]))
                .ToList();
        }

        using var sem = new SemaphoreSlim(_opt.EffectiveConcurrency);
        var workers = work.Select(async item =>
        {
            if (await IsCancelledAsync(runId, ct)
                || ct.IsCancellationRequested)
            {
                await MarkCancelledAsync(item.ResultId);
                return;
            }

            await sem.WaitAsync(ct);
            try
            {
                if (await IsCancelledAsync(runId, ct))
                {
                    await MarkCancelledAsync(item.ResultId);
                    return;
                }

                await ExecuteTaskAsync(
                    runId, item.ResultId, item.Task, ct);
            }
            catch (OperationCanceledException)
                when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "eval task {TaskId} crashed",
                    item.Task.TaskId);
                await PersistVerdictAsync(item.ResultId,
                    EvalVerdicts.Error, null,
                    $"runner: {ex.Message}", null);
            }
            finally
            {
                sem.Release();
            }
        }).ToList();
        await Task.WhenAll(workers);

        await FinalizeRunAsync(runId);
    }

    /// <summary>One task: sandbox → setup → agent → checker → verdict.</summary>
    private async Task ExecuteTaskAsync(Guid runId, Guid resultId,
        EvalTaskV1 task, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var result = await db.EvalResults.IgnoreQueryFilters()
            .FirstAsync(r => r.Id == resultId, ct);
        var run = await db.EvalRuns.IgnoreQueryFilters()
            .FirstAsync(r => r.Id == runId, ct);
        result.Status = EvalResultStatuses.Running;
        await db.SaveChangesAsync(ct);
        var evidence = new StringBuilder();
        Domain.Sandbox.Sandbox? sandbox = null;
        var timeoutS = task.TimeoutS ?? _opt.TaskTimeoutSec;
        using var deadline = CancellationTokenSource
            .CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutS));
        var taskCt = deadline.Token;

        try
        {
            // 1. spec + conversa eval (sandbox efêmero via start-task).
            var specId = await ResolveSpecIdAsync(task, taskCt);
            if (!Guid.TryParse(run.CreatedByUserId, out var owner))
            {
                throw new EvalSuiteException(
                    "run has no owner user id");
            }

            var startTask = await conversations.CreateForUserAsync(
                owner, run.OrgId, new CreateConversationRequest(
                    InitialMessage: new InitialMessage(
                        "user", [task.Prompt], Run: true),
                    SelectedRepository: task.Repo,
                    AgentType: EvalAgentTypes.Eval,
                    // SPEC-152: route_override = pin duro no
                    // LlmRouteContext.PreferredModel (SPEC-149) —
                    // `model` fica como label de scorecard.
                    LlmModel: run.RouteOverride ?? run.Model,
                    SandboxSpecId: specId,
                    ConfirmationMode: false,
                    Mode: "build"), taskCt);
            var conv = await WaitReadyAsync(db, startTask, taskCt);
            result.SessionId = conv.Id;
            await db.SaveChangesAsync(ct);
            if (conv.SandboxId is { } sbId)
            {
                sandbox = await sandboxes.GetAsync(sbId, taskCt);
            }

            // 2. setup_cmds dentro do sandbox (falha → error, não scored).
            if (sandbox is not null && task.SetupCmds.Count > 0)
            {
                var setup = await RunSetupAsync(sandbox, task,
                    evidence, taskCt);
                if (setup is not null)
                {
                    await SaveVerdictAsync(db, result,
                        EvalVerdicts.Error, sw, setup, evidence);
                    return;
                }
            }

            // 3. aguarda o agente (loop já rodando — kickoff do worker).
            var agentEnd = await WaitAgentAsync(db, conv.Id, taskCt);
            if (agentEnd is not null)
            {
                await SaveVerdictAsync(db, result,
                    agentEnd == "timeout"
                        ? EvalVerdicts.Timeout : EvalVerdicts.Error,
                    sw, agentEnd == "timeout" ? null : agentEnd,
                    evidence);
                return;
            }

            // 4. checker — mesmo sandbox, untrusted → trust boundary ok.
            if (sandbox is null)
            {
                await SaveVerdictAsync(db, result, EvalVerdicts.Error,
                    sw, "sandbox missing after agent run", evidence);
                return;
            }

            var verdict = await RunCheckerAsync(sandbox, task,
                evidence, taskCt);
            var (tokens, promptTok, complTok, cost) =
                await CollectUsageAsync(conv.Id, ct);
            result.Tokens = tokens;
            result.PromptTokens = promptTok;
            result.CompletionTokens = complTok;
            result.CostUsd = cost;
            await SaveVerdictAsync(db, result, verdict.Verdict, sw,
                verdict.Error, evidence);
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested
                && !ct.IsCancellationRequested)
        {
            await SaveVerdictAsync(db, result, EvalVerdicts.Timeout,
                sw, $"task timeout ({timeoutS}s)", evidence);
        }
        finally
        {
            // Sandbox efêmero — sempre destruído (idempotente).
            if (sandbox is not null)
            {
                try
                {
                    await sandboxes.DeleteAsync(
                        sandbox.Id, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "eval sandbox {SandboxId} delete failed",
                        sandbox.Id);
                }
            }
        }
    }

    /// <summary>spec id for image tasks (deterministic per image).</summary>
    private async Task<Guid?> ResolveSpecIdAsync(
        EvalTaskV1 task, CancellationToken ct)
    {
        if (task.Image is not { Length: > 0 } image)
        {
            return null; // repo task → spec default.
        }

        // SPEC-152: spec efêmera por imagem — id derivado do hash da
        // imagem (dedup estável entre tasks/runs).
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(image));
        var specId = new Guid(hash[..16]);
        var spec = await sandboxes.UpsertSpecAsync(new SandboxSpec(
            specId, $"eval-{Convert.ToHexStringLower(hash[..4])}",
            image, TimeoutMinutes: Math.Max(30,
                _opt.TaskTimeoutSec / 60 + 5)), ct);
        return spec.Id;
    }

    /// <summary>Waits for the start task to reach READY (sandbox up).</summary>
    private async Task<ConversationRecord> WaitReadyAsync(
        AppDbContext db,
        Domain.Conversations.AppConversationStartTask startTask,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow
            + TimeSpan.FromSeconds(_opt.ProvisionTimeoutSec);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var rec = await db.StartTasks.IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == startTask.Id, ct);
            if (rec?.Status == Domain.Conversations.StartTaskStatus
                .Error.ToString())
            {
                throw new InvalidOperationException(
                    $"start task failed: {rec.Detail}");
            }

            if (rec?.Status == Domain.Conversations.StartTaskStatus
                .Ready.ToString()
                && rec.AppConversationId is { } convId)
            {
                var conv = await db.Conversations.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(c => c.Id == convId, ct);
                if (conv is not null)
                {
                    return conv;
                }
            }

            await Task.Delay(
                TimeSpan.FromSeconds(_opt.PollIntervalSec), ct);
        }

        throw new TimeoutException(
            $"conversation not READY within {_opt.ProvisionTimeoutSec}s");
    }

    /// <summary>Runs setup_cmds; null = ok, else the error detail.</summary>
    private async Task<string?> RunSetupAsync(
        Domain.Sandbox.Sandbox sandbox, EvalTaskV1 task,
        StringBuilder evidence, CancellationToken ct)
    {
        foreach (var cmd in task.SetupCmds)
        {
            var r = await executor.ExecAsync(sandbox, cmd,
                TimeSpan.FromSeconds(_opt.SetupCmdTimeoutSec), ct);
            evidence.AppendLine($"[setup]$ {cmd}")
                .AppendLine(Truncate(r.StdOut + r.StdErr));
            if (r.ExitCode != 0 || r.TimedOut)
            {
                return $"setup failed (exit {r.ExitCode}"
                    + (r.TimedOut ? ", timeout" : "") + $"): {cmd}";
            }
        }

        return null;
    }

    /// <summary>
    /// Polls the eval conversation until terminal. Returns
    /// <see langword="null"/> on Finished, else an error detail.
    /// </summary>
    private async Task<string?> WaitAgentAsync(
        AppDbContext db, Guid conversationId, CancellationToken ct)
    {
        while (true)
        {
            var status = await db.Conversations.IgnoreQueryFilters()
                .Where(c => c.Id == conversationId)
                .Select(c => c.ExecutionStatus)
                .FirstOrDefaultAsync(ct);
            if (status is not null && ConvTerminal.Contains(status))
            {
                return status == Domain.Conversations
                    .ConversationExecutionStatus.Finished.ToString()
                        ? null
                        : $"agent ended with status {status}";
            }

            await Task.Delay(
                TimeSpan.FromSeconds(_opt.PollIntervalSec), ct);
        }
    }

    /// <summary>Materializes verifier files + runs the checker.</summary>
    private async Task<(string Verdict, string? Error)> RunCheckerAsync(
        Domain.Sandbox.Sandbox sandbox, EvalTaskV1 task,
        StringBuilder evidence, CancellationToken ct)
    {
        // metadata.files → /eval-verifier/{relpath} (adapter-embedded
        // verifier scripts — untrusted, roda só no sandbox).
        if (task.Metadata?.TryGetValue("files", out var filesEl) == true
            && filesEl.ValueKind == JsonValueKind.Object)
        {
            var files = filesEl.Deserialize<
                Dictionary<string, string>>();
            if (files is not null)
            {
                foreach (var (rel, content) in files)
                {
                    var path = TerminalBench2SuiteAdapter.VerifierDir
                        + "/" + rel;
                    await executor.WriteFileAsync(
                        sandbox, path, content, ct);
                }
            }
        }

        var checkerTimeout = task.Metadata?.TryGetValue(
            "verifier_timeout_s", out var vt) == true
                && vt.ValueKind == JsonValueKind.Number
                && vt.TryGetInt32(out var v) && v > 0
                    ? v : _opt.CheckerTimeoutSec;
        var cmd = task.Checker!.Cmd is { Length: > 0 } c
            ? c
            : $"bash {task.Checker.Path}";
        evidence.AppendLine($"[checker]$ {cmd}");
        CommandResult r;
        try
        {
            r = await executor.ExecAsync(sandbox, cmd,
                TimeSpan.FromSeconds(checkerTimeout), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            evidence.AppendLine("[checker] exec error: " + ex.Message);
            return (EvalVerdicts.Error, $"checker exec: {ex.Message}");
        }

        evidence.AppendLine(Truncate(r.StdOut + r.StdErr))
            .AppendLine($"[checker] exit={r.ExitCode}");
        return r.TimedOut
            ? (EvalVerdicts.Error, "checker timed out")
            : r.ExitCode == 0
                ? (EvalVerdicts.Resolved, null)
                : (EvalVerdicts.Failed,
                    $"checker exit {r.ExitCode}");
    }

    /// <summary>Sums TokenEvent usage of the eval conversation.</summary>
    private async Task<(long Total, long Prompt, long Completion,
        decimal Cost)> CollectUsageAsync(
            Guid conversationId, CancellationToken ct)
    {
        long prompt = 0, completion = 0;
        decimal cost = 0m;
        string? cursor = null;
        do
        {
            var page = await events.SearchAsync(conversationId,
                new EventQuery(KindEq: "TokenEvent",
                    PageId: cursor, Limit: 100), ct);
            foreach (var e in page.Items.OfType<TokenEvent>())
            {
                prompt += e.PromptTokens ?? 0;
                completion += e.CompletionTokens ?? 0;
                cost += e.CostUsd ?? 0m;
            }

            cursor = page.NextPageId;
        }
        while (cursor is not null);

        return (prompt + completion, prompt, completion, cost);
    }

    /// <summary>Persists verdict on the task-owned context.</summary>
    private static async Task SaveVerdictAsync(AppDbContext db,
        EvalResultRecord result, string verdict,
        System.Diagnostics.Stopwatch sw, string? error,
        StringBuilder? evidence)
    {
        result.Status = EvalResultStatuses.Done;
        result.Verdict = verdict;
        result.DurationS = sw.Elapsed.TotalSeconds;
        result.Error = error;
        result.FinishedAt = DateTimeOffset.UtcNow;
        if (evidence is { Length: > 0 })
        {
            var tail = evidence.Length > 8192
                ? evidence.ToString(evidence.Length - 8192, 8192)
                : evidence.ToString();
            result.LogDigest = Digest(
                Agent.SecretRedactor.Redact(tail));
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Verdict save for the outer catch (own context).</summary>
    private async Task PersistVerdictAsync(Guid resultId,
        string verdict, double? durationS, string? error,
        StringBuilder? evidence)
    {
        try
        {
            await using var db = await dbFactory
                .CreateDbContextAsync();
            var result = await db.EvalResults.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Id == resultId);
            if (result is null
                || result.Status == EvalResultStatuses.Done)
            {
                return;
            }

            result.Status = EvalResultStatuses.Done;
            result.Verdict = verdict;
            result.DurationS = durationS;
            result.Error = error;
            result.FinishedAt = DateTimeOffset.UtcNow;
            if (evidence is { Length: > 0 })
            {
                var tail = evidence.Length > 8192
                    ? evidence.ToString(
                        evidence.Length - 8192, 8192)
                    : evidence.ToString();
                result.LogDigest = Digest(
                    Agent.SecretRedactor.Redact(tail));
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "eval result {ResultId} verdict persist failed",
                resultId);
        }
    }

    /// <summary>Pending/running rows left by a cancelled run → error.</summary>
    private async Task MarkCancelledAsync(Guid resultId)
    {
        try
        {
            await using var db = await dbFactory
                .CreateDbContextAsync();
            var result = await db.EvalResults.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Id == resultId);
            if (result is null
                || result.Status == EvalResultStatuses.Done)
            {
                return;
            }

            result.Status = EvalResultStatuses.Done;
            result.Verdict = EvalVerdicts.Error;
            result.Error = "run cancelled";
            result.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "eval result {ResultId} cancel persist failed",
                resultId);
        }
    }

    /// <summary>Aggregates counters + stats and lands the run.</summary>
    private async Task FinalizeRunAsync(Guid runId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        // Releitura — status pode ter mudado p/ cancelled durante o loop.
        var run = await db.EvalRuns.IgnoreQueryFilters()
            .FirstAsync(r => r.Id == runId);
        var results = await db.EvalResults.IgnoreQueryFilters()
            .Where(r => r.RunId == runId)
            .ToListAsync();
        var cancelled = run.Status == EvalRunStatuses.Cancelled;
        run.ResolvedN = results.Count(
            r => r.Verdict == EvalVerdicts.Resolved);
        run.FailedN = results.Count(
            r => r.Verdict == EvalVerdicts.Failed);
        run.ErrorN = results.Count(
            r => r.Verdict == EvalVerdicts.Error);
        run.TimeoutN = results.Count(
            r => r.Verdict == EvalVerdicts.Timeout);
        run.ScorePct = run.NTasks == 0 ? 0
            : Math.Round(100.0 * run.ResolvedN / run.NTasks, 1);
        run.ActualCostUsd = results.Sum(r => r.CostUsd);
        run.FinishedAt = DateTimeOffset.UtcNow;
        run.Status = cancelled
            ? EvalRunStatuses.Cancelled : EvalRunStatuses.Completed;
        run.StatsJson = EvalService.BuildStatsJson(run, results);
        await db.SaveChangesAsync(CancellationToken.None);
        logger.LogInformation(
            "eval run {RunId} {Status} — score {Score}%"
            + " ({Resolved}/{N})",
            run.Id, run.Status, run.ScorePct, run.ResolvedN, run.NTasks);
    }

    /// <summary>Is the run cancelled? (cheap status re-read, own ctx)</summary>
    private async Task<bool> IsCancelledAsync(
        Guid runId, CancellationToken ct)
    {
        await using var db = await dbFactory
            .CreateDbContextAsync(ct);
        return await db.EvalRuns.IgnoreQueryFilters()
            .Where(r => r.Id == runId)
            .Select(r => r.Status).FirstOrDefaultAsync(ct)
            == EvalRunStatuses.Cancelled;
    }

    private static string Digest(string text) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Truncate(string s, int max = 4096) =>
        s.Length <= max ? s : s[..max];
}