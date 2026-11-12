using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Artifacts;
using Sandcastle.Application.Git;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Sandbox;
using Sandcastle.Application.Scm;

using Sandcastle.Infrastructure.Agent;
using Sandcastle.Infrastructure.Agent.SystemPrompt;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Sandbox;
using Sandcastle.Infrastructure.Scm.Review;
using Sandcastle.Infrastructure.Security;

namespace Sandcastle.Infrastructure.Scm.Qa;

/// <summary>
/// SPEC-142 RF-002 — runner do QA agent. Pipeline de quatro fases num
/// sandbox descartável por run:
///
/// <list type="number">
/// <item><b>Understand</b> — diff+PR via <see cref="IScmProvider"/> →
/// LLM → <see cref="QaPlan"/> (classificação + passos).</item>
/// <item><b>Setup</b> — clone do head exato, provision de credencial,
/// install/setup (<c>sandcastle.yaml</c>/detecção), app start +
/// readiness.</item>
/// <item><b>Exercise</b> — exec (CLI/API via sandbox exec) e browser
/// (Playwright via <see cref="IQaBrowserDriver"/>); screenshot por
/// passo browser; bugfix reproduce-on-base quando viável; caps
/// <c>Qa:MaxSteps</c>/<c>Qa:MaxMinutes</c>.</item>
/// <item><b>Report</b> — verdict <c>PASS|FAIL|PARTIAL</c> honesto,
/// comentário no PR com evidência auth'd e status
/// <c>sandcastle/qa</c> advisory.</item>
/// </list>
///
/// QA **nunca** edita a branch do PR — somente comenta e publica status.
/// PARTIAL é bail-out honesto: passos não executados jamais promovem a
/// run a PASS.
/// </summary>
public sealed class QaRunner : IQaRunner
{
    private const string RepoDir = "/workspace/repo";

    /// <summary>Working dir do run fora do repo e fora de /tmp (S5443).</summary>
    private const string WorkDir = "/workspace/.qa";

    /// <summary>Kind de passo do plano executado no browser.</summary>
    internal const string Browser = "browser";

    private readonly AppDbContext _db;
    private readonly IScmProviderRegistry _providers;
    private readonly IAgentLlmProvider _llm;
    private readonly IModelRouter _router;
    private readonly IOptions<QaOptions> _options;
    private readonly ISandboxService _sandboxes;
    private readonly ISandboxExecutor _executor;
    private readonly IGitService _git;
    private readonly IGitHubClientFactory _github;
    private readonly IQaBrowserDriverFactory _driverFactory;
    private readonly IArtifactStore _artifacts;
    private readonly IUsageMeter _meter;
    private readonly IAuditService _audit;
    private readonly ILogger<QaRunner> _logger;

    /// <summary>DI composition root.</summary>
    public QaRunner( // NOSONAR S107 — composition root do runner (SPEC-140/141 pattern)
        AppDbContext db,
        IScmProviderRegistry providers,
        IAgentLlmProvider llm,
        IModelRouter router,
        IOptions<QaOptions> options,
        ISandboxService sandboxes,
        ISandboxExecutor executor,
        IGitService git,
        IGitHubClientFactory github,
        IQaBrowserDriverFactory driverFactory,
        IArtifactStore artifacts,
        IUsageMeter meter,
        IAuditService audit,
        ILogger<QaRunner> logger)
    {
        _db = db;
        _providers = providers;
        _llm = llm;
        _router = router;
        _options = options;
        _sandboxes = sandboxes;
        _executor = executor;
        _git = git;
        _github = github;
        _driverFactory = driverFactory;
        _artifacts = artifacts;
        _meter = meter;
        _audit = audit;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task RunAsync(Guid qaRunId, CancellationToken ct)
    {
        var run = await _db.QaRuns.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == qaRunId, ct);
        if (run is null)
        {
            _logger.LogWarning("qa run {Id} not found", qaRunId);
            return;
        }
        if (run.Status != QaRunStatuses.Queued)
        {
            return; // só queued executa — entrada duplicada/terminal é no-op
        }

        var connection = await _db.ScmConnections.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == run.ConnectionId, ct);
        var model = connection is null
            ? null : ScmConnectionService.ToModel(connection);
        var provider = model is null
            ? null : _providers.Get(model.Kind);
        if (model is null || provider is null)
        {
            await FailAsync(run, "scm connection/provider missing");
            return;
        }

