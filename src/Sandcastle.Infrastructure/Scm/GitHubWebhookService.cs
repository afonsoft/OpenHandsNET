using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Sandcastle.Application.Contracts;
using Sandcastle.Application.Conversations;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Indexing;
using Sandcastle.Application.Scm;
using Sandcastle.Application.Wiki;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Security;

namespace Sandcastle.Infrastructure.Scm;

/// <summary>
/// GitHub webhook pipeline (SPEC-52 RF-004): HMAC-SHA256
/// (<c>X-Hub-Signature-256</c>) → idempotency by <c>X-GitHub-Delivery</c> →
/// mention routing (<c>@sandcastle</c> → start-task). O payload nunca é
/// executado — só extraído como contexto da mensagem inicial.
/// </summary>
public sealed class GitHubWebhookService(
    AppDbContext db,
    ISecretStore secrets,
    Sandcastle.Application.Conversations.IAppConversationService conversations,
    IAuditService audit,
    ILogger<GitHubWebhookService> logger,
    IReviewService? reviews = null,
    Microsoft.Extensions.Options.IOptions<Review.ReviewOptions>? reviewOptions = null,
    IAutofixService? autofix = null,
    Microsoft.Extensions.Options.IOptions<Autofix.AutofixOptions>? autofixOptions = null,
    IQaService? qa = null,
    Microsoft.Extensions.Options.IOptions<Qa.QaOptions>? qaOptions = null,
    IRepoIndexer? indexer = null,
    Microsoft.Extensions.Options.IOptions<Indexing.IndexingOptions>? indexingOptions = null,
    IWikiService? wiki = null) : IScmWebhookService
{
    private const string Repository = "repository";
    private const string Comment = "comment";
    private const string ActionProp = "action";
    private const string NumberProp = "number";
    private const string WebhookTrigger = "webhook";
    /// <summary>Mention marker that triggers a conversation.</summary>
    public const string Mention = "@sandcastle";

    /// <inheritdoc />
    public async Task<ScmWebhookResult> HandleGitHubAsync(
        string? signature256, string eventType, string? deliveryId,
        byte[] payload, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return ScmWebhookResult.InvalidSignature;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var installationId = root.TryGetProperty("installation", out var inst)
                && inst.TryGetProperty("id", out var iid)
                    ? iid.GetRawText() : null;
            var repository = root.TryGetProperty(Repository, out var repo)
                && repo.TryGetProperty("full_name", out var fn)
                    ? fn.GetString() : null;

            var connection = await FindConnectionAsync(
                ScmKind.GitHub, installationId, repository, ct);
            if (connection is null)
            {
                return ScmWebhookResult.NoConnection;
            }

            if (!await VerifySignatureAsync(
                    connection, signature256, payload, ct))
            {
                await audit.RecordAsync("scm.webhook_rejected", orgId: null,
                    targetType: "scm_connection", targetId: connection.Id.ToString(),
                    metadata: new { reason = "bad_signature", eventType },
                    cancellationToken: ct);
                return ScmWebhookResult.InvalidSignature;
            }

            if (await IsDuplicateAsync(
                    ScmKind.GitHub, deliveryId, payload, ct))
            {
                return ScmWebhookResult.Duplicate;
            }

            // SPEC-140 RF-001: pull_request events → auto-review pipeline
            // (enrollment + trigger rules + dedup), independente de menção.
            // SPEC-142 RF-001: o mesmo evento também alimenta o QA agent
            // (opened|synchronize, opt-in por conexão) — ambos rodam.
            if (eventType == "pull_request")
            {
                return await RoutePullRequestAsync(root, connection, ct);
            }

            // SPEC-141 RF-001: check_run|check_suite|workflow_run|status
            // com conclusão de falha → CI autofix pipeline (normaliza o
            // evento, resolve o PR, aplica política e enfileira o run).
            if (eventType is "check_run" or "check_suite"
                or "workflow_run" or "status")
            {
                return await HandleCiEventAsync(
                    eventType, root, connection, ct);
            }

            // SPEC-145 RF-001: push muda head da branch → re-index
            // (AutoRefreshOnPush) + stale-mark das páginas de wiki.
            if (eventType == "push")
            {
                return await HandlePushEventAsync(
                    ScmKind.GitHub, root, connection, repository, ct);
            }

            var mention = ParseMention(eventType, root, installationId);
            if (mention is null || IsOwnBot(mention.CommentAuthor, connection))
            {
                return ScmWebhookResult.Ignored;
            }

            var ownerUserId = await ResolveOwnerUserIdAsync(connection, ct);
            if (ownerUserId is null)
            {
                logger.LogWarning(
                    "scm mention without resolvable owner (conn {Id})",
                    connection.Id);
                return ScmWebhookResult.Ignored;
            }

            var prompt = BuildPrompt(mention);
            var task = await conversations.CreateForUserAsync(
                ownerUserId.Value, connection.OrgId,
                new CreateConversationRequest(
                    new InitialMessage("user", [prompt], Run: true),
                    mention.Repository,
                    AgentType: null, LlmModel: null, SandboxSpecId: null),
                ct);

            await audit.RecordAsync("scm.webhook_triggered",
                actorUserId: ownerUserId.Value.ToString(),
                orgId: connection.OrgId,
                targetType: "app_conversation",
                targetId: task.AppConversationId?.ToString(),
                metadata: new
                {
                    event_type = eventType,
                    repository = mention.Repository,
                    pr = mention.PullRequestNumber,
                },
                cancellationToken: ct);
            return ScmWebhookResult.Processed;
        }
    }

    /// <summary>
    /// Despacho de <c>pull_request</c> (GitHub): review + QA rodam em
    /// paralelo lógico — <see cref="ScmWebhookResult.Processed"/> se
    /// qualquer um enfileirar.
    /// </summary>
    private async Task<ScmWebhookResult> RoutePullRequestAsync(
        JsonElement root, ScmConnection connection, CancellationToken ct)
    {
        var reviewResult = await HandlePullRequestEventAsync(
            root, connection, ct);
        var qaResult = await HandleQaPullRequestAsync(
            root, connection, ct);
        return reviewResult == ScmWebhookResult.Processed
            || qaResult == ScmWebhookResult.Processed
                ? ScmWebhookResult.Processed
                : ScmWebhookResult.Ignored;
    }

    /// <summary>
    /// SPEC-140 RF-001/RF-005 — roteamento de <c>pull_request</c>:
    /// actions disparadoras {opened, synchronize, ready_for_review,
    /// labeled, unlabeled, review_requested} → enrollment
    /// (<see cref="ScmConnection.ReviewCovers"/>) → regras de disparo
    /// (não-draft, modo label_only, reviewer request do nosso bot) →
    /// <see cref="IReviewService.TriggerAsync"/> (dedup por head).
    /// </summary>
    private async Task<ScmWebhookResult> HandlePullRequestEventAsync(
        JsonElement root, ScmConnection connection, CancellationToken ct)
    {
        if (reviews is null || reviewOptions?.Value.Enabled is false)
        {
            return ScmWebhookResult.Ignored;
        }
        if (!root.TryGetProperty("pull_request", out var pr)
            || !root.TryGetProperty(ActionProp, out var act))
        {
            return ScmWebhookResult.Ignored;
        }
        var action = act.GetString();
        var repository = root.TryGetProperty(Repository, out var repo)
            && repo.TryGetProperty("full_name", out var fn)
                ? fn.GetString() : null;
        if (repository is null || !connection.ReviewCovers(repository))
        {
            return ScmWebhookResult.Ignored;
        }

        var draft = pr.TryGetProperty("draft", out var d)
            && d.GetBoolean();
        var labels = LabelsOf(pr);
        var mode = connection.EffectiveTriggerMode;
        var botLogin = BotLogin(connection);

        var fire = action switch
        {
            "opened" or "synchronize" or "ready_for_review" or "reopened"
                => !draft && (mode != ReviewDefaults.TriggerModeLabelOnly
                    || labels.Contains(ReviewDefaults.GateLabel)),
            "labeled"
                => !draft && (mode != ReviewDefaults.TriggerModeLabelOnly
                    || LabelOf(root) == ReviewDefaults.GateLabel
                    || labels.Contains(ReviewDefaults.GateLabel)),
            "unlabeled" => false,
            "review_requested"
                => !draft && botLogin is not null
                    && RequestedReviewerOf(root) is { } rr
                    && string.Equals(rr, botLogin,
                        StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        if (!fire)
        {
            return ScmWebhookResult.Ignored;
        }

        var prNumber = pr.TryGetProperty(NumberProp, out var n)
            ? n.GetInt32() : 0;
        var headSha = pr.TryGetProperty("head", out var h)
            && h.TryGetProperty("sha", out var hs)
                ? hs.GetString() : null;
        if (prNumber <= 0 || string.IsNullOrEmpty(headSha))
        {
            return ScmWebhookResult.Ignored;
        }

        var trigger = action == "review_requested"
            ? "reviewer_request" : WebhookTrigger;
        var result = await reviews.TriggerAsync(
            connection, repository, prNumber, headSha, trigger, null, ct);
        if (result.Error is not null and not ReviewTriggerError.Duplicate)
        {
            logger.LogWarning(
                "review trigger failed ({Repo}#{Pr}): {Err} {Detail}",
                repository, prNumber, result.Error, result.Detail);
            return ScmWebhookResult.Ignored;
        }
        if (result.ReviewRunId is { } runId)
        {
            await audit.RecordAsync("scm.review_triggered",
                orgId: connection.OrgId,
                targetType: "review_run", targetId: runId.ToString(),
                metadata: new
                {
                    repository,
                    pr = prNumber,
                    head_sha = headSha,
                    action,
                },
                cancellationToken: ct);
        }
        return result.ReviewRunId is null
            ? ScmWebhookResult.Ignored
            : ScmWebhookResult.Processed;
    }

    /// <summary>
    /// SPEC-142 RF-001 — roteamento QA de <c>pull_request</c>: actions
    /// <c>opened</c>/<c>synchronize</c> + enrollment
    /// (<see cref="ScmConnection.QaCovers"/>) →
    /// <see cref="IQaService.TriggerAsync"/> (dedup por
    /// <c>(repo, pr, head_sha)</c>). Anti-loop é estrutural: QA só
    /// comenta e publica status — nunca dispara opened/synchronize.
    /// </summary>
    private async Task<ScmWebhookResult> HandleQaPullRequestAsync(
        JsonElement root, ScmConnection connection, CancellationToken ct)
    {
        if (!TryReadQaPullRequest(root, connection, out var repository,
                out var prNumber, out var headSha, out var baseSha,
                out var action))
        {
            return ScmWebhookResult.Ignored;
        }

        var result = await qa!.TriggerAsync(
            connection, repository, prNumber, headSha, baseSha,
            WebhookTrigger, null, ct);
        if (result.Error is not null and not QaTriggerError.Duplicate)
        {
            logger.LogWarning(
                "qa trigger failed ({Repo}#{Pr}): {Err} {Detail}",
                repository, prNumber, result.Error, result.Detail);
            return ScmWebhookResult.Ignored;
        }
        if (result.QaRunId is { } runId)
        {
            await audit.RecordAsync("scm.qa_triggered",
                orgId: connection.OrgId,
                targetType: "qa_run", targetId: runId.ToString(),
                metadata: new
                {
                    repository,
                    pr = prNumber,
                    head_sha = headSha,
                    action,
                },
                cancellationToken: ct);
        }
        return result.QaRunId is null
            ? ScmWebhookResult.Ignored
            : ScmWebhookResult.Processed;
    }

    /// <summary>
    /// Gates do trigger QA de <c>pull_request</c> (GitHub): QA habilitado,
    /// action <c>opened</c>/<c>synchronize</c>, repo inscrito, não-draft,
    /// número/head válidos. Retorna os dados extraídos via out.
    /// </summary>
    private bool TryReadQaPullRequest(
        JsonElement root, ScmConnection connection,
        out string repository, out int prNumber, out string headSha,
        out string? baseSha, out string? action)
    {
        repository = "";
        prNumber = 0;
        headSha = "";
        baseSha = null;
        action = null;
        if (qa is null || qaOptions?.Value.Enabled is false)
        {
            return false;
        }
        if (!root.TryGetProperty("pull_request", out var pr)
            || !root.TryGetProperty(ActionProp, out var act))
        {
            return false;
        }
        action = act.GetString();
        if (action is not ("opened" or "synchronize"))
        {
            return false;
        }
        repository = root.TryGetProperty(Repository, out var repo)
            && repo.TryGetProperty("full_name", out var fn)
                ? fn.GetString() ?? "" : "";
        if (repository.Length == 0 || !connection.QaCovers(repository))
        {
            return false;
        }
        if (pr.TryGetProperty("draft", out var d) && d.GetBoolean())
        {
            return false;
        }

        prNumber = pr.TryGetProperty(NumberProp, out var n)
            ? n.GetInt32() : 0;
        headSha = pr.TryGetProperty("head", out var h)
            && h.TryGetProperty("sha", out var hs)
                ? hs.GetString() ?? "" : "";
        baseSha = pr.TryGetProperty("base", out var bs)
            && bs.TryGetProperty("sha", out var bss)
                ? bss.GetString() : null;
        return prNumber > 0 && headSha.Length > 0;
    }

    /// <summary>
    /// SPEC-141 RF-001 — roteamento dos eventos de CI: normaliza o
    /// payload (<see cref="Autofix.CiCheckEventParser"/>) e dispara
    /// <see cref="IAutofixService.TriggerCiEventAsync"/>. Skips de
    /// política (não-falha, não inscrito, anti-loop, dedup) →
    /// <see cref="ScmWebhookResult.Ignored"/>.
    /// </summary>
    private async Task<ScmWebhookResult> HandleCiEventAsync(
        string eventType, JsonElement root, ScmConnection connection,
        CancellationToken ct)
    {
        if (autofix is null || autofixOptions?.Value.Enabled is false)
        {
            return ScmWebhookResult.Ignored;
        }
        var ev = Autofix.CiCheckEventParser.Parse(eventType, root);
        if (ev is null
            || !Autofix.CiCheckEventParser.FailureConclusions
                .Contains(ev.Conclusion))
        {
            return ScmWebhookResult.Ignored;
        }

        var result = await autofix.TriggerCiEventAsync(connection, ev, ct);
        if (result.RunId is { } runId)
        {
            await audit.RecordAsync("scm.autofix_triggered",
                orgId: connection.OrgId,
                targetType: "autofix_run", targetId: runId.ToString(),
                metadata: new
                {
                    repository = ev.Repository,
                    pr = ev.PrNumber,
                    head_sha = ev.HeadSha,
                    check = ev.CheckName,
                    check_kind = ev.Kind,
                },
                cancellationToken: ct);
            return ScmWebhookResult.Processed;
        }
        if (result.Error is AutofixTriggerError.NoConnection)
        {
            logger.LogWarning(
                "autofix trigger failed ({Repo} {Check}): {Err} {Detail}",
                ev.Repository, ev.CheckName, result.Error, result.Detail);
        }
        return ScmWebhookResult.Ignored;
    }

    /// <summary>
    /// SPEC-145 RF-001: push event (GitHub <c>push</c> / GitLab
    /// <c>Push Hook</c>). Quando a branch tocada tem índice desta
    /// conexão: re-index se <c>Indexing:AutoRefreshOnPush</c> e marca
    /// stale das páginas de wiki que cobrem os arquivos do diff.
    /// </summary>
    private async Task<ScmWebhookResult> HandlePushEventAsync(
        ScmKind kind, JsonElement root, ScmConnection connection,
        string? repository, CancellationToken ct)
    {
        if (indexer is null && wiki is null)
        {
            return ScmWebhookResult.Ignored;
        }

        // Branch: "refs/heads/<b>" nos dois providers; branch/tag
        // deletes (after/checkout_sha zerados) são ignorados.
        var refName = root.TryGetProperty("ref", out var r)
            ? r.GetString() : null;
        const string prefix = "refs/heads/";
        if (refName is null || !refName.StartsWith(prefix,
                StringComparison.Ordinal))
        {
            return ScmWebhookResult.Ignored;
        }
        var branch = refName[prefix.Length..];
        var headSha = root.TryGetProperty("after", out var a)
            ? a.GetString()
            : root.TryGetProperty("checkout_sha", out var c2)
                ? c2.GetString() : null;
        if (headSha is null
            || headSha.All(c => c == '0')) // "0000…" = branch deleted
        {
            return ScmWebhookResult.Ignored;
        }

        repository ??= kind == ScmKind.GitLab
            ? root.TryGetProperty("project", out var pr)
                && pr.TryGetProperty("path_with_namespace", out var pn)
                    ? pn.GetString() : null
            : null;
        if (repository is null)
        {
            return ScmWebhookResult.Ignored;
        }

        // Só reage quando a conexão cobre (repo, branch) indexado —
        // push em repo desconectado é Ignored.
        var indexed = await db.RepoIndexes.IgnoreQueryFilters()
            .AnyAsync(x => x.Repository == repository
                && x.Branch == branch
                && x.OrgId == connection.OrgId, ct);
        var wikiRun = await db.WikiRuns.IgnoreQueryFilters()
            .AnyAsync(x => x.Repository == repository
                && x.Branch == branch
                && x.OrgId == connection.OrgId, ct);
        if (!indexed && !wikiRun)
        {
            return ScmWebhookResult.Ignored;
        }

        // Caminhos do diff: union(commits[].added|modified|removed).
        var changed = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("commits", out var commits)
            && commits.ValueKind == JsonValueKind.Array)
        {
            foreach (var commit in commits.EnumerateArray())
            {
                foreach (var field in new[] { "added", "modified", "removed" })
                {
                    if (commit.TryGetProperty(field, out var arr)
                        && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var p in arr.EnumerateArray())
                        {
                            if (p.GetString() is { } path)
                            {
                                changed.Add(path);
                            }
                        }
                    }
                }
            }
        }

        var processed = false;
        if (indexed && indexer is not null
            && indexingOptions?.Value.AutoRefreshOnPush is true)
        {
            var index = await indexer.TriggerIndexAsync(
                repository, branch, connection.OrgId, ct);
            if (index is not null)
            {
                processed = true;
                await audit.RecordAsync("scm.index_refreshed",
                    orgId: connection.OrgId,
                    targetType: "repo_index",
                    targetId: index.Id.ToString(),
                    metadata: new
                    {
                        repository,
                        branch,
                        head_sha = headSha,
                        trigger = "push",
                    },
                    cancellationToken: ct);
            }
        }

        if (wiki is not null
            && await wiki.HandlePushAsync(
                repository, branch, [.. changed], headSha,
                connection.OrgId, ct))
        {
            processed = true;
        }

        return processed
            ? ScmWebhookResult.Processed : ScmWebhookResult.Ignored;
    }

    /// <summary>Login do bot da App ("{slug}[bot]") — null quando desconhecido.</summary>
    private static string? BotLogin(ScmConnection c) =>
        string.IsNullOrEmpty(c.AppSlug) ? null : $"{c.AppSlug}[bot]";

    /// <summary>
    /// SPEC-140 RF-005: anti-loop — comentários/reviews do próprio bot
    /// nunca disparam menção nem automations.
    /// </summary>
    private static bool IsOwnBot(string? author, ScmConnection c) =>
        author is not null && BotLogin(c) is { } bot
            && string.Equals(author, bot,
                StringComparison.OrdinalIgnoreCase);

    private static IReadOnlySet<string> LabelsOf(JsonElement pr)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (pr.TryGetProperty("labels", out var arr)
            && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in arr.EnumerateArray())
            {
                if (l.TryGetProperty("name", out var n)
                    && n.GetString() is { } name)
                {
                    set.Add(name);
                }
            }
        }
        return set;
    }

    private static string? LabelOf(JsonElement root) =>
        root.TryGetProperty("label", out var l)
            && l.TryGetProperty("name", out var n)
                ? n.GetString() : null;

    private static string? RequestedReviewerOf(JsonElement root) =>
        root.TryGetProperty("requested_reviewer", out var r)
            && r.TryGetProperty("login", out var l)
                ? l.GetString() : null;

    /// <summary>
    /// SPEC-140 RF-001 — GitLab <c>Merge Request Hook</c>: actions
    /// {open, update, reopen, ready_for_review} → mesmas regras do
    /// GitHub (não-draft = <c>work_in_progress==false</c>; label gate).
    /// </summary>
    private async Task<ScmWebhookResult> HandleGitLabMergeRequestAsync(
        JsonElement root, ScmConnection connection, CancellationToken ct)
    {
        if (reviews is null || reviewOptions?.Value.Enabled is false)
        {
            return ScmWebhookResult.Ignored;
        }
        var repository = ExtractRepository(ScmKind.GitLab, root);
        if (repository is null || !connection.ReviewCovers(repository))
        {
            return ScmWebhookResult.Ignored;
        }
        if (!root.TryGetProperty("object_attributes", out var oa))
        {
            return ScmWebhookResult.Ignored;
        }
        var action = oa.TryGetProperty(ActionProp, out var a)
            ? a.GetString() : null;
        var draft = oa.TryGetProperty("work_in_progress", out var w)
            && w.GetBoolean();
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("labels", out var la)
            && la.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in la.EnumerateArray())
            {
                if (l.TryGetProperty("name", out var n)
                    && n.GetString() is { } name)
                {
                    labels.Add(name);
                }
            }
        }
        var mode = connection.EffectiveTriggerMode;
        var fire = action is "open" or "update" or "reopen"
                or "ready_for_review"
            && !draft
            && (mode != ReviewDefaults.TriggerModeLabelOnly
                || labels.Contains(ReviewDefaults.GateLabel));
        if (!fire)
        {
            return ScmWebhookResult.Ignored;
        }

        var prNumber = oa.TryGetProperty("iid", out var ii)
            ? ii.GetInt32() : 0;
        var headSha = oa.TryGetProperty("last_commit", out var lc)
            && lc.TryGetProperty("id", out var id)
                ? id.GetString() : null;
        if (prNumber <= 0 || string.IsNullOrEmpty(headSha))
        {
            return ScmWebhookResult.Ignored;
        }

        var result = await reviews.TriggerAsync(
            connection, repository, prNumber, headSha, WebhookTrigger,
            null, ct);
        if (result.ReviewRunId is { } runId)
        {
            await audit.RecordAsync("scm.review_triggered",
                orgId: connection.OrgId,
                targetType: "review_run", targetId: runId.ToString(),
                metadata: new
                {
                    kind = "gitlab",
                    repository,
                    pr = prNumber,
                    head_sha = headSha,
                    action,
                },
                cancellationToken: ct);
            return ScmWebhookResult.Processed;
        }
        return ScmWebhookResult.Ignored;
    }

    /// <summary>
    /// SPEC-142 RF-001 — GitLab <c>Merge Request Hook</c> para o QA:
    /// actions <c>open</c>/<c>update</c> (≙ opened/synchronize) +
    /// <see cref="ScmConnection.QaCovers"/> → trigger (dedup por head).
    /// </summary>
    private async Task<ScmWebhookResult> HandleGitLabQaMergeRequestAsync(
        JsonElement root, ScmConnection connection, CancellationToken ct)
    {
        if (!TryReadGitLabQaMr(root, connection,
                out var repository, out var prNumber, out var headSha,
                out var action))
        {
            return ScmWebhookResult.Ignored;
        }

        var result = await qa!.TriggerAsync(
            connection, repository, prNumber, headSha, baseSha: null,
            WebhookTrigger, null, ct);
        if (result.QaRunId is { } runId)
        {
            await audit.RecordAsync("scm.qa_triggered",
                orgId: connection.OrgId,
                targetType: "qa_run", targetId: runId.ToString(),
                metadata: new
                {
                    kind = "gitlab",
                    repository,
                    pr = prNumber,
                    head_sha = headSha,
                    action,
                },
                cancellationToken: ct);
            return ScmWebhookResult.Processed;
        }
        return ScmWebhookResult.Ignored;
    }

    /// <summary>
    /// Gates do trigger QA de merge_request (GitLab): QA habilitado, repo
    /// inscrito, action <c>open</c>/<c>update</c>, não-WIP, iid/head
    /// válidos. Retorna os dados extraídos via out.
    /// </summary>
    private bool TryReadGitLabQaMr(
        JsonElement root, ScmConnection connection,
        out string repository, out int prNumber, out string headSha,
        out string? action)
    {
        repository = "";
        prNumber = 0;
        headSha = "";
        action = null;
        if (qa is null || qaOptions?.Value.Enabled is false)
        {
            return false;
        }
        repository = ExtractRepository(ScmKind.GitLab, root) ?? "";
        if (repository.Length == 0 || !connection.QaCovers(repository))
        {
            return false;
        }
        if (!root.TryGetProperty("object_attributes", out var oa))
        {
            return false;
        }
        action = oa.TryGetProperty(ActionProp, out var a)
            ? a.GetString() : null;
        if (action is not ("open" or "update")
            || (oa.TryGetProperty("work_in_progress", out var w)
                && w.GetBoolean()))
        {
            return false;
        }
        prNumber = oa.TryGetProperty("iid", out var ii)
            ? ii.GetInt32() : 0;
        headSha = oa.TryGetProperty("last_commit", out var lc)
            && lc.TryGetProperty("id", out var id)
                ? id.GetString() ?? "" : "";
        return prNumber > 0 && headSha.Length > 0;
    }

    private async Task<ScmConnection?> FindConnectionAsync(
        ScmKind kind, string? installationId, string? repository,
        CancellationToken ct)
    {
        var query = db.ScmConnections.IgnoreQueryFilters()
            .Where(c => c.Kind == kind.ToWire());
        if (installationId is not null)
        {
            var byInstall = await query
                .FirstOrDefaultAsync(c => c.InstallationId == installationId, ct);
            if (byInstall is not null)
            {
                return ScmConnectionService.ToModel(byInstall);
            }
        }
        if (repository is not null)
        {
            var rows = await query.ToListAsync(ct);
            return rows.Select(ScmConnectionService.ToModel)
                .FirstOrDefault(c => c.Repos.Count == 0
                    || c.Repos.Contains(repository,
                        StringComparer.OrdinalIgnoreCase));
        }
        return null;
    }

    private async Task<bool> VerifySignatureAsync(
        ScmConnection connection, string? signature256,
        byte[] payload, CancellationToken ct)
    {
        if (connection.WebhookSecretRef is null || signature256 is null
            || !signature256.StartsWith("sha256=", StringComparison.Ordinal))
        {
            return false;
        }

        var secret = await secrets.ResolveAsync(
            "org", connection.OrgId?.ToString() ?? "",
            connection.WebhookSecretRef, ct);
        if (secret is null)
        {
            return false;
        }

        var expected = Convert.ToHexStringLower(
            HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(secret), payload));
        var presented = signature256["sha256=".Length..];
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(presented));
    }

    private async Task<bool> IsDuplicateAsync(
        ScmKind kind, string? deliveryId, byte[] payload, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(deliveryId))
        {
            return false;
        }

        var wire = kind.ToWire();
        var exists = await db.WebhookDeliveries.IgnoreQueryFilters()
            .AnyAsync(d => d.Kind == wire && d.DeliveryId == deliveryId, ct);
        if (exists)
        {
            return true;
        }

        db.WebhookDeliveries.Add(new WebhookDeliveryRecord
        {
            Id = Guid.NewGuid(),
            Kind = wire,
            DeliveryId = deliveryId,
            PayloadSha = Convert.ToHexStringLower(
                SHA256.HashData(payload)),
            ProcessedAt = DateTimeOffset.UtcNow,
        });
        try
        {
            await db.SaveChangesAsync(ct);
            return false;
        }
        catch (DbUpdateException)
        {
            // Corrida de entregas com o mesmo id — tratar como duplicata.
            db.Entry(db.WebhookDeliveries.Local
                    .First(d => d.DeliveryId == deliveryId))
                .State = EntityState.Detached;
            return true;
        }
    }

    private sealed record MentionFields(
        string? Body, string? Author, string? Branch,
        string? PrUrl, int? PrNumber, long? InReplyTo);

    private static ScmMentionContext? ParseMention(
        string eventType, JsonElement root, string? installationId)
    {
        string? repository = null;
        if (root.TryGetProperty(Repository, out var repo)
            && repo.TryGetProperty("full_name", out var fn))
        {
            repository = fn.GetString();
        }
        if (repository is null)
        {
            return null;
        }

        var f = eventType switch
        {
            "issue_comment" => ParseIssueComment(root),
            "pull_request_review_comment" => ParsePrReviewComment(root),
            "pull_request_review" => ParsePrReview(root),
            _ => null,
        };
        if (f?.Body is null || !f.Body.Contains(Mention, StringComparison.Ordinal))
        {
            return null;
        }

        return new ScmMentionContext(
            ScmKind.GitHub, repository, f.Branch, f.PrUrl, f.PrNumber,
            f.Author, f.Body, f.InReplyTo, installationId);
    }

    private static (string? PrUrl, int? PrNumber, string? Branch) PullRefOf(JsonElement pr)
        => (pr.TryGetProperty("html_url", out var u) ? u.GetString() : null,
            pr.TryGetProperty(NumberProp, out var n) ? n.GetInt32() : null,
            pr.TryGetProperty("head", out var h)
                && h.TryGetProperty("ref", out var rf)
                    ? rf.GetString() : null);

    private static MentionFields? ParseIssueComment(JsonElement root)
    {
        if (!root.TryGetProperty(Comment, out var c))
        {
            return null;
        }
        var body = c.TryGetProperty("body", out var b) ? b.GetString() : null;
        var author = c.TryGetProperty("user", out var u)
            && u.TryGetProperty("login", out var l)
                ? l.GetString() : null;
        string? prUrl = null;
        int? prNumber = null;
        if (root.TryGetProperty("issue", out var issue)
            && issue.TryGetProperty("pull_request", out var pr)
            && pr.TryGetProperty("html_url", out var pu))
        {
            prUrl = pu.GetString();
            prNumber = issue.GetProperty(NumberProp).GetInt32();
        }
        return new MentionFields(body, author, null, prUrl, prNumber, null);
    }

    private static MentionFields? ParsePrReviewComment(JsonElement root)
    {
        if (!root.TryGetProperty(Comment, out var c))
        {
            return null;
        }
        var body = c.TryGetProperty("body", out var b) ? b.GetString() : null;
        var author = c.TryGetProperty("user", out var u)
            && u.TryGetProperty("login", out var l)
                ? l.GetString() : null;
        long? inReplyTo = null;
        if (c.TryGetProperty("in_reply_to_id", out var r)
            && r.ValueKind == JsonValueKind.Number)
        {
            inReplyTo = r.GetInt64();
        }
        string? prUrl = null, branch = null;
        int? prNumber = null;
        if (root.TryGetProperty("pull_request", out var pr))
        {
            (prUrl, prNumber, branch) = PullRefOf(pr);
        }
        return new MentionFields(body, author, branch, prUrl, prNumber, inReplyTo);
    }

    private static MentionFields? ParsePrReview(JsonElement root)
    {
        if (!root.TryGetProperty("review", out var rv))
        {
            return null;
        }
        var body = rv.TryGetProperty("body", out var b) ? b.GetString() : null;
        var author = rv.TryGetProperty("user", out var u)
            && u.TryGetProperty("login", out var l)
                ? l.GetString() : null;
        string? prUrl = null, branch = null;
        int? prNumber = null;
        if (root.TryGetProperty("pull_request", out var pr))
        {
            (prUrl, prNumber, branch) = PullRefOf(pr);
        }
        return new MentionFields(body, author, branch, prUrl, prNumber, null);
    }

    /// <inheritdoc />
    public async Task<ScmWebhookResult> HandleAsync(
        ScmKind kind, string? signatureOrToken, string? eventType,
        string? deliveryId, byte[] payload, CancellationToken ct)
    {
        if (kind == ScmKind.GitHub)
        {
            return await HandleGitHubAsync(
                signatureOrToken, eventType ?? "", deliveryId, payload, ct);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return ScmWebhookResult.InvalidSignature;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var repository = ExtractRepository(kind, root);
            var connection = await FindConnectionAsync(
                kind, null, repository, ct);
            if (connection is null)
            {
                return ScmWebhookResult.NoConnection;
            }

            if (!await VerifyByKindAsync(
                    kind, connection, signatureOrToken, payload, ct))
            {
                await audit.RecordAsync("scm.webhook_rejected", orgId: null,
                    targetType: "scm_connection",
                    targetId: connection.Id.ToString(),
                    metadata: new
                    {
                        reason = "bad_signature",
                        eventType,
                        kind = kind.ToWire(),
                    },
                    cancellationToken: ct);
                return ScmWebhookResult.InvalidSignature;
            }

            if (await IsDuplicateAsync(kind, deliveryId, payload, ct))
            {
                return ScmWebhookResult.Duplicate;
            }

            // SPEC-140 RF-001: GitLab Merge Request Hook — equivalente do
            // pull_request (open|update|reopen|ready_for_review).
            // SPEC-142 RF-001: QA segue o mesmo hook (open|update).
            if (kind == ScmKind.GitLab
                && eventType == "Merge Request Hook")
            {
                var reviewResult = await HandleGitLabMergeRequestAsync(
                    root, connection, ct);
                var qaResult = await HandleGitLabQaMergeRequestAsync(
                    root, connection, ct);
                return reviewResult == ScmWebhookResult.Processed
                    || qaResult == ScmWebhookResult.Processed
                        ? ScmWebhookResult.Processed
                        : ScmWebhookResult.Ignored;
            }

            // SPEC-145 RF-001: GitLab Push Hook — mesmo pipeline do
            // push do GitHub (re-index + wiki stale).
            if (kind == ScmKind.GitLab && eventType == "Push Hook")
            {
                return await HandlePushEventAsync(
                    kind, root, connection, repository, ct);
            }

            var mention = ParseMentionByKind(kind, eventType, root);
            if (mention is null || IsOwnBot(mention.CommentAuthor, connection))
            {
                return ScmWebhookResult.Ignored;
            }

            var ownerUserId = await ResolveOwnerUserIdAsync(connection, ct);
            if (ownerUserId is null)
            {
                return ScmWebhookResult.Ignored;
            }

            var prompt = BuildPrompt(mention);
            var task = await conversations.CreateForUserAsync(
                ownerUserId.Value, connection.OrgId,
                new CreateConversationRequest(
                    new InitialMessage("user", [prompt], Run: true),
                    mention.Repository,
                    AgentType: null, LlmModel: null, SandboxSpecId: null),
                ct);

            await audit.RecordAsync("scm.webhook_triggered",
                actorUserId: ownerUserId.Value.ToString(),
                orgId: connection.OrgId,
                targetType: "app_conversation",
                targetId: task.AppConversationId?.ToString(),
                metadata: new
                {
                    event_type = eventType,
                    kind = kind.ToWire(),
                    repository = mention.Repository,
                    pr = mention.PullRequestNumber,
                },
                cancellationToken: ct);
            return ScmWebhookResult.Processed;
        }
    }

    /// <summary>SPEC-53: verificação por kind (token | HMAC | shared secret).</summary>
    private async Task<bool> VerifyByKindAsync(
        ScmKind kind, ScmConnection connection, string? signatureOrToken,
        byte[] payload, CancellationToken ct)
    {
        if (kind == ScmKind.Bitbucket)
        {
            // X-Hub-Signature "sha256=<hmac>" — mesmo esquema do GitHub.
            return await VerifySignatureAsync(
                connection, signatureOrToken, payload, ct);
        }

        if (connection.WebhookSecretRef is null || signatureOrToken is null)
        {
            return false;
        }
        var secret = await secrets.ResolveAsync(
            "org", connection.OrgId?.ToString() ?? "",
            connection.WebhookSecretRef, ct);
        if (secret is null)
        {
            return false;
        }
        // GitLab X-Gitlab-Token / AzDO shared secret: igualdade em tempo fixo.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(signatureOrToken));
    }

    /// <summary>SPEC-53: extrai "owner/repo" do payload por provider.</summary>
    private static string? ExtractRepository(ScmKind kind, JsonElement root)
    {
        switch (kind)
        {
            case ScmKind.GitLab:
                return root.TryGetProperty("project", out var p)
                    && p.TryGetProperty("path_with_namespace", out var pn)
                        ? pn.GetString() : null;
            case ScmKind.Bitbucket:
                return root.TryGetProperty(Repository, out var r)
                    && r.TryGetProperty("full_name", out var fn)
                        ? fn.GetString() : null;
            case ScmKind.AzureDevOps:
                return root.TryGetProperty("resource", out var res)
                    && res.TryGetProperty(Repository, out var rr)
                    && rr.TryGetProperty("name", out var rn)
                        ? rn.GetString() : null;
            default:
                return null;
        }
    }

    /// <summary>SPEC-53: parse da menção por provider (note/comment events).</summary>
    private static ScmMentionContext? ParseMentionByKind(
        ScmKind kind, string? eventType, JsonElement root)
    {
        var repository = ExtractRepository(kind, root);
        if (repository is null)
        {
            return null;
        }

        var f = kind switch
        {
            ScmKind.GitLab => ParseGitLabMention(eventType, root),
            ScmKind.Bitbucket => ParseBitbucketMention(eventType, root),
            ScmKind.AzureDevOps => ParseAzureMention(root),
            _ => null,
        };
        if (f?.Body is null || !f.Body.Contains(Mention, StringComparison.Ordinal))
        {
            return null;
        }
        return new ScmMentionContext(
            kind, repository, f.Branch, f.PrUrl, f.PrNumber,
            f.Author, f.Body, null, null);
    }

    private static MentionFields? ParseGitLabMention(
        string? eventType, JsonElement root)
    {
        if (eventType != "Note Hook"
            || !root.TryGetProperty("object_attributes", out var oa))
        {
            return null;
        }
        var body = oa.TryGetProperty("note", out var n)
            ? n.GetString() : null;
        var author = root.TryGetProperty("user", out var gu)
            && gu.TryGetProperty("username", out var un)
                ? un.GetString() : null;
        string? prUrl = null, branch = null;
        int? prNumber = null;
        if (oa.TryGetProperty("noteable_type", out var nt)
            && nt.GetString() == "MergeRequest"
            && root.TryGetProperty("merge_request", out var glmr))
        {
            prNumber = glmr.TryGetProperty("iid", out var ii)
                ? ii.GetInt32() : null;
            prUrl = glmr.TryGetProperty("url", out var gu2)
                ? gu2.GetString() : null;
            branch = glmr.TryGetProperty("source_branch", out var sb)
                ? sb.GetString() : null;
        }
        return new MentionFields(body, author, branch, prUrl, prNumber, null);
    }

    private static MentionFields? ParseBitbucketMention(
        string? eventType, JsonElement root)
    {
        if (eventType != "pullrequest:comment_created"
            && eventType != "issue:comment_created")
        {
            return null;
        }
        string? body = null;
        if (root.TryGetProperty(Comment, out var bc)
            && bc.TryGetProperty("content", out var bco))
        {
            body = bco.TryGetProperty("raw", out var raw)
                ? raw.GetString() : null;
        }
        var author = root.TryGetProperty(Comment, out var bcc)
            && bcc.TryGetProperty("user", out var bu)
            && bu.TryGetProperty("nickname", out var bn)
                ? bn.GetString() : null;
        string? prUrl = null;
        int? prNumber = null;
        if (root.TryGetProperty("pullrequest", out var bbpr))
        {
            prNumber = bbpr.TryGetProperty("id", out var bid)
                ? bid.GetInt32() : null;
            prUrl = bbpr.TryGetProperty("links", out var bl)
                && bl.TryGetProperty("html", out var bh)
                && bh.TryGetProperty("href", out var bh2)
                    ? bh2.GetString() : null;
        }
        return new MentionFields(body, author, null, prUrl, prNumber, null);
    }

    private static MentionFields? ParseAzureMention(JsonElement root)
    {
        if (!root.TryGetProperty("resource", out var res))
        {
            return null;
        }
        // ms.vss-code.git-pullrequest-comment-event
        if (!res.TryGetProperty(Comment, out var ac))
        {
            return null;
        }
        var body = ac.TryGetProperty("content", out var aco)
            ? aco.GetString() : null;
        var author = ac.TryGetProperty("author", out var aa)
            && aa.TryGetProperty("displayName", out var an)
                ? an.GetString() : null;
        int? prNumber = res.TryGetProperty("pullRequest", out var apr)
            && apr.TryGetProperty("pullRequestId", out var aid)
                ? aid.GetInt32() : null;
        return new MentionFields(body, author, null, null, prNumber, null);
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

    // SPEC-91: qualquer texto derivado do payload entra envelopado; o
    // cabeçalho estrutural e ids validados (nº do PR como int) ficam fora.
    internal static string BuildPrompt(
        ScmMentionContext mention)
    {
        var src = $"github:{mention.Repository}"
            + (mention.PullRequestNumber is { } pr ? $"#{pr}" : "");
        var env = new UntrustedInput.Envelope();
        var sb = new StringBuilder();
        sb.Append("[SCM trigger: github webhook]\n");
        sb.Append($"Repository: {env.WrapField(mention.Repository, src, "repository")}\n");
        if (mention.PullRequestNumber is { } n)
        {
            sb.Append($"Pull request: #{n} ({env.WrapField(mention.PullRequestUrl, src, "pull_request.url")})\n");
        }
        if (mention.Branch is not null)
        {
            sb.Append($"Branch: {env.WrapField(mention.Branch, src, "branch")}\n");
        }
        sb.Append($"Mentioned by: {env.WrapField(mention.CommentAuthor, src, "comment.author")}\n");
        sb.Append("Instruction:\n");
        sb.Append(env.WrapField(mention.CommentBody.Replace(Mention, "").Trim(),
            src, "comment.body"));
        return sb.ToString();
    }
}