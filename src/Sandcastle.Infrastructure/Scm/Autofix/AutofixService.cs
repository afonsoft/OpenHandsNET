using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Scm;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Scm.Autofix;

/// <summary>
/// <see cref="IAutofixService"/> (SPEC-141 RF-001/RF-002/RF-004):
/// trigger por evento de CI com política (conclusão de falha → escopo →
/// check-name glob → anti-loop por app/ator/head_sha_out) + resolução
/// de PR (payload → commits/{sha}/pulls →
/// conversations.PullRequestHeadSha), dedup (run ativo por PR + mesmo
/// (head,check) qualquer status), quota diária, listagem, detalhe e
/// cancel.
/// </summary>
public sealed class AutofixService(
    AppDbContext db,
    IAutofixDispatcher dispatcher,
    IScmProviderRegistry providers,
    IOptions<AutofixOptions> options,
    ILogger<AutofixService> logger) : IAutofixService
{
    /// <inheritdoc />
    public async Task<AutofixTriggerResult> TriggerCiEventAsync(
        ScmConnection connection, CiCheckEvent ev, CancellationToken ct)
    {
        var o = options.Value;

        // 1) só falhas.
        if (!CiCheckEventParser.FailureConclusions.Contains(ev.Conclusion))
        {
            return AutofixTriggerResult.Fail(
                AutofixTriggerError.Ignored, "not a failure conclusion");
        }

        // 2) anti-loop: checks do próprio app/contexto sandcastle e
        //    atores bot (workflow_run.actor) nunca disparam.
        if (IsOwnCheck(connection, ev, o))
        {
            return AutofixTriggerResult.Fail(
                AutofixTriggerError.Ignored, "own bot check/actor");
        }

        // 3) gate de check-name (conexão → global).
        if (!connection.AutofixAllowsCheck(ev.CheckName, o.CheckNamePatterns))
        {
            return AutofixTriggerResult.Fail(
                AutofixTriggerError.Ignored, "check name out of scope");
        }

        // 4) resolução do PR: payload → commits/{sha}/pulls →
        //    conversations.PullRequestHeadSha (PR criado pelo Sandcastle).
        var (pr, viaConversation) = await ResolvePullRequestAsync(
            connection, ev, ct);
        if (pr is null)
        {
            return AutofixTriggerResult.Fail(
                AutofixTriggerError.Ignored, "no open pr for head sha");
        }

        // 5) escopo: repo inscrito OU PR criado por conversa Sandcastle.
        if (!connection.AutofixCovers(ev.Repository) && !viaConversation)
        {
            return AutofixTriggerResult.Fail(
                AutofixTriggerError.Disabled,
                "autofix not enabled for this repository");
        }

        // 6) PR de fork (head repo != base repo) — push na branch não é
        //    possível com a credencial da conexão → não-autofixável.
        if (pr.Value.HeadRepo is { } headRepo
            && !string.Equals(headRepo, ev.Repository,
                StringComparison.OrdinalIgnoreCase))
        {
            return AutofixTriggerResult.Fail(
                AutofixTriggerError.Ignored, "fork pr head not writable");
        }

        // 7) anti-loop por push próprio: head_sha já foi produzido por
        //    um fix nosso neste PR → o check dele é observado pelo run
        //    ativo (poll interno), não por um novo run.
        var ownPush = await db.AutofixAttempts.IgnoreQueryFilters()
            .Where(a => a.HeadShaOut == ev.HeadSha)
            .Join(db.AutofixRuns.IgnoreQueryFilters(),
                a => a.RunId, r => r.Id, (a, r) => r)
            .AnyAsync(r => r.Repository == ev.Repository
                && r.PrNumber == pr.Value.Number, ct);
        if (ownPush)
        {
            return AutofixTriggerResult.Fail(
                AutofixTriggerError.Ignored, "head sha pushed by autofix");
        }

        return await EnqueueAsync(connection, ev.Repository,
            pr.Value.Number, ev.HeadSha, pr.Value.Branch, ev.CheckName,
            ev.Kind, ev.CheckRunId, ev.WorkflowRunId, ev.CheckUrl,
            "webhook", null, ct);
    }

    /// <inheritdoc />
    public async Task<AutofixTriggerResult> TriggerManualAsync(
        ScmConnection connection, string repository, int prNumber,
        string? requestedByUserId, CancellationToken ct)
    {
        if (!connection.AutofixCovers(repository))
        {
            return AutofixTriggerResult.Fail(
                AutofixTriggerError.Disabled,
                "autofix not enabled for this repository");
        }
        var provider = providers.Get(connection.Kind);
        if (provider is null)
        {
            return AutofixTriggerResult.Fail(
                AutofixTriggerError.NoConnection, "provider not registered");
        }

        ScmPullRequest? pr;
        try
        {
            pr = await provider.GetPullRequestAsync(
                connection, repository, prNumber, ct);
        }
        catch (Exception)
        {
            pr = null;
        }
        IReadOnlyList<ScmCheckRun> failing = [];
        if (pr is not null)
        {
            try
            {
                failing = (await provider.GetCheckRunsAsync(
                    connection, repository, pr.HeadSha, ct))
                    .Where(c => IsFailureConclusion(c.Conclusion))
                    .ToList();
            }
            catch (NotSupportedException)
            {
                // provider sem Checks API — segue com dados do PR.
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogDebug(e,
                    "check runs fetch failed ({Repo}#{Pr})", repository,
                    prNumber);
            }
        }
        if (pr is null || !string.Equals(
                pr.State, "open", StringComparison.OrdinalIgnoreCase))
        {
            return AutofixTriggerResult.Fail(
                AutofixTriggerError.NotFound, "pull request not found");
        }

        var failingCheck = failing.FirstOrDefault();
        return await EnqueueAsync(connection, repository, prNumber,
            pr.HeadSha, headBranch: null, // resolvido no runner via PR
            failingCheck?.Name ?? "manual",
            failingCheck is null ? "manual" : "check_run",
            failingCheck?.Id, null, failingCheck?.DetailsUrl,
            "manual", requestedByUserId, ct);
    }

    /// <summary>Dedup + quota + persist + enqueue — parte comum.</summary>
    private async Task<AutofixTriggerResult> EnqueueAsync(
        ScmConnection connection, string repository, int prNumber,
        string headSha, string? headBranch, string checkName,
        string checkKind, long? checkRunId, long? workflowRunId,
        string? checkUrl, string trigger, string? requestedByUserId,
        CancellationToken ct)
    {
        // Concorrência: um run ativo por (repo, pr) — o loop interno do
        // runner já observa os próximos checks da head chain.
        var active = await db.AutofixRuns.IgnoreQueryFilters()
            .AnyAsync(r => r.Repository == repository
                && r.PrNumber == prNumber
                && AutofixRunStatuses.Active.Contains(r.Status), ct);
        if (active)
        {
            return AutofixTriggerResult.Fail(
                AutofixTriggerError.Duplicate,
                "an autofix run is already active for this pr");
        }

        // Mesmo (repo, pr, head_sha, check_name) já teve run — re-disparo
        // do mesmo check no mesmo head nunca gera segundo run.
        var twin = await db.AutofixRuns.IgnoreQueryFilters()
            .AnyAsync(r => r.Repository == repository
                && r.PrNumber == prNumber
                && r.HeadSha == headSha
                && r.CheckName == checkName, ct);
        if (twin)
        {
            return AutofixTriggerResult.Fail(
                AutofixTriggerError.Duplicate,
                "a run already covers this head+check");
        }

        // Quota diária por repo.
        var dayStartTicks = new DateTimeOffset(
            DateTime.UtcNow.Date, TimeSpan.Zero).UtcTicks;
        var runsToday = await db.AutofixRuns.IgnoreQueryFilters()
            .CountAsync(r => r.Repository == repository
                && r.CreatedAtTicks >= dayStartTicks, ct);
        if (runsToday >= options.Value.MaxDailyRunsPerRepo)
        {
            return AutofixTriggerResult.Fail(
                AutofixTriggerError.Disabled,
                $"daily autofix quota reached ({runsToday}/"
                + $"{options.Value.MaxDailyRunsPerRepo})");
        }

        var run = new AutofixRunRecord
        {
            Id = Guid.NewGuid(),
            OrgId = connection.OrgId,
            ConnectionId = connection.Id,
            Repository = repository,
            PrNumber = prNumber,
            HeadSha = headSha,
            HeadBranch = headBranch,
            CheckName = checkName,
            CheckKind = checkKind,
            CheckRunId = checkRunId,
            WorkflowRunId = workflowRunId,
            CheckUrl = checkUrl,
            Status = AutofixRunStatuses.Queued,
            Trigger = trigger,
            RequestedByUserId = requestedByUserId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.AutofixRuns.Add(run);
        await db.SaveChangesAsync(ct);
        await dispatcher.EnqueueAsync(run.Id, ct);
        logger.LogInformation(
            "autofix run {Id} queued ({Repo}#{Pr} {Sha} {Check})",
            run.Id, repository, prNumber,
            headSha[..Math.Min(8, headSha.Length)], checkName);
        return AutofixTriggerResult.Ok(run.Id);
    }

    /// <summary>
    /// Resolução sha → PR: número no payload →
    /// <c>GET /commits/{sha}/pulls</c> (open) →
    /// <c>conversations.PullRequestHeadSha</c>. Retorna
    /// (number, branch?, head_repo?) + flag "via conversation".
    /// </summary>
    private async Task<((int Number, string? Branch, string? HeadRepo)?
        Pr, bool ViaConversation)> ResolvePullRequestAsync(
        ScmConnection connection, CiCheckEvent ev, CancellationToken ct)
    {
        if (ev.PrNumber is { } n && n > 0)
        {
            return ((n, ev.HeadBranch, ev.HeadRepoFullName), false);
        }

        var provider = providers.Get(connection.Kind);
        if (provider is not null)
        {
            try
            {
                var pulls = await provider.GetPullRequestsForCommitAsync(
                    connection, ev.Repository, ev.HeadSha, ct);
                var open = pulls.FirstOrDefault(p =>
                    string.Equals(p.State, "open",
                        StringComparison.OrdinalIgnoreCase));
                if (open is not null)
                {
                    return ((open.Number,
                        ev.HeadBranch ?? open.HeadRef,
                        open.HeadRepoFullName), false);
                }
            }
            catch (NotSupportedException)
            {
                // provider sem commits/pulls — segue p/ fallback de conversa.
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogDebug(e,
                    "commit→pulls lookup failed ({Repo} {Sha})",
                    ev.Repository, ev.HeadSha[..Math.Min(8, ev.HeadSha.Length)]);
            }
        }

        var conv = await db.Conversations.IgnoreQueryFilters()
            .Where(c => c.SelectedRepository == ev.Repository
                && c.PullRequestHeadSha == ev.HeadSha
                && c.PullRequestNumber != null)
            .OrderByDescending(c => c.UpdatedAtTicks)
            .Select(c => new { c.PullRequestNumber })
            .FirstOrDefaultAsync(ct);
        if (conv?.PullRequestNumber is { } convPr)
        {
            return ((convPr, ev.HeadBranch, null), true);
        }
        return (null, false);
    }

    /// <summary>
    /// Anti-loop (RF-002): check do próprio app (<c>app.slug</c> da
    /// conexão), contexto <c>sandcastle/*</c> ou ator bot
    /// (<c>Autofix:BotAuthors</c> + <c>{AppSlug}[bot]</c>) — nunca dispara.
    /// </summary>
    private static bool IsOwnCheck(
        ScmConnection connection, CiCheckEvent ev, AutofixOptions o)
    {
        if (ev.CheckName.StartsWith(
                AutofixDefaults.OwnContextPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (ev.AppSlug is not null && connection.AppSlug is not null
            && string.Equals(ev.AppSlug, connection.AppSlug,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (ev.ActorLogin is { } actor
            && (o.BotAuthors.Any(b => string.Equals(
                    b, actor, StringComparison.OrdinalIgnoreCase))
                || (connection.AppSlug is { } slug && string.Equals(
                    actor, $"{slug}[bot]", StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }
        return false;
    }

    /// <summary>Conclusão terminal de falha (checks + statuses).</summary>
    private static bool IsFailureConclusion(string? conclusion) =>
        conclusion is not null
            && CiCheckEventParser.FailureConclusions.Contains(conclusion);

    /// <inheritdoc />
    public async Task<AutofixRunPage> SearchAsync(
        Guid orgId, string? repository, int? prNumber, string? status,
        string? cursor, int limit, CancellationToken ct)
    {
        var q = db.AutofixRuns.IgnoreQueryFilters()
            .Where(r => r.OrgId == orgId);
        if (!string.IsNullOrEmpty(repository))
        {
            q = q.Where(r => r.Repository == repository);
        }
        if (prNumber is { } pr)
        {
            q = q.Where(r => r.PrNumber == pr);
        }
        if (!string.IsNullOrEmpty(status))
        {
            q = q.Where(r => r.Status == status);
        }
        if (CursorPaginator.TryDecode(cursor, out var ticks, out var raw)
            && Guid.TryParse(raw, out _))
        {
            // Sqlite persiste Guid como TEXT uppercase; normalizar os dois
            // lados via LOWER() mantém o tie-break consistente com o
            // ORDER BY id (#424).
            var id = raw.ToLowerInvariant();
            q = q.Where(r => r.CreatedAtTicks < ticks
                || (r.CreatedAtTicks == ticks
                    && string.Compare(r.Id.ToString().ToLower(), id) < 0));
        }

        var pageSize = Math.Clamp(limit, 1, 100);
        var rows = await q
            .OrderByDescending(r => r.CreatedAtTicks)
            .ThenByDescending(r => r.Id)
            .Take(pageSize + 1)
            .ToListAsync(ct);

        string? next = null;
        if (rows.Count > pageSize)
        {
            rows.RemoveAt(rows.Count - 1);
            var last = rows[^1];
            next = CursorPaginator.Encode(
                last.CreatedAtTicks, last.Id.ToString());
        }
        return new AutofixRunPage(
            rows.Select(ToModel).ToList(), next);
    }

    /// <inheritdoc />
    public async Task<AutofixRunDetail?> GetAsync(
        Guid orgId, Guid id, CancellationToken ct)
    {
        var run = await db.AutofixRuns.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == id && r.OrgId == orgId, ct);
        if (run is null)
        {
            return null;
        }
        var attempts = await db.AutofixAttempts.IgnoreQueryFilters()
            .Where(a => a.RunId == id)
            .ToListAsync(ct);
        attempts.Sort((a, b) => a.Number.CompareTo(b.Number));
        return new AutofixRunDetail(
            ToModel(run), attempts.Select(ToModel).ToList());
    }

    /// <inheritdoc />
    public async Task<bool> CancelAsync(
        Guid orgId, Guid id, CancellationToken ct)
    {
        var run = await db.AutofixRuns.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == id && r.OrgId == orgId, ct);
        if (run is null
            || !AutofixRunStatuses.Active.Contains(run.Status))
        {
            return false;
        }
        run.Status = AutofixRunStatuses.Cancelled;
        run.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static AutofixRun ToModel(AutofixRunRecord r) => new(
        r.Id, r.OrgId, r.ConnectionId, r.Repository, r.PrNumber,
        r.HeadSha, r.HeadBranch, r.CheckName, r.CheckKind, r.Status,
        r.AttemptCount, r.ConversationId, r.Trigger, r.RequestedByUserId,
        r.Error, r.CreatedAt, r.FinishedAt);

    private static AutofixAttempt ToModel(AutofixAttemptRecord a) => new(
        a.Id, a.RunId, a.Number, a.HeadShaIn, a.HeadShaOut,
        a.FailedChecksJson, a.Outcome, a.LogDigest, a.ConversationId,
        a.CreatedAt, a.FinishedAt);
}