        // Claim atômico queued→running: o reconcile de boot pode re-
        // enfileirar um run que também já estava no canal — só o
        // primeiro claim executa (idempotente contra double-enqueue).
        var now = DateTimeOffset.UtcNow;
        var claimed = await _db.QaRuns.IgnoreQueryFilters()
            .Where(r => r.Id == run.Id && r.Status == QaRunStatuses.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, QaRunStatuses.Running)
                .SetProperty(r => r.StartedAt, now), ct);
        if (claimed == 0)
        {
            return;
        }
        run.Status = QaRunStatuses.Running;
        run.StartedAt = now;
        await AuditAsync(run, "scm.qa_started",
            new { repo = run.Repository, pr = run.PrNumber }, ct);

        try
        {
            await ExecuteAsync(run, model, provider, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "qa run {Id} failed", run.Id);
            await FailAsync(run, ex.Message);
        }
    }

    // ── Pipeline ───────────────────────────────────────────────────

    /// <summary>Pipeline das quatro fases + Report.</summary>
    private async Task ExecuteAsync(
        QaRunRecord run, ScmConnection connection,
        IScmProvider provider, CancellationToken ct)
    {
        var opts = _options.Value;
        var deadline = DateTimeOffset.UtcNow
            + TimeSpan.FromMinutes(opts.MaxMinutes);
        var recorder = new StepRecorder(_db, run);
        var artifactNs = run.ConversationId ?? run.Id;

        // ── Phase 1: Understand ──────────────────────────────────
        var files = await provider.GetPullRequestFilesAsync(
            connection, run.Repository, run.PrNumber, ct);
        var pr = await provider.GetPullRequestAsync(
            connection, run.Repository, run.PrNumber, ct);
        var (selected, partial) = DiffIndex.SelectForReview(
            files, opts.MaxFilesPerRun, opts.MaxDiffKb * 1024);
        run.PartialPlan = partial;
        var plan = await PlanAsync(run, pr, selected, ct);
        run.Classification = plan?.Classification
            ?? QaClassifications.Unknown;
        run.PlanJson = plan is null
            ? null
            : JsonSerializer.Serialize(plan, QaPlanSerialization.Options);
        await _db.SaveChangesAsync(ct);
        await AuditAsync(run, "scm.qa_plan",
            new
            {
                classification = run.Classification,
                steps = plan?.Steps.Count ?? 0,
                partial,
            }, ct);

        if (plan is null || plan.Steps.Count == 0)
        {
            await FinishAsync(run, connection, provider, recorder,
                QaVerdicts.Partial,
                "no exercisable test plan", ct);
            return;
        }

        // ── Phase 2/3: Setup + Exercise num sandbox descartável ──
        var sandbox = await _sandboxes.StartAsync(
            opts.SandboxSpecId ?? Guid.Empty, null, ct);
        try
        {
            var setupOk = await SetupAsync(
                run, connection, sandbox, plan, recorder, opts, ct);
            if (setupOk)
            {
                var baseUrl = plan.Start is { } start
                    ? $"http://localhost:{start.Port}" : null;
                await ExerciseAsync(
                    run, plan, recorder, sandbox, baseUrl,
                    deadline, opts, artifactNs, ct);
            }
        }
        finally
        {
            try
            {
                await _sandboxes.DeleteAsync(
                    sandbox.Id, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "qa run {Id} sandbox cleanup failed", run.Id);
            }
        }

        // ── Phase 4: Report ──────────────────────────────────────
        await FinishAsync(run, connection, provider, recorder,
            Verdict(recorder.Steps), recorder.BailNote, ct);
    }

    /// <summary>Understand: LLM → <see cref="QaPlan"/> (null = sem plano).</summary>
    private async Task<QaPlan?> PlanAsync(
        QaRunRecord run, ScmPullRequest? pr,
        IReadOnlyList<ScmPullRequestFile> files, CancellationToken ct)
    {
        const string src = "scm.pr";
        var user = new StringBuilder();
        user.Append("Plan QA for pull request #").Append(run.PrNumber)
            .Append(" of ").Append(run.Repository)
            .Append(" (").Append(files.Count)
            .Append(" file(s) changed, head ").Append(run.HeadSha)
            .Append(").\n\n");
        if (pr?.Title is { } title)
        {
            user.Append("PR title: ").Append(UntrustedInput.Wrap(
                SecretRedactor.Redact(title), src, "pr.title"))
                .Append('\n');
        }
        user.Append("Changed files:\n").Append(string.Join('\n',
            files.Select(f => $"{f.Status} {f.Path} "
                + $"(+{f.Additions}/-{f.Deletions})")));
        user.Append("\n\nDiff (possibly truncated):\n")
            .Append(UntrustedInput.Wrap(
                SecretRedactor.Redact(DiffIndex.Render(files)),
                src, "diff", int.MaxValue / 4));

        var model = (await _router.SelectAsync(Guid.Empty,
            ModelRole.Reviewer, _options.Value.Model, ct)).Model;
        var text = new StringBuilder();
        LlmUsage? usage = null;
        await foreach (var chunk in _llm.ChatStreamAsync(
            [
                new LlmMessage("system", EmbeddedPrompts.Get("qa-plan")),
                LlmMessage.User(user.ToString()),
            ],
            [],
            new LlmOptions(
                Model: model, Temperature: 0.1,
                MaxTokens: _options.Value.MaxCompletionTokens),
            ct))
        {
            switch (chunk)
            {
                case CompletionEnd end:
                    usage = end.Usage;
                    break;
                case TextDelta delta:
                    text.Append(delta.Text);
                    break;
            }
        }
        var total = (long?)(
            (usage?.PromptTokens ?? 0) + (usage?.CompletionTokens ?? 0));
        if (total is > 0 && run.OrgId is { } orgId)
        {
            await _meter.RecordTokensAsync(orgId, total.Value, null, ct);
        }
        return QaPlanParser.Parse(
            text.ToString(), _options.Value.MaxSteps);
    }

    /// <summary>
    /// Setup: clone do head exato → provision de credencial →
    /// install/setup (sandcastle.yaml ou detecção) → app start +
    /// readiness. Retorna false quando falha de infra impede Exercise.
    /// </summary>
    private async Task<bool> SetupAsync(
        QaRunRecord run, ScmConnection connection, Domain.Sandbox.Sandbox sandbox,
        QaPlan plan, StepRecorder recorder, QaOptions opts,
        CancellationToken ct)
    {
        // Credencial GitHub App (clone only — QA nunca faz push).
        if (connection.Kind == ScmKind.GitHub)
        {
            var prov = await TimeAsync(recorder, "provision",
                "git credentials", async () =>
                {
                    var token = await _github.GetInstallationTokenAsync(
                        connection, ct);
                    return await _git.ProvisionTokenAuthAsync(
                        sandbox, "github.com", token, ct);
                });
            if (!prov)
            {
                return false;
            }
        }

        var cloneOk = await TimeAsync(recorder, "clone",
            $"{run.Repository}@{run.HeadSha[..Math.Min(7, run.HeadSha.Length)]}",
            async () =>
            {
                await _git.CloneAsync(
                    sandbox, run.Repository,
                    $"qa/{run.HeadSha[..Math.Min(8, run.HeadSha.Length)]}",
                    null, ct);
                // Head exato — branch do PR nunca é editada.
                var co = await ExecAsync(sandbox,
                    $"git -C {RepoDir} fetch --depth 50 origin "
                    + $"{run.HeadSha} 2>/dev/null; "
                    + $"git -C {RepoDir} checkout --detach {run.HeadSha} "
                    + $"|| git -C {RepoDir} checkout --detach FETCH_HEAD",
                    TimeSpan.FromSeconds(120), ct);
                return co.ExitCode == 0;
            });
        if (!cloneOk)
        {
            return false;
        }

        foreach (var cmd in await DetectSetupAsync(sandbox, ct))
        {
            if (!await TimeAsync(recorder, "setup", cmd, async () =>
                {
                    var r = await ExecAsync(sandbox,
                        $"cd {RepoDir} && {cmd}",
                        TimeSpan.FromSeconds(opts.SetupTimeoutSeconds),
                        ct);
                    return r.ExitCode == 0;
                }))
            {
                return false;
            }
        }

        return plan.Start is not { } start
            || await StartAppAsync(
                recorder, sandbox, start, opts, ct);
    }

    /// <summary>Comandos de install/setup detectados no checkout.</summary>
    private async Task<IReadOnlyList<string>> DetectSetupAsync(
        Domain.Sandbox.Sandbox sandbox, CancellationToken ct)
    {
        var yaml = await ExecAsync(sandbox,
            $"cat {RepoDir}/sandcastle.yaml 2>/dev/null",
            TimeSpan.FromSeconds(15), ct);
        if (yaml.ExitCode == 0 && !string.IsNullOrWhiteSpace(yaml.StdOut))
        {
            var spec = BlueprintService.Parse(yaml.StdOut);
            if (spec?.SetupScript is { Length: > 0 } script)
            {
                return [$"bash {script}"];
            }
        }

        var probe = await ExecAsync(sandbox,
            $"cd {RepoDir} && ls -1 2>/dev/null | tr '\\n' ' '",
            TimeSpan.FromSeconds(15), ct);
        var listing = probe.StdOut;
        var cmds = new List<string>();
        if (listing.Contains("package.json", StringComparison.Ordinal))
        {
            cmds.Add(
                "! command -v npm >/dev/null || "
                + "(npm ci --no-audit --no-fund 2>/dev/null "
                + "|| npm install --no-audit --no-fund)");
        }
        if (listing.Contains("requirements.txt", StringComparison.Ordinal))
        {
            cmds.Add("! command -v pip >/dev/null || "
                + "pip install -q -r requirements.txt");
        }
        if (listing.Contains(".sln", StringComparison.Ordinal)
            || listing.Contains(".slnx", StringComparison.Ordinal))
        {
            cmds.Add("! command -v dotnet >/dev/null || dotnet restore");
        }
        if (listing.Contains("go.mod", StringComparison.Ordinal))
        {
            cmds.Add("! command -v go >/dev/null || go build ./...");
        }
        return cmds;
    }

    /// <summary>App start em background + readiness poll (curl).</summary>
    private async Task<bool> StartAppAsync(
        StepRecorder recorder, Domain.Sandbox.Sandbox sandbox, QaPlanStart start,
        QaOptions opts, CancellationToken ct,
        string action = "app_start")
    {
        var readyUrl = $"http://localhost:{start.Port}{start.Path ?? "/"}";
        return await TimeAsync(recorder, action, start.Command,
            async () =>
            {
                var launch = await ExecAsync(sandbox,
                    $"mkdir -p {WorkDir} && cd {RepoDir} "
                    + $"&& (nohup bash -lc '{start.Command}' "
                    + $"> {WorkDir}/qa-app.log 2>&1 "
                    + $"& echo $! > {WorkDir}/qa-app.pid)",
                    TimeSpan.FromSeconds(15), ct);
                if (launch.ExitCode != 0)
                {
                    return false;
                }
                var until = DateTimeOffset.UtcNow
                    + TimeSpan.FromSeconds(opts.AppReadySeconds);
                while (DateTimeOffset.UtcNow < until)
                {
                    ct.ThrowIfCancellationRequested();
                    var ready = await ExecAsync(sandbox,
                        $"curl -sf -o /dev/null -m 5 '{readyUrl}'",
                        TimeSpan.FromSeconds(10), ct);
                    if (ready.ExitCode == 0)
                    {
                        return true;
                    }
                    var alive = await ExecAsync(sandbox,
                        $"kill -0 $(cat {WorkDir}/qa-app.pid) 2>/dev/null",
                        TimeSpan.FromSeconds(10), ct);
                    if (alive.ExitCode != 0)
                    {
                        break; // processo morreu — não queima o budget
                    }
                    await Task.Delay(2000, ct);
                }
                var tail = await ExecAsync(sandbox,
                    $"tail -c 2000 {WorkDir}/qa-app.log 2>/dev/null",
                    TimeSpan.FromSeconds(10), ct);
                var log = tail.StdOut.Trim();
                recorder.Note(
                    $"app not ready on {readyUrl}: "
                    + SecretRedactor.Redact(
                        log[..Math.Min(300, log.Length)]));
                return false;
            });
    }

    /// <summary>
    /// Exercise: executa o plano — batches browser contíguos ao driver
    /// (uma sessão = um vídeo), exec individual ao sandbox; bugfix tenta
    /// reproduce-on-base antes quando viável. Caps: MaxSteps + deadline.
    /// </summary>
    private async Task ExerciseAsync( // NOSONAR S107 — contexto da fase Exercise (mesmo critério dos runners SPEC-140/141)
        QaRunRecord run, QaPlan plan, StepRecorder recorder,
        Domain.Sandbox.Sandbox sandbox, string? baseUrl, DateTimeOffset deadline,
        QaOptions opts, Guid artifactNs, CancellationToken ct)
    {
        if (CanReproduce(opts, plan, run, baseUrl))
        {
            await ReproduceOnBaseAsync(
                run, plan, plan.Reproduction!, recorder, sandbox,
                run.BaseSha!, opts, ct);
        }

        var driver = plan.Steps.Any(s => s.Kind == Browser)
            && opts.BrowserEnabled && baseUrl is not null
            ? _driverFactory.Create(sandbox)
            : null;

        var i = 0;
        while (i < plan.Steps.Count)
        {
            var step = plan.Steps[i];
            if (DateTimeOffset.UtcNow >= deadline)
            {
                // Cap de minutos — bail honesto: restantes skipped.
                await recorder.SkipRemainingAsync(
                    plan.Steps.Skip(i), "max minutes reached", ct);
                break;
            }

            if (step.Kind != Browser)
            {
                await RunExecStepAsync(
                    recorder, sandbox, step, opts, ct);
                i++;
                continue;
            }

            var batch = plan.Steps.Skip(i)
                .TakeWhile(s => s.Kind == Browser).ToList();
            i += batch.Count;
            if (driver is not null && baseUrl is not null)
            {
                await RunBrowserBatchAsync(
                    recorder, driver, baseUrl, batch, artifactNs, ct);
            }
            else
            {
                await SkipBrowserBatchAsync(recorder, batch, baseUrl, ct);
            }
        }
    }

    /// <summary>Guard do reproduce-on-base (bugfix + base + start + repro).</summary>
    private static bool CanReproduce(
        QaOptions opts, QaPlan plan, QaRunRecord run, string? baseUrl) =>
        opts.ReproductionEnabled
        && plan.Classification == QaClassifications.Bugfix
        && plan.Reproduction is not null
        && run.BaseSha is { Length: > 0 }
        && baseUrl is not null;

    /// <summary>Passos browser sem driver/url → skipped (bail honesto).</summary>
    private static async Task SkipBrowserBatchAsync(
        StepRecorder recorder, IReadOnlyList<QaPlanStep> batch,
        string? baseUrl, CancellationToken ct)
    {
        foreach (var s in batch)
        {
            await recorder.RecordAsync(Browser,
                s.Url ?? s.Selector ?? s.Action,
                QaStepOutcomes.Skipped,
                baseUrl is null
                    ? "no app url (no start command in plan)"
                    : "browser driver disabled",
                null, null, ct);
        }
    }

    /// <summary>Um passo exec (CLI/API) com asserção <c>expect</c>.</summary>
    private async Task RunExecStepAsync(
        StepRecorder recorder, Domain.Sandbox.Sandbox sandbox, QaPlanStep step,
        QaOptions opts, CancellationToken ct,
        string action = "exec")
    {
        var timeout = TimeSpan.FromSeconds(
            step.TimeoutSeconds ?? opts.StepTimeoutSeconds);
        var sw = Stopwatch.StartNew();
        var r = await ExecAsync(sandbox,
            $"cd {RepoDir} && {step.Command}", timeout, ct);
        sw.Stop();
        var outcome = QaStepOutcomes.Ok;
        string? detail;
        if (r.TimedOut)
        {
            outcome = QaStepOutcomes.Fail;
            detail = "step timed out";
        }
        else
        {
            var err = CheckExpect(step.Expect, r);
            if (err is not null)
            {
                outcome = QaStepOutcomes.Fail;
            }
            var text = (r.StdOut + r.StdErr).Trim();
            detail = $"exit {r.ExitCode}"
                + (err is null ? "" : $" — {err}")
                + (text.Length == 0 ? ""
                    : " — " + SecretRedactor.Redact(
                        text[..Math.Min(300, text.Length)]));
        }
        await recorder.RecordAsync(action, step.Command, outcome,
            detail, null, sw.ElapsedMilliseconds, ct);
    }

    /// <summary>
    /// Batch de passos browser contíguos (uma sessão = um vídeo):
    /// driver → outcomes + screenshots → artifacts. Driver indisponível
    /// → todos os passos do batch skipped (não conta como falha).
    /// </summary>
    private async Task RunBrowserBatchAsync(
        StepRecorder recorder, IQaBrowserDriver driver, string baseUrl,
        IReadOnlyList<QaPlanStep> batch, Guid artifactNs,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = await driver.RunStepsAsync(baseUrl, batch, ct);
        sw.Stop();
        if (!result.Executed)
        {
            foreach (var s in batch)
            {
                await recorder.RecordAsync(Browser,
                    s.Url ?? s.Selector ?? s.Action,
                    QaStepOutcomes.Skipped,
                    result.Error ?? "browser driver unavailable",
                    null, null, ct);
            }
            return;
        }

        for (var i = 0; i < batch.Count; i++)
        {
            var s = batch[i];
            var r = i < result.Steps.Count
                ? result.Steps[i]
                : new QaBrowserStepResult(false, "not executed", null, null);
            string? artifactRef = null;
            if (r.Screenshot is { Length: > 0 } png)
            {
                var name =
                    $"qa-{recorder.NextNumber}-{s.Name ?? s.Action}.png";
                artifactRef = await _artifacts.PutAsync(
                    artifactNs, Sanitize(name), png, ct);
            }
            await recorder.RecordAsync(Browser,
                $"{s.Action} {s.Url ?? s.Selector ?? s.Name}".Trim(),
                r.Ok ? QaStepOutcomes.Ok : QaStepOutcomes.Fail,
                r.Detail is null
                    ? null
                    : SecretRedactor.Redact(
                        r.Detail[..Math.Min(300, r.Detail.Length)]),
                artifactRef, sw.ElapsedMilliseconds, ct);
        }

        // Vídeo da sessão — um artifact por batch (SPEC-142 RF-005).
        if (result.Video is { Length: > 0 } webm)
        {
            var videoRef = await _artifacts.PutAsync(
                artifactNs, $"qa-{recorder.NextNumber}-session.webm",
                webm, ct);
            await recorder.RecordAsync("video", "browser session",
                QaStepOutcomes.Ok, null, videoRef,
                sw.ElapsedMilliseconds, ct);
        }
    }

    /// <summary>
    /// Bugfix reproduce-on-base: checkout base → app start → repro
    /// command (indicador = bug presente) → volta ao head → re-start.
    /// Falha de infra no base vira nota e segue — verificação no PR
    /// ainda acontece (bail honesto, nunca FAIL por infra).
    /// </summary>
    private async Task ReproduceOnBaseAsync( // NOSONAR S107 — contexto da fase (mesmo critério dos runners SPEC-140/141)
        QaRunRecord run, QaPlan plan, QaPlanReproduction repro,
        StepRecorder recorder, Domain.Sandbox.Sandbox sandbox, string baseSha,
        QaOptions opts, CancellationToken ct)
    {
        var switched = await TimeAsync(recorder, "repro",
            $"checkout base {baseSha[..Math.Min(7, baseSha.Length)]}",
            async () =>
            {
                var r = await ExecAsync(sandbox,
                    $"git -C {RepoDir} fetch --depth 50 origin {baseSha} "
                    + $"2>/dev/null; git -C {RepoDir} checkout --detach "
                    + $"{baseSha} 2>/dev/null",
                    TimeSpan.FromSeconds(120), ct);
                return r.ExitCode == 0;
            });
        if (!switched || plan.Start is null)
        {
            recorder.Note("reproduction on base not feasible");
            return;
        }

        if (await StartAppAsync(
                recorder, sandbox, plan.Start, opts, ct,
                action: "app_start_base"))
        {
            await RunExecStepAsync(
                recorder, sandbox,
                new QaPlanStep("exec", "reproduce on base", null,
                    repro.Command, null, null, null,
                    Invert(repro.Indicator), null),
                opts, ct, action: "repro");
        }

        // Volta ao head do PR — o exercício real roda nele.
        await ExecAsync(sandbox,
            $"git -C {RepoDir} checkout --detach {run.HeadSha}",
            TimeSpan.FromSeconds(120), ct);
        await ExecAsync(sandbox,
            $"kill $(cat {WorkDir}/qa-app.pid) 2>/dev/null; sleep 1",
            TimeSpan.FromSeconds(15), ct);
        await StartAppAsync(
            recorder, sandbox, plan.Start, opts, ct);
    }

    /// <summary>
    /// Indicador de bug → expect do passo repro: "bug presente" é o
    /// outcome <b>ok</b> do passo (ele valida que o indicador bateu);
    /// sem indicador, exit≠0 no base indica o bug.
    /// </summary>
    private static string? Invert(string? indicator) =>
        indicator is null ? "exit:1" : indicator;

    // ── Report / finish ────────────────────────────────────────────

    /// <summary>
    /// Verdict honesto: qualquer fail de teste → FAIL; sem fail mas com
    /// passos não executados (skipped/infra) → PARTIAL; tudo ok → PASS.
    /// </summary>
    internal static string Verdict(IReadOnlyList<QaStepRecord> steps)
    {
        var test = steps.Where(
            s => s.Action is "exec" or Browser or "repro").ToList();
        if (test.Any(s => s.Outcome is QaStepOutcomes.Fail))
        {
            return QaVerdicts.Fail;
        }
        if (test.Count == 0
            || test.Any(s => s.Outcome == QaStepOutcomes.Skipped)
            || steps.Any(s => s.Outcome == QaStepOutcomes.InfraFail))
        {
            return QaVerdicts.Partial;
        }
        return QaVerdicts.Pass;
    }

    /// <summary>
    /// Report: verdict + evidência → comentário no PR + status
    /// <c>sandcastle/qa</c> + stats/audit/metering. Nunca edita a
    /// branch — apenas comenta/status.
    /// </summary>
    private async Task FinishAsync(
        QaRunRecord run, ScmConnection connection,
        IScmProvider provider, StepRecorder recorder,
        string verdict, string? finishError, CancellationToken ct)
    {
        run.Status = QaRunStatuses.Completed;
        run.Verdict = verdict;
        run.Error = finishError;
        run.FinishedAt = DateTimeOffset.UtcNow;

        var steps = recorder.Steps;
        var evidence = steps
            .Where(s => s.ArtifactRef is not null)
            .Select(s => (
                s.Action == "video"
                    ? $"qa-{s.Number}-session.webm"
                    : $"qa-{s.Number}.png",
                ArtifactUrl(run, s.ArtifactRef!)))
            .ToList();
        try
        {
            await provider.CreateIssueCommentAsync(
                connection, run.Repository, run.PrNumber,
                QaFormatter.Report(
                    run, steps, evidence,
                    _options.Value.InlineScreenshots), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "qa run {Id} comment failed", run.Id);
        }
        var (state, description) = QaFormatter.CommitStatus(
            connection.EffectiveQaPassCondition, verdict,
            runFailed: false);
        try
        {
            await provider.SetCommitStatusAsync(
                connection, run.Repository, run.HeadSha,
                state, QaDefaults.StatusContext, null, description, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "qa run {Id} status failed", run.Id);
        }

        var ok = steps.Count(s => s.Outcome == QaStepOutcomes.Ok);
        var failed = steps.Count(s => s.Outcome == QaStepOutcomes.Fail);
        var skipped = steps.Count(
            s => s.Outcome == QaStepOutcomes.Skipped);
        run.StatsJson = JsonSerializer.Serialize(new
        {
            verdict,
            ok,
            failed,
            skipped,
            artifacts = evidence.Count,
        });
        await _db.SaveChangesAsync(ct);
        await AuditAsync(run, "scm.qa_completed",
            new { verdict, ok, failed, skipped }, ct);
    }

    /// <summary>Falha de pipeline — run failed, status error.</summary>
    private async Task FailAsync(QaRunRecord run, string error)
    {
        run.Status = QaRunStatuses.Failed;
        run.Error = SecretRedactor.Redact(
            error[..Math.Min(500, error.Length)]);
        run.FinishedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(CancellationToken.None);

        var connection = await _db.ScmConnections.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                c => c.Id == run.ConnectionId, CancellationToken.None);
        var provider = connection is null
            ? null : _providers.Get(
                ScmConnectionService.ToModel(connection).Kind);
        if (connection is not null && provider is not null)
        {
            var (state, description) = QaFormatter.CommitStatus(
                ScmConnectionService.ToModel(connection)
                    .EffectiveQaPassCondition, null, runFailed: true);
            try
            {
                await provider.SetCommitStatusAsync(
                    ScmConnectionService.ToModel(connection),
                    run.Repository, run.HeadSha,
                    state, QaDefaults.StatusContext, null,
                    description, CancellationToken.None);
            }
            catch (Exception)
            {
                // status advisory — falha na postagem não propaga
            }
        }
        await AuditAsync(run, "scm.qa_failed",
            new { error = run.Error }, CancellationToken.None);
    }

    // ── Helpers ────────────────────────────────────────────────────

    /// <summary>Link auth'd do artifact (rota do próprio run).</summary>
    private string ArtifactUrl(QaRunRecord run, string artifactRef)
    {
        var path = $"/api/v1/qa/runs/{run.Id}/artifacts/"
            + Uri.EscapeDataString(artifactRef);
        var b = _options.Value.PublicBaseUrl;
        return string.IsNullOrEmpty(b) ? path : b.TrimEnd('/') + path;
    }

    /// <summary>Exec curto no sandbox (stdout/stderr capturados).</summary>
    private Task<CommandResult> ExecAsync(
        Domain.Sandbox.Sandbox sandbox, string command, TimeSpan timeout,
        CancellationToken ct) =>
        _executor.ExecAsync(sandbox, command, timeout, ct);

    /// <summary>Executa uma ação de infra cronometrada → qa_step.</summary>
    private static async Task<bool> TimeAsync(
        StepRecorder recorder, string action, string? target,
        Func<Task<bool>> work)
    {
        var sw = Stopwatch.StartNew();
        var ok = await work();
        sw.Stop();
        await recorder.RecordAsync(action, target,
            ok ? QaStepOutcomes.Ok : QaStepOutcomes.InfraFail,
            null, null, sw.ElapsedMilliseconds, CancellationToken.None);
        return ok;
    }

    /// <summary>Audit event <c>scm.qa_*</c> com atribuição do run.</summary>
    private Task AuditAsync(
        QaRunRecord run, string kind, object? data,
        CancellationToken ct) =>
        _audit.RecordAsync(kind,
            actorUserId: run.RequestedByUserId,
            orgId: run.OrgId,
            targetType: "qa_run", targetId: run.Id.ToString(),
            metadata: data, cancellationToken: ct);

    /// <summary>Nome de artifact seguro (store já saneia — belt).</summary>
    private static string Sanitize(string name) =>
        string.Concat(name.Select(c =>
            char.IsLetterOrDigit(c) || c is '.' or '-' or '_'
                ? c : '-'));

    /// <summary>
    /// Asserção <c>expect</c> de um passo exec — <c>exit:N</c>,
    /// <c>contains:</c>, <c>not_contains:</c>, <c>status:NNN</c>.
    /// </summary>
    internal static string? CheckExpect(
        string? expect, CommandResult r)
    {
        var combined = (r.StdOut ?? "") + (r.StdErr ?? "");
        var i = expect?.IndexOf(':') ?? -1;
        var op = i < 0 ? expect : expect![..i];
        var arg = i < 0 ? "" : expect![(i + 1)..];
        var (passed, error) = op switch
        {
            "exit" => (r.ExitCode.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
                    == arg.Trim(),
                $"exit {r.ExitCode} != {arg}"),
            "status" => (combined.Contains(arg.Trim(),
                    StringComparison.Ordinal),
                $"status {arg} not in output"),
            "contains" => (combined.Contains(arg,
                    StringComparison.Ordinal),
                $"output missing \"{arg}\""),
            "not_contains" => (!combined.Contains(arg,
                    StringComparison.Ordinal),
                $"output contains forbidden \"{arg}\""),
            _ => (r.ExitCode == 0, $"exit {r.ExitCode} != 0"),
        };
        return passed ? null : error;
    }
}

