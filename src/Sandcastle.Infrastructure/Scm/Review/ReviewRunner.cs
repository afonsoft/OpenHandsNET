using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Scm;
using Sandcastle.Infrastructure.Agent;
using Sandcastle.Infrastructure.Agent.SystemPrompt;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Security;

namespace Sandcastle.Infrastructure.Scm.Review;

/// <summary>
/// <see cref="IReviewRunner"/> (SPEC-140 RF-002/RF-003/RF-005): job
/// próprio — NÃO cria conversa visível. Pipeline: fetch diff (caps +
/// seleção não-generated) → dedup por diff hash → repo rules
/// (envelope untrusted) → LLM structured findings → validação contra o
/// diff → posting (review inline ou comentário único) → commit status
/// <c>sandcastle/review</c> (advisory — pass-condition por conexão) →
/// persistência + usage metering.
/// </summary>
public sealed class ReviewRunner( // NOSONAR S107 — SPEC-104 (decisão aprovada): DI ctor — aridade = lista de dependências (bundle = service locator)
    AppDbContext db,
    IScmProviderRegistry providers,
    IAgentLlmProvider llm,
    IModelRouter router,
    IOptions<ReviewOptions> options,
    IUsageMeter usage,
    IAuditService audit,
    ILogger<ReviewRunner> logger) : IReviewRunner
{
    /// <summary>Repo rules files lidos via contents API (ordem fixa).</summary>
    private static readonly string[] RuleFiles =
    [
        "AGENTS.md", "CLAUDE.md", ".sandcastle/review.md",
        ".sandcastle/rules.md", ".github/copilot-instructions.md",
    ];

    /// <summary>Diretório de regras adicionais (globbed via contents API).</summary>
    private const string RulesDirectory = ".sandcastle/rules";

    /// <inheritdoc />
    public async Task RunAsync(Guid reviewRunId, CancellationToken ct)
    {
        var run = await db.ReviewRuns.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == reviewRunId, ct);
        if (run is null
            || run.Status is not (ReviewRunStatuses.Queued
                or ReviewRunStatuses.Running))
        {
            return;
        }

        var connectionRecord = await db.ScmConnections.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == run.ConnectionId, ct);
        if (connectionRecord is null)
        {
            await FinishAsync(run, ReviewRunStatuses.Failed,
                "scm connection not found", ct);
            return;
        }

        run.Status = ReviewRunStatuses.Running;
        run.StartedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var connection = ScmConnectionService.ToModel(connectionRecord);
        try
        {
            await ExecuteAsync(run, connection, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e,
                "review run {Id} failed ({Repo}#{Pr})", run.Id,
                run.Repository, run.PrNumber);
            await FinishAsync(run, ReviewRunStatuses.Failed,
                e.Message, ct);
            await PostStatusAsync(connection, run, "error",
                "Review failed (advisory)", ct);
            await audit.RecordAsync("scm.review_failed",
                actorUserId: run.RequestedByUserId, orgId: run.OrgId,
                targetType: "review_run", targetId: run.Id.ToString(),
                metadata: new
                {
                    repository = run.Repository,
                    pr = run.PrNumber,
                    error = e.Message[..Math.Min(200, e.Message.Length)],
                },
                cancellationToken: ct);
        }
    }

    private async Task ExecuteAsync(
        ReviewRunRecord run, ScmConnection connection, CancellationToken ct)
    {
        var provider = providers.Get(connection.Kind)
            ?? throw new InvalidOperationException(
                $"provider '{connection.Kind.ToWire()}' not registered");
        var o = options.Value;

        // 1) diff do PR (arquivos + patches).
        var files = await provider.GetPullRequestFilesAsync(
            connection, run.Repository, run.PrNumber, ct);

        // 2) dedup por conteúdo — mesmo diff já revisado → skip
        //    (paridade Devin re-review skip, SPEC-140 RF-003).
        var diffHash = DiffIndex.HashOf(files);
        run.DiffHash = diffHash;
        var twin = await db.ReviewRuns.IgnoreQueryFilters()
            .Where(r => r.Id != run.Id
                && r.Repository == run.Repository
                && r.PrNumber == run.PrNumber
                && r.DiffHash == diffHash
                && (r.Status == ReviewRunStatuses.Completed
                    || r.Status == ReviewRunStatuses.Skipped))
            .OrderByDescending(r => r.CreatedAtTicks)
            .FirstOrDefaultAsync(ct);
        if (twin is not null)
        {
            run.FinishedAt = DateTimeOffset.UtcNow;
            await FinishAsync(run, ReviewRunStatuses.Skipped,
                $"same diff already reviewed in run {twin.Id}", ct);
            return;
        }

        // 3) seleção por caps (MaxFilesPerRun/MaxDiffKb) + heurística
        //    não-generated; run marcado partial quando trunca.
        var (selected, partial) = DiffIndex.SelectForReview(
            files, o.MaxFilesPerRun, o.MaxDiffKb * 1024);
        run.Partial = partial;
        var diffIndex = DiffIndex.Build(selected);
        var diffText = SecretRedactor.Redact(DiffIndex.Render(selected));

        string? prTitle = null;
        try
        {
            prTitle = (await provider.GetPullRequestAsync(
                connection, run.Repository, run.PrNumber, ct))?.Title;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogDebug(e, "pr title fetch failed — seguindo sem");
        }

        // 4) repo rules — untrusted (SPEC-91) + SecretRedactor (NFR).
        var rules = await LoadRulesAsync(
            provider, connection, run.Repository, run.HeadSha, ct);

        // 5) LLM → findings estruturados.
        var prompt = BuildPrompt(run, prTitle, files.Count, diffText, rules);
        var model = (await router.SelectAsync(Guid.Empty,
            ModelRole.Reviewer, o.Model, ct)).Model;
        var response = await CompleteAsync(prompt, model, o, ct);

        // 6) validação: path/line precisam existir no diff (dropa alucinação).
        var (findings, dropped) = ReviewFindingParser.Parse(
            SecretRedactor.Redact(response.Text), diffIndex);

        // 7) persistir findings (redacted).
        var now = DateTimeOffset.UtcNow;
        var records = findings.Select(f => new ReviewFindingRecord
        {
            Id = Guid.NewGuid(),
            OrgId = run.OrgId,
            ReviewRunId = run.Id,
            Repository = run.Repository,
            PrNumber = run.PrNumber,
            Kind = f.Kind,
            Severity = f.Severity,
            Confidence = f.Confidence,
            Path = f.Path,
            Line = f.Line,
            Title = f.Title is null ? null
                : SecretRedactor.Redact(f.Title)[..Math.Min(300,
                    SecretRedactor.Redact(f.Title).Length)],
            Body = SecretRedactor.Redact(f.Body),
            SuggestedPatch = f.SuggestedPatch is null ? null
                : SecretRedactor.Redact(f.SuggestedPatch),
            Status = ReviewFindingStatuses.Open,
            CreatedAt = now,
            UpdatedAt = now,
        }).ToList();
        db.ReviewFindings.AddRange(records);

        // 8) findings `open` de runs anteriores do mesmo PR viram stale
        //    (o diff novo os substitui — SPEC-140 RF-004 lifecycle).
        var stale = await db.ReviewFindings.IgnoreQueryFilters()
            .Where(f => f.Repository == run.Repository
                && f.PrNumber == run.PrNumber
                && f.ReviewRunId != run.Id
                && f.Status == ReviewFindingStatuses.Open)
            .ToListAsync(ct);
        foreach (var s in stale)
        {
            s.Status = ReviewFindingStatuses.Stale;
            s.UpdatedAt = now;
        }

        // 9) posting — review inline (cap MaxInlineComments) ou
        //    comentário único de fallback.
        var inline = findings.Where(f => f.Line is not null)
            .Take(o.MaxInlineComments).ToList();
        var overflow = findings.Skip(o.MaxInlineComments).ToList();
        var summary = ReviewFormatter.Summary(
            findings, overflow, selected.Count, files.Count, partial,
            inlineAnchored: true);
        var comments = inline.Select(f => new ScmReviewComment(
            f.Path, f.Line!.Value, ReviewFormatter.InlineBody(f))).ToList();

        ScmReviewResult? review = null;
        try
        {
            review = await provider.CreateReviewAsync(
                connection, run.Repository, run.PrNumber, run.HeadSha,
                summary, comments, ct);
        }
        catch (NotSupportedException)
        {
            review = null; // fallback p/ comentário único
        }

        string? reviewUrl;
        if (review is not null)
        {
            reviewUrl = review.Url;
        }
        else
        {
            var body = ReviewFormatter.FallbackBody(
                findings, selected.Count, files.Count, partial);
            await provider.CreateIssueCommentAsync(
                connection, run.Repository, run.PrNumber, body, ct);
            reviewUrl = null;
        }

        // 10) commit status sandcastle/review — advisory, pass-condition
        //     da conexão (nunca required por default).
        var (state, description) = ReviewFormatter.CommitStatus(
            connection.EffectivePassCondition, findings, partial);
        await PostStatusAsync(connection, run, state, description, ct);

        // 11) usage metering (quota do owner/org da conexão — RF-005).
        if (run.OrgId is { } orgId && response.Usage is { } u)
        {
            await usage.RecordTokensAsync(
                orgId, u.PromptTokens + u.CompletionTokens, u.CostUsd, ct);
        }

        // 12) fechar o run + audit.
        var stats = new
        {
            files_total = files.Count,
            files_reviewed = selected.Count,
            findings = findings.Count,
            findings_dropped_invalid = dropped,
            inline_comments = comments.Count,
            overflow_findings = overflow.Count,
            review_posted = review is not null,
            model,
            prompt_tokens = response.Usage?.PromptTokens,
            completion_tokens = response.Usage?.CompletionTokens,
        };
        run.StatsJson = JsonSerializer.Serialize(stats);
        run.FinishedAt = DateTimeOffset.UtcNow;
        await FinishAsync(run, ReviewRunStatuses.Completed, null, ct);

        await audit.RecordAsync("scm.review_completed",
            actorUserId: run.RequestedByUserId, orgId: run.OrgId,
            targetType: "review_run", targetId: run.Id.ToString(),
            metadata: new
            {
                repository = run.Repository,
                pr = run.PrNumber,
                head_sha = run.HeadSha,
                findings = findings.Count,
                partial,
            },
            cancellationToken: ct);

        foreach (var r in records)
        {
            r.CommentUrl = reviewUrl;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Regras do repo via contents API — concatenadas no budget.</summary>
    private async Task<IReadOnlyList<(string Path, string Content)>>
        LoadRulesAsync(IScmProvider provider, ScmConnection connection,
            string repository, string reference, CancellationToken ct)
    {
        var paths = new List<string>(RuleFiles);
        try
        {
            var dir = await provider.ListDirectoryAsync(
                connection, repository, RulesDirectory, reference, ct);
            paths.AddRange(dir.Where(p =>
                p.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogDebug(e, "rules dir listing failed ({Repo})", repository);
        }

        var result = new List<(string, string)>();
        var budget = options.Value.MaxRulesChars;
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            if (budget <= 0)
            {
                break;
            }
            string? content;
            try
            {
                content = await provider.GetFileContentAsync(
                    connection, repository, path, reference, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogDebug(e,
                    "rule file {Path} unreadable ({Repo})", path, repository);
                continue;
            }
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }
            var redacted = SecretRedactor.Redact(content);
            result.Add((path,
                redacted[..Math.Min(redacted.Length, budget)]));
            budget -= redacted.Length;
        }
        return result;
    }

    /// <summary>Prompt do run — todo conteúdo externo envelopado (SPEC-91).</summary>
    private static string BuildPrompt(
        ReviewRunRecord run, string? prTitle, int filesTotal,
        string diffText, IReadOnlyList<(string Path, string Content)> rules)
    {
        var src = $"scm:{run.Repository}#{run.PrNumber}";
        var sb = new StringBuilder();
        sb.Append("Review pull request #").Append(run.PrNumber)
            .Append(" of ").Append(run.Repository)
            .Append(" (").Append(filesTotal).Append(" file(s) changed, ")
            .Append("head ").Append(run.HeadSha).Append(").\n\n");
        if (prTitle is not null)
        {
            sb.Append("PR title: ")
                .Append(UntrustedInput.Wrap(prTitle, src, "pr.title"))
                .Append('\n');
        }
        foreach (var (path, content) in rules)
        {
            sb.Append('\n').Append(UntrustedInput.Wrap(
                content, src, $"repo_rules:{path}")).Append('\n');
        }
        // O diff é o envelope principal — cap interno de 4KB não se aplica;
        // o limite real é Review:MaxDiffKb (já aplicado na seleção).
        sb.Append("\nDiff to review:\n")
            .Append(UntrustedInput.Wrap(diffText, src, "diff", int.MaxValue));
        return sb.ToString();
    }

    /// <summary>Uma completion — texto puro + usage do CompletionEnd.</summary>
    private async Task<(string Text, LlmUsage? Usage)> CompleteAsync(
        string prompt, string model, ReviewOptions o, CancellationToken ct)
    {
        var system = EmbeddedPrompts.Get("pr-review");
        var text = new StringBuilder();
        LlmUsage? usage = null;
        await foreach (var chunk in llm.ChatStreamAsync(
            [
                new LlmMessage("system", system),
                LlmMessage.User(prompt),
            ],
            [],
            new LlmOptions(
                Model: model,
                Temperature: 0.1,
                MaxTokens: o.MaxCompletionTokens),
            ct))
        {
            switch (chunk)
            {
                case TextDelta d:
                    text.Append(d.Text);
                    break;
                case CompletionEnd end:
                    usage = end.Usage;
                    break;
            }
        }
        return (text.ToString(), usage);
    }

    /// <summary>Posta o commit status — melhor esforço (nunca lança).</summary>
    private async Task PostStatusAsync(
        ScmConnection connection, ReviewRunRecord run, string state,
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
                ReviewDefaults.StatusContext,
                targetUrl: null, description, ct);
        }
        catch (Exception e)
        {
            logger.LogDebug(e,
                "commit status post skipped (run {Id})", run.Id);
        }
    }

    private async Task FinishAsync(
        ReviewRunRecord run, string status, string? error,
        CancellationToken ct)
    {
        run.Status = status;
        run.Error = error;
        run.FinishedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}