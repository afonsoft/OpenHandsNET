using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Conversations;
using Sandcastle.Application.Git;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Sandbox;
using Sandcastle.Application.Scm;
using Sandcastle.Infrastructure.Agent;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Security;

namespace Sandcastle.Infrastructure.Scm.Autofix;

/// <summary>
/// <see cref="IAutofixRunner"/> (SPEC-141 RF-002/RF-003): loop do CI
/// autofix — por ciclo: fetch do log da falha (caps + SecretRedactor)
/// → classificação (lint|type|test|build autofixáveis; infra|timeout|
/// flaky|permissão|segredo → blocked sem consumir tentativa) → conversa
/// de fix (<c>agent_type: autofix</c>, repo já clonado no sandbox,
/// credencial da CONEXÃO provisionada via installation token) → poll de
/// <c>check_runs</c> no head novo até terminal → verde = completed /
/// vermelho = próxima tentativa (cap <c>AutofixMaxAttempts</c>) /
/// timeout = failed / cancel = cancelled. Commit status
/// <c>sandcastle/autofix</c> advisory durante todo o loop.
/// </summary>
public sealed class AutofixRunner( // NOSONAR S107 — SPEC-104 (decisão aprovada): DI ctor — aridade = lista de dependências (bundle = service locator)
    AppDbContext db,
    IScmProviderRegistry providers,
    IAppConversationService conversations,
    ISandboxService sandboxes,
    IGitService git,
    IGitHubClientFactory githubClients,
    IOptions<AutofixOptions> options,
    IAuditService audit,
    ILogger<AutofixRunner> logger) : IAutofixRunner
{
    /// <summary>Check statuses considerados terminais no poll.</summary>
    private static readonly IReadOnlySet<string> TerminalStatuses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "completed", "success", "failure", "error", "cancelled", "skipped" };

    /// <inheritdoc />
    public async Task RunAsync(Guid autofixRunId, CancellationToken ct)
    {
        var run = await db.AutofixRuns.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == autofixRunId, ct);
        if (run is null || run.Status != AutofixRunStatuses.Queued)
        {
            return; // só queued executa — entrada duplicada/terminal é no-op
        }

        var connectionRecord = await db.ScmConnections.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == run.ConnectionId, ct);
        if (connectionRecord is null)
        {
            await FinishAsync(run, AutofixRunStatuses.Failed,
                "scm connection not found", ct);
            return;
        }

        // Claim atômico queued→running: o reconcile de boot pode re-
        // enfileirar um run que também já estava no canal — só o
        // primeiro claim executa (idempotente contra double-enqueue).
        var now = DateTimeOffset.UtcNow;
        var claimed = await db.AutofixRuns.IgnoreQueryFilters()
            .Where(r => r.Id == run.Id
                && r.Status == AutofixRunStatuses.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, AutofixRunStatuses.Running)
                .SetProperty(r => r.StartedAt, now), ct);
        if (claimed == 0)
        {
            return;
        }
        run.Status = AutofixRunStatuses.Running;
        run.StartedAt = now;

        var connection = ScmConnectionService.ToModel(connectionRecord);
        try
        {
            await ExecuteAsync(run, connection, ct);
        }
        catch (NotSupportedException e)
        {
            // Provider sem Checks/Actions API — autofix é GitHub-only em v1.
            await BlockedAsync(connection, run,
                $"CI inspection not supported by "
                + $"{connection.Kind.ToWire()} provider ({e.Message})", ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e,
                "autofix run {Id} failed ({Repo}#{Pr})", run.Id,
                run.Repository, run.PrNumber);
            if (!await IsCancelledAsync(run.Id, ct))
            {
                await FinishAsync(run, AutofixRunStatuses.Failed,
                    e.Message[..Math.Min(500, e.Message.Length)], ct);
                await PostStatusAsync(connection, run, "error",
                    "Autofix failed (advisory)", ct);
                await RecordAuditAsync("scm.autofix_failed", run,
                    new { outcome = "error", error = e.Message[..Math.Min(200, e.Message.Length)] }, ct);
            }
        }
    }

    private async Task ExecuteAsync(
        AutofixRunRecord run, ScmConnection connection, CancellationToken ct)
    {
        var provider = providers.Get(connection.Kind)
            ?? throw new InvalidOperationException(
                $"provider '{connection.Kind.ToWire()}' not registered");
        var o = options.Value;
        var maxAttempts = connection.EffectiveAutofixMaxAttempts(o.MaxAttempts);

        await PostStatusAsync(connection, run, "pending",
            AutofixFormatter.StatusPending(
                run.AttemptCount + 1, maxAttempts), ct);

        while (true)
        {
            if (await IsCancelledAsync(run.Id, ct))
            {
                return;
            }

            // 1) log da falha (bounded + redacted) → classificação.
            var logTail = await FetchLogTailAsync(
                provider, connection, run, o, ct);
            var cls = CiFailureClassifier.Classify(
                run.CheckName, logTail, o.FlakyPatterns);
            if (!CiFailureClassifier.IsFixable(cls))
            {
                await BlockedAsync(connection, run,
                    CiFailureClassifier.Describe(cls), ct);
                return;
            }

            // 2) cap de tentativas (duro — nunca bypassar).
            if (run.AttemptCount >= maxAttempts)
            {
                await GiveUpAsync(provider, connection, run,
                    maxAttempts, ct);
                return;
            }

            // 3) branch do PR (prompt + push target) — resolve quando
            //    o payload não trouxe.
            run.HeadBranch ??= await ResolveHeadBranchAsync(
                provider, connection, run, ct);
            if (run.HeadBranch is null)
            {
                await BlockedAsync(connection, run,
                    "pull request head branch could not be resolved", ct);
                return;
            }

            // 4) nova tentativa + conversa de fix.
            var attempt = await StartAttemptAsync(
                run, connection, logTail, maxAttempts, ct);
            run.Status = AutofixRunStatuses.WaitingCheck;
            await db.SaveChangesAsync(ct);

            // 5) poll: push detectado → checks terminais → verde/vermelho.
            var poll = await PollAttemptAsync(
                provider, connection, run, attempt, o, ct);
            switch (poll.Kind)
            {
                case AttemptPollKind.Green:
                    attempt.Outcome = AutofixAttemptOutcomes.Fixed;
                    attempt.FinishedAt = DateTimeOffset.UtcNow;
                    run.StatsJson = JsonSerializer.Serialize(new
                    {
                        attempts = run.AttemptCount,
                        outcome = "fixed",
                        model = o.Model,
                    });
                    await FinishAsync(run, AutofixRunStatuses.Completed,
                        null, ct);
                    await PostCommentAsync(provider, connection, run,
                        AutofixFormatter.SuccessComment(
                            run.CheckName, run.AttemptCount,
                            attempt.HeadShaOut), ct);
                    await PostStatusAsync(connection, run, "success",
                        AutofixFormatter.StatusSuccess(run.AttemptCount), ct);
                    await RecordAuditAsync("scm.autofix_completed", run,
                        new
                        {
                            outcome = "fixed",
                            attempts = run.AttemptCount,
                            head_sha_out = attempt.HeadShaOut,
                        }, ct);
                    return;

                case AttemptPollKind.Red:
                    attempt.Outcome = AutofixAttemptOutcomes.StillFailing;
                    attempt.FinishedAt = DateTimeOffset.UtcNow;
                    // avança a máquina para o próximo check em falha.
                    run.HeadSha = poll.NewHeadSha!;
                    run.CheckName = poll.FailingCheck!.Name;
                    run.CheckRunId = poll.FailingCheck.Id;
                    run.CheckKind = "check_run";
                    run.Status = AutofixRunStatuses.Running;
                    await db.SaveChangesAsync(ct);
                    continue;

                case AttemptPollKind.Cancelled:
                    attempt.Outcome = AutofixAttemptOutcomes.Cancelled;
                    attempt.FinishedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                    return;

                case AttemptPollKind.PrClosed:
                    attempt.Outcome = AutofixAttemptOutcomes.Error;
                    attempt.FinishedAt = DateTimeOffset.UtcNow;
                    await FailWithCommentAsync(provider, connection, run,
                        "pull request was closed during autofix", ct);
                    return;

                default: // Timeout
                    attempt.Outcome = AutofixAttemptOutcomes.Error;
                    attempt.FinishedAt = DateTimeOffset.UtcNow;
                    await FailWithCommentAsync(provider, connection, run,
                        $"no check result observed within "
                        + $"{o.RunTimeoutMinutes}m", ct);
                    return;
            }
        }
    }

    /// <summary>Cria a tentativa + conversa de fix (agent_type=autofix).</summary>
    private async Task<AutofixAttemptRecord> StartAttemptAsync(
        AutofixRunRecord run, ScmConnection connection,
        string? logTail, int maxAttempts, CancellationToken ct)
    {
        var attemptNo = run.AttemptCount + 1;
        var attempt = new AutofixAttemptRecord
        {
            Id = Guid.NewGuid(),
            OrgId = run.OrgId,
            RunId = run.Id,
            Number = attemptNo,
            HeadShaIn = run.HeadSha,
            FailedChecksJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    name = run.CheckName,
                    kind = run.CheckKind,
                    id = run.CheckRunId,
                    workflow_run_id = run.WorkflowRunId,
                    conclusion = "failure",
                },
            }),
            LogDigest = logTail is null
                ? null
                : logTail[..Math.Min(2048, logTail.Length)],
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.AutofixAttempts.Add(attempt);
        run.AttemptCount = attemptNo;
        run.Status = AutofixRunStatuses.Running;
        await db.SaveChangesAsync(ct);

        var ownerUserId = await ResolveOwnerUserIdAsync(connection, ct)
            ?? throw new InvalidOperationException(
                "no resolvable owner for the fix conversation");

        var findings = options.Value.FixReviewFindings
            ? await LoadOpenFindingsAsync(run, ct)
            : null;
        var prompt = AutofixFormatter.FixPrompt(
            run.Repository, run.PrNumber, run.HeadBranch!,
            run.CheckName, run.CheckKind, attemptNo, maxAttempts,
            logTail, findings);

        var task = await conversations.CreateForUserAsync(
            ownerUserId, run.OrgId,
            new CreateConversationRequest(
                new InitialMessage("user", [prompt], Run: true),
                run.Repository,
                AgentType: AutofixDefaults.AgentType,
                LlmModel: options.Value.Model, SandboxSpecId: null),
            ct);
        attempt.ConversationId = task.AppConversationId;
        run.ConversationId = task.AppConversationId;
        await db.SaveChangesAsync(ct);

        await PostStatusAsync(
            ScmConnectionService.ToModel(
                await db.ScmConnections.IgnoreQueryFilters()
                    .FirstAsync(c => c.Id == run.ConnectionId, ct)),
            run, "pending",
            AutofixFormatter.StatusPending(attemptNo, maxAttempts), ct);

        await RecordAuditAsync("scm.autofix_attempt", run,
            new
            {
                attempt = attemptNo,
                conversation_id = task.AppConversationId,
                check = run.CheckName,
            }, ct);
        return attempt;
    }

    /// <summary>
    /// Poll do ciclo: credenciais da conexão provisionadas no sandbox da
    /// conversa (assíncrono — retentado até suceder/desistir), depois
    /// head do PR mudou → espera checks terminais no novo head → verde
    /// ou primeiro check em falha.
    /// </summary>
    private async Task<PollResult> PollAttemptAsync(
        IScmProvider provider, ScmConnection connection,
        AutofixRunRecord run, AutofixAttemptRecord attempt,
        AutofixOptions o, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow
            + TimeSpan.FromMinutes(o.RunTimeoutMinutes);
        var interval = TimeSpan.FromSeconds(Math.Max(1, o.PollSeconds));
        var provisioned = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsCancelledAsync(run.Id, ct))
            {
                return new PollResult(AttemptPollKind.Cancelled, null, null);
            }
            if (!provisioned)
            {
                provisioned = await TryProvisionAsync(run, connection, ct);
            }

            ScmPullRequest? pr = null;
            try
            {
                pr = await provider.GetPullRequestAsync(
                    connection, run.Repository, run.PrNumber, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogDebug(e,
                    "pr fetch failed during autofix poll (run {Id})", run.Id);
            }

            if (pr is not null)
            {
                if (!string.Equals(pr.State, "open",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return new PollResult(
                        AttemptPollKind.PrClosed, null, null);
                }
                if (!string.Equals(pr.HeadSha, attempt.HeadShaIn,
                        StringComparison.OrdinalIgnoreCase))
                {
                    attempt.HeadShaOut = pr.HeadSha;
                    await db.SaveChangesAsync(ct);

                    var checks = await GetChecksSafeAsync(
                        provider, connection, run.Repository, pr.HeadSha, ct);
                    var relevant = checks.Where(c =>
                        !c.Name.StartsWith(AutofixDefaults.OwnContextPrefix,
                            StringComparison.OrdinalIgnoreCase)).ToList();
                    if (relevant.Count > 0
                        && relevant.All(c => TerminalStatuses.Contains(c.Status)))
                    {
                        var failing = relevant.FirstOrDefault(c =>
                            CiCheckEventParser.FailureConclusions
                                .Contains(c.Conclusion ?? ""));
                        return failing is null
                            ? new PollResult(AttemptPollKind.Green, null, null)
                            : new PollResult(AttemptPollKind.Red,
                                pr.HeadSha, failing);
                    }
                }
            }
            await Task.Delay(interval, ct);
        }
        return new PollResult(AttemptPollKind.Timeout, null, null);
    }

    /// <summary>
    /// Provisiona o installation token da conexão no sandbox da conversa
    /// de fix (push na branch do PR usa a credencial da CONEXÃO — NFR).
    /// Retenta enquanto o sandbox não existe; desiste sem erro fatal
    /// (o agente ainda pode usar a credencial do dono da conversa).
    /// </summary>
    private async Task<bool> TryProvisionAsync(
        AutofixRunRecord run, ScmConnection connection,
        CancellationToken ct)
    {
        if (run.ConversationId is null || connection.Kind != ScmKind.GitHub)
        {
            return true;
        }
        try
        {
            var sandboxId = await db.Conversations.IgnoreQueryFilters()
                .Where(c => c.Id == run.ConversationId)
                .Select(c => c.SandboxId).FirstOrDefaultAsync(ct);
            if (sandboxId is not { } sid)
            {
                return false; // sandbox ainda não criado — retry no próximo ciclo.
            }
            var sandbox = await sandboxes.GetAsync(sid, ct);
            if (sandbox is null)
            {
                return true;
            }
            var token = await githubClients.GetInstallationTokenAsync(
                connection, ct);
            await git.ProvisionTokenAuthAsync(
                sandbox, "github.com", token, ct);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogDebug(e,
                "autofix credential provision skipped (run {Id})", run.Id);
            return true;
        }
    }

    /// <summary>
    /// Log da falha por kind: check_run → output do check; workflow_run →
    /// zip de logs do Actions; check_suite/manual → primeiro check run
    /// em falha do head; status → apenas a descrição/url (sem API de log).
    /// Bounded por <c>Autofix:MaxLogKb</c> + <see cref="SecretRedactor"/>.
    /// </summary>
    private async Task<string?> FetchLogTailAsync(
        IScmProvider provider, ScmConnection connection,
        AutofixRunRecord run, AutofixOptions o, CancellationToken ct)
    {
        string? raw = null;
        try
        {
            switch (run.CheckKind)
            {
                case "check_run" when run.CheckRunId is { } checkId:
                    raw = await provider.GetCheckRunLogsAsync(
                        connection, run.Repository, checkId, ct);
                    break;
                case "workflow_run" when run.WorkflowRunId is { } wfId:
                    raw = await provider.GetWorkflowRunLogsAsync(
                        connection, run.Repository, wfId, ct);
                    break;
                case "check_suite" or "manual":
                    var failing = (await GetChecksSafeAsync(
                            provider, connection, run.Repository,
                            run.HeadSha, ct))
                        .FirstOrDefault(c =>
                            CiCheckEventParser.FailureConclusions
                                .Contains(c.Conclusion ?? ""));
                    if (failing is not null)
                    {
                        run.CheckName = failing.Name;
                        run.CheckRunId = failing.Id;
                        raw = await provider.GetCheckRunLogsAsync(
                            connection, run.Repository, failing.Id, ct);
                    }
                    break;
            }
        }
        catch (NotSupportedException)
        {
            throw; // → blocked no caller (provider sem CI inspection).
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogDebug(e,
                "ci log fetch failed (run {Id} {Check})", run.Id,
                run.CheckName);
        }

        var text = raw ?? run.CheckUrl;
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        var bound = o.MaxLogKb * 1024;
        var redacted = SecretRedactor.Redact(text);
        return redacted.Length <= bound ? redacted : redacted[^bound..];
    }

    private async Task<IReadOnlyList<ScmCheckRun>> GetChecksSafeAsync(
        IScmProvider provider, ScmConnection connection,
        string repository, string sha, CancellationToken ct)
    {
        try
        {
            return await provider.GetCheckRunsAsync(
                connection, repository, sha, ct);
        }
        catch (NotSupportedException)
        {
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogDebug(e, "check runs fetch failed ({Repo} {Sha})",
                repository, sha[..Math.Min(8, sha.Length)]);
            return [];
        }
    }

    private async Task<string?> ResolveHeadBranchAsync(
        IScmProvider provider, ScmConnection connection,
        AutofixRunRecord run, CancellationToken ct)
    {
        try
        {
            var pulls = await provider.GetPullRequestsForCommitAsync(
                connection, run.Repository, run.HeadSha, ct);
            var open = pulls.FirstOrDefault(p =>
                p.Number == run.PrNumber
                || string.Equals(p.State, "open",
                    StringComparison.OrdinalIgnoreCase));
            return open?.HeadRef;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogDebug(e, "head branch resolve failed (run {Id})", run.Id);
            return null;
        }
    }

    /// <summary>Findings bug|security abertos do PR (RF-004 opcional).</summary>
    private async Task<IReadOnlyList<string>?> LoadOpenFindingsAsync(
        AutofixRunRecord run, CancellationToken ct)
    {
        var findings = await db.ReviewFindings.IgnoreQueryFilters()
            .Where(f => f.Repository == run.Repository
                && f.PrNumber == run.PrNumber
                && f.Status == ReviewFindingStatuses.Open
                && (f.Kind == ReviewFindingKinds.Bug
                    || f.Kind == ReviewFindingKinds.Security))
            .Take(20).ToListAsync(ct);
        return findings.Count == 0
            ? null
            : findings.Select(f =>
                $"{f.Kind}:{f.Severity} {f.Path}"
                + (f.Line is { } l ? $":{l}" : "")
                + $" — {SecretRedactor.Redact(f.Body)[..Math.Min(200, f.Body.Length)]}")
              .ToList();
    }

    private async Task<Guid?> ResolveOwnerUserIdAsync(
        ScmConnection connection, CancellationToken ct)
    {
        if (connection.CreatedByUserId is not null
            && Guid.TryParse(connection.CreatedByUserId, out var createdBy))
        {
            return createdBy;
        }
        if (connection.OrgId is { } orgId)
        {
            var owner = await db.OrgMembers.IgnoreQueryFilters()
                .Where(m => m.OrgId == orgId && m.Role == "owner")
                .Select(m => m.UserId)
                .FirstOrDefaultAsync(ct);
            if (owner != Guid.Empty)
            {
                return owner;
            }
        }
        return null;
    }

    private async Task GiveUpAsync(
        IScmProvider provider, ScmConnection connection,
        AutofixRunRecord run, int maxAttempts, CancellationToken ct)
    {
        var triedShas = await db.AutofixAttempts.IgnoreQueryFilters()
            .Where(a => a.RunId == run.Id && a.HeadShaOut != null)
            .Select(a => a.HeadShaOut!)
            .ToListAsync(ct);
        await FinishAsync(run, AutofixRunStatuses.Failed,
            $"attempt cap reached ({maxAttempts})", ct);
        await PostCommentAsync(provider, connection, run,
            AutofixFormatter.GiveUpComment(
                run.CheckName, maxAttempts, run.Error, triedShas), ct);
        await PostStatusAsync(connection, run, "failure",
            AutofixFormatter.StatusFailure(maxAttempts), ct);
        await RecordAuditAsync("scm.autofix_failed", run,
            new { outcome = "gave_up", attempts = run.AttemptCount }, ct);
    }

    private async Task BlockedAsync(
        ScmConnection connection, AutofixRunRecord run,
        string reason, CancellationToken ct)
    {
        IScmProvider? provider = null;
        try { provider = providers.Get(connection.Kind); }
        catch (Exception) { /* melhor esforço */ }
        await FinishAsync(run, AutofixRunStatuses.Blocked, reason, ct);
        if (provider is not null)
        {
            await PostCommentAsync(provider, connection, run,
                AutofixFormatter.BlockedComment(run.CheckName, reason), ct);
        }
        await PostStatusAsync(connection, run, "error",
            AutofixFormatter.StatusBlocked(reason), ct);
        await RecordAuditAsync("scm.autofix_failed", run,
            new { outcome = "blocked", reason }, ct);
    }

    private async Task FailWithCommentAsync(
        IScmProvider provider, ScmConnection connection,
        AutofixRunRecord run, string detail, CancellationToken ct)
    {
        await FinishAsync(run, AutofixRunStatuses.Failed, detail, ct);
        await PostCommentAsync(provider, connection, run,
            AutofixFormatter.ErrorComment(run.CheckName, detail), ct);
        await PostStatusAsync(connection, run, "error",
            $"Autofix stopped — {detail} (advisory)", ct);
        await RecordAuditAsync("scm.autofix_failed", run,
            new { outcome = "error", detail }, ct);
    }

    /// <summary>Run cancelado via API? (releitura curta — poll usa isto).</summary>
    private async Task<bool> IsCancelledAsync(Guid runId, CancellationToken ct) =>
        await db.AutofixRuns.IgnoreQueryFilters()
            .Where(r => r.Id == runId)
            .Select(r => r.Status)
            .FirstOrDefaultAsync(ct) == AutofixRunStatuses.Cancelled;

    /// <summary>Comentário no PR — melhor esforço (nunca lança).</summary>
    private async Task PostCommentAsync(
        IScmProvider provider, ScmConnection connection,
        AutofixRunRecord run, string body, CancellationToken ct)
    {
        try
        {
            await provider.CreateIssueCommentAsync(
                connection, run.Repository, run.PrNumber, body, ct);
        }
        catch (Exception e)
        {
            logger.LogDebug(e,
                "autofix comment post skipped (run {Id})", run.Id);
        }
    }

    /// <summary>Commit status <c>sandcastle/autofix</c> — melhor esforço.</summary>
    private async Task PostStatusAsync(
        ScmConnection connection, AutofixRunRecord run, string state,
        string description, CancellationToken ct)
    {
        try
        {
            var provider = providers.Get(connection.Kind);
            if (provider is null)
            {
                return;
            }
            await provider.SetCommitStatusAsync(
                connection, run.Repository, run.HeadSha, state,
                AutofixDefaults.StatusContext,
                targetUrl: null,
                description[..Math.Min(140, description.Length)], ct);
        }
        catch (Exception e)
        {
            logger.LogDebug(e,
                "autofix commit status post skipped (run {Id})", run.Id);
        }
    }

    private async Task RecordAuditAsync(
        string action, AutofixRunRecord run, object metadata,
        CancellationToken ct) =>
        await audit.RecordAsync(action,
            actorUserId: run.RequestedByUserId, orgId: run.OrgId,
            targetType: "autofix_run", targetId: run.Id.ToString(),
            metadata: metadata is string
                ? metadata
                : new
                {
                    repository = run.Repository,
                    pr = run.PrNumber,
                    head_sha = run.HeadSha,
                    stats = metadata,
                },
            cancellationToken: ct);

    private async Task FinishAsync(
        AutofixRunRecord run, string status, string? error,
        CancellationToken ct)
    {
        run.Status = status;
        run.Error = error;
        run.FinishedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private enum AttemptPollKind { Green, Red, Timeout, PrClosed, Cancelled }

    private sealed record PollResult(
        AttemptPollKind Kind, string? NewHeadSha, ScmCheckRun? FailingCheck);
}