/// <summary>
/// SPEC-142 RF-003 — gravador de passos: numera, persiste e mantém a
/// lista em-memória para o veredicto/relatório (uma instância por run).
/// </summary>
internal sealed class StepRecorder(AppDbContext db, QaRunRecord run)
{
    private readonly List<QaStepRecord> _steps = [];
    private int _number;
    private string? _note;

    /// <summary>Passos já gravados (ordem de execução).</summary>
    public IReadOnlyList<QaStepRecord> Steps => _steps;

    /// <summary>Próximo número de passo (nomes de artifacts).</summary>
    public int NextNumber => _number + 1;

    /// <summary>Nota de bail-out acumulada (vai para o erro do run).</summary>
    public void Note(string note) =>
        _note = _note is null ? note : _note + "; " + note;

    /// <summary>Nota acumulada (null quando nada anotado).</summary>
    public string? BailNote => _note;

    /// <summary>Registra um passo e persiste.</summary>
    public async Task<QaStepRecord> RecordAsync(
        string action, string? target, string outcome,
        string? detail, string? artifactRef, long? durationMs,
        CancellationToken ct)
    {
        var text = detail ?? _note;
        var step = new QaStepRecord
        {
            Id = Guid.NewGuid(),
            OrgId = run.OrgId,
            QaRunId = run.Id,
            Number = ++_number,
            Action = action,
            Target = target is null
                ? null : target[..Math.Min(1000, target.Length)],
            Outcome = outcome,
            Detail = text is null
                ? null : text[..Math.Min(2000, text.Length)],
            ArtifactRef = artifactRef,
            DurationMs = durationMs,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _steps.Add(step);
        db.QaSteps.Add(step);
        await db.SaveChangesAsync(ct);
        return step;
    }

    /// <summary>
    /// Marca passos restantes como <c>skipped</c> (cap/bail-out) —
    /// honest PARTIAL: nunca conta como executados.
    /// </summary>
    public async Task SkipRemainingAsync(
        IEnumerable<QaPlanStep> remaining, string reason,
        CancellationToken ct)
    {
        foreach (var s in remaining)
        {
            await RecordAsync(
                s.Kind == QaRunner.Browser ? QaRunner.Browser : "exec",
                s.Command ?? s.Url ?? s.Selector ?? s.Action,
                QaStepOutcomes.Skipped, reason, null, null, ct);
        }
    }
}