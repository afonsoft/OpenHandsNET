using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Coordination;
using Sandcastle.Application.Sandbox;
using Sandcastle.Domain.Conversations;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Sandbox;

namespace Sandcastle.Infrastructure.Conversations;

/// <summary>
/// Background worker materializing conversations: drains the start-task queue,
/// drives each through <c>WAITING_FOR_SANDBOX → PREPARING_REPOSITORY →
/// SETTING_UP_SKILLS → READY</c> (or <c>ERROR</c>), creating the sandbox and
/// cloning the selected repository.
/// </summary>
public sealed class ConversationStartWorker(
    StartTaskService tasks,
    IServiceScopeFactory scopes,
    IDistributedLock distributedLock,
    ILogger<ConversationStartWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReconcileOrphansAsync(stoppingToken);
        await foreach (var taskId in tasks.Queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(taskId, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Start task {TaskId} failed", taskId);
                await FailAsync(taskId, ex.Message, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Reconcile de boot (#434 — mesmo padrão dos run workers SPEC-140/141/
    /// 142/145): a fila é in-process, então no start toda task
    /// <c>Working</c> (queued) é órfã do processo anterior e volta ao
    /// canal. Tasks interrompidas a caminho do READY
    /// (<c>WaitingForSandbox|PreparingRepository|SettingUpEnvironment|
    /// SettingUpSkills</c>) viram <c>Error</c> com
    /// <c>worker_restart</c> — re-executar cego podia provisionar um
    /// segundo sandbox para a mesma conversa. Como podem ter sandbox
    /// parcialmente provisionado, o cleanup é best-effort via
    /// <see cref="ISandboxService.DeleteAsync"/> (mesmo caminho do
    /// delete de conversa): o handle é <c>task.SandboxId</c> e qualquer
    /// record de sandbox bound à conversa (cobre a janela de crash entre
    /// <c>StartAsync</c> persistir o record e a task gravar o id). Um
    /// sandbox saudável nunca é tocado — enquanto uma start-task está
    /// em curso todo sandbox bound à conversa nasceu daquela task
    /// (o profile switch deleta o anterior antes de re-enfileirar).
    /// </summary>
    private async Task ReconcileOrphansAsync(CancellationToken ct)
    {
        try
        {
            // Um reconcile por vez entre réplicas (padrão ScanWorker).
            await using var guard = await distributedLock.TryAcquireAsync(
                "starttask-orphan-reconcile", TimeSpan.FromMinutes(2), ct);
            if (guard is null)
            {
                return;
            }

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider
                .GetRequiredService<AppDbContext>();
            var sandboxService = scope.ServiceProvider
                .GetRequiredService<ISandboxService>();
            var now = DateTimeOffset.UtcNow;

            var interrupted = await db.StartTasks.IgnoreQueryFilters()
                .Where(t => t.Status != nameof(StartTaskStatus.Working)
                    && t.Status != nameof(StartTaskStatus.Ready)
                    && t.Status != nameof(StartTaskStatus.Error))
                .ToListAsync(ct);
            foreach (var task in interrupted)
            {
                task.Status = nameof(StartTaskStatus.Error);
                task.Detail = "worker_restart";
                task.UpdatedAt = now;
                if (task.AppConversationId is not { } convId)
                {
                    continue;
                }

                var conv = await db.Conversations.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(c => c.Id == convId, ct);
                if (conv is not null
                    && conv.ExecutionStatus
                        != nameof(ConversationExecutionStatus.Archived))
                {
                    conv.ExecutionStatus =
                        nameof(ConversationExecutionStatus.Error);
                    conv.UpdatedAt = now;
                }

                await CleanupPartialSandboxesAsync(
                    db, sandboxService, scope.ServiceProvider,
                    task, convId, conv?.OrgId, ct);
            }
            if (interrupted.Count > 0)
            {
                await db.SaveChangesAsync(ct);
            }

            var queued = await db.StartTasks.IgnoreQueryFilters()
                .Where(t => t.Status == nameof(StartTaskStatus.Working))
                .Select(t => t.Id)
                .ToListAsync(ct);
            foreach (var id in queued)
            {
                await tasks.EnqueueAsync(id, ct);
            }

            if (queued.Count + interrupted.Count > 0)
            {
                logger.LogInformation(
                    "start-task orphan reconcile: {Queued} requeued, "
                    + "{Failed} failed (worker_restart)",
                    queued.Count, interrupted.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reconcile falhou não pode derrubar o drain da fila.
            logger.LogWarning(ex, "start-task orphan reconcile failed");
        }
    }

    /// <summary>
    /// Best-effort: remove o sandbox parcial da task — o id persistido em
    /// <paramref name="task"/> + todo record bound à conversa (janela de
    /// crash entre o insert do record e o save de
    /// <c>task.SandboxId</c>). Falha vira warning + audit
    /// <c>starttask.orphan_cleanup_failed</c> — o task-fail acontece
    /// mesmo assim (o sandbox residual fica para o
    /// <see cref="ISandboxService.SweepOrphansAsync"/>).
    /// </summary>
    private async Task CleanupPartialSandboxesAsync(
        AppDbContext db, ISandboxService sandboxService,
        IServiceProvider services, StartTaskRecord task,
        Guid conversationId, Guid? orgId, CancellationToken ct)
    {
        var ids = await db.Sandboxes.IgnoreQueryFilters()
            .Where(s => s.ConversationId == conversationId)
            .Select(s => s.Id)
            .ToListAsync(ct);
        if (task.SandboxId is { } handle && !ids.Contains(handle))
        {
            ids.Add(handle);
        }
        if (ids.Count == 0)
        {
            return;
        }

        var audit = services
            .GetService<Application.Identity.IAuditService>();
        foreach (var sandboxId in ids)
        {
            try
            {
                await sandboxService.DeleteAsync(sandboxId, ct);
                logger.LogInformation(
                    "start-task {TaskId} orphan cleanup removed sandbox "
                    + "{SandboxId}", task.Id, sandboxId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "start-task {TaskId} orphan cleanup failed for "
                    + "sandbox {SandboxId}", task.Id, sandboxId);
                try
                {
                    if (audit is not null)
                    {
                        await audit.RecordAsync(
                            "starttask.orphan_cleanup_failed",
                            orgId: orgId,
                            targetType: "start_task",
                            targetId: task.Id.ToString(),
                            metadata: new
                            {
                                sandbox_id = sandboxId,
                                conversation_id = conversationId,
                                error = ex.Message[..Math.Min(
                                    200, ex.Message.Length)],
                            },
                            cancellationToken: CancellationToken.None);
                    }
                }
                catch (Exception auditEx)
                    when (auditEx is not OperationCanceledException)
                {
                    logger.LogWarning(auditEx,
                        "starttask.orphan_cleanup_failed audit failed");
                }
            }
        }
    }

    private async Task ProcessAsync(Guid taskId, CancellationToken ct)
    {
        using var span = Telemetry.ScTelemetry.StartSpan("start_task");
        span?.SetTag("task_id", taskId.ToString());
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sandboxService = scope.ServiceProvider.GetRequiredService<ISandboxService>();
        var workspaces = scope.ServiceProvider.GetRequiredService<Sandcastle.Sandboxing.WorkspaceProvisioner>();

        var task = await db.StartTasks.FindAsync([taskId], ct);
        if (task?.AppConversationId is null)
        {
            return;
        }

        var conv = await db.Conversations.FindAsync([task.AppConversationId.Value], ct);
        if (conv is null)
        {
            return;
        }

        // Claim atômico Working→WaitingForSandbox (#434): o reconcile de
        // boot pode re-enfileirar uma task que outra réplica também
        // enxergou no banco (fila in-process por nó) — só o primeiro
        // claim provisiona; os demais saem sem tocar em sandbox/plano.
        var claimed = await db.StartTasks
            .Where(t => t.Id == taskId
                && t.Status == nameof(StartTaskStatus.Working))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status,
                    nameof(StartTaskStatus.WaitingForSandbox))
                .SetProperty(t => t.UpdatedAt, DateTimeOffset.UtcNow), ct);
        if (claimed == 0)
        {
            return;
        }
        task.Status = nameof(StartTaskStatus.WaitingForSandbox);

        // SPEC-20 RF-003: enforcement de plano antes de criar o sandbox —
        // concurrent runs, tokens/mês e sandbox-seconds/mês.
        var planError = await scope.ServiceProvider
            .GetRequiredService<Application.Identity.IPlanEnforcementService>()
            .CheckRunStartAsync(conv.OrgId,
                Guid.TryParse(conv.CreatedByUserId, out var uid) ? uid : null,
                ct);
        if (planError is not null)
        {
            await FailAsync(taskId,
                $"plan_limit_exceeded:{planError.LimitKey}"
                + $" ({planError.Used}/{planError.Limit})", ct);
            return;
        }

        var sandbox = await sandboxService.StartAsync(
            Guid.Empty /* default spec */, conv.Id, ct);
        task.SandboxId = sandbox.Id;
        conv.SandboxId = sandbox.Id;
        conv.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // SPEC-15 RF-004: gh/git auth persistente no sandbox do dono da
        // conversa — roda com e sem selected_repository ("inicializar uma
        // conversa"); no-op quando não há token cadastrado.
        var gitService = scope.ServiceProvider
            .GetRequiredService<Application.Git.IGitService>();
        var sbForProvision = await sandboxService.GetAsync(sandbox.Id, ct);
        if (sbForProvision is not null)
        {
            await gitService.ProvisionGhAuthAsync(
                sbForProvision, conv.CreatedByUserId, ct);
        }

        await AdvanceAsync(db, task, StartTaskStatus.PreparingRepository, ct);
        await CloneRepositoryAsync(
            scope, workspaces, sbForProvision, sandbox, conv, ct);

        // SPEC-150 RF-001: handoff — aplica o patch do working tree local
        // sobre o base_sha clonado e reporta stats no stream.
        await ApplyHandoffAsync(scope, db, sbForProvision, sandbox, conv, ct);

        // SPEC-17 RF-004 + SPEC-25 RF-001: blueprint + descoberta de contexto
        // — falha nunca bloqueia READY.
        var sbForBlueprint = await sandboxService.GetAsync(sandbox.Id, ct);
        if (sbForBlueprint is not null)
        {
            await AdvanceAsync(
                db, task, StartTaskStatus.SettingUpEnvironment, ct);
            var blueprint = scope.ServiceProvider
                .GetRequiredService<Sandbox.BlueprintService>();
            var executor = scope.ServiceProvider
                .GetRequiredService<ISandboxExecutor>();
            var spec = await blueprint.ResolveAsync(
                conv.Id, conv.OrgId, ct);
            await blueprint.RunAsync(
                sbForBlueprint, conv.Id, spec, executor, ct);
            await RunDiscoveryAsync(scope, conv, sbForBlueprint, ct);
        }

        await AdvanceAsync(db, task, StartTaskStatus.SettingUpSkills, ct);
        await ResolveSkillsAsync(scope, conv, ct);

        conv.ExecutionStatus = ConversationExecutionStatus.Running.ToString();
        conv.UpdatedAt = DateTimeOffset.UtcNow;
        task.Status = StartTaskStatus.Ready.ToString();
        task.UpdatedAt = DateTimeOffset.UtcNow;
        var kickoff = conv.PendingKickoff;
        conv.PendingKickoff = false;
        await db.SaveChangesAsync(ct);
        Telemetry.ScTelemetry.ConversationsStarted.Inc();
        logger.LogInformation(
            "Conversation {ConversationId} READY (sandbox {SandboxId})",
            conv.Id, sandbox.Id);

        // initial_message.run → acorda o agente (SPEC-09).
        if (kickoff)
        {
            var host = scope.ServiceProvider
                .GetRequiredService<Sandcastle.Application.Agent.IAgentRunnerHost>();
            await host.NotifyMessageAsync(conv.Id, ct);
        }
    }

    private async Task CloneRepositoryAsync(
        IServiceScope scope, Sandcastle.Sandboxing.WorkspaceProvisioner workspaces,
        Domain.Sandbox.Sandbox? sbForProvision, Domain.Sandbox.Sandbox sandbox,
        ConversationRecord conv, CancellationToken ct)
    {
        var sandboxService = scope.ServiceProvider
            .GetRequiredService<ISandboxService>();
        var gitService = scope.ServiceProvider
            .GetRequiredService<Application.Git.IGitService>();
        // SPEC-146: lista ordenada (primário no índice 0) — RepositoriesJson
        // com fallback pro legado SelectedRepository.
        var repos = conv.GetRepositories();
        if (repos.Count == 0 || string.IsNullOrEmpty(conv.SelectedRepository))
        {
            return;
        }
        var gitOpt = scope.ServiceProvider
            .GetRequiredService<IOptions<Git.GitOptions>>().Value;
        var branch = gitOpt.WorkBranch
            .Replace("{id}", conv.Id.ToString("N")[..8],
                StringComparison.Ordinal);

        if (repos.Count == 1)
        {
            // Single-repo — fluxo legado inalterado (clone em /workspace/repo).
            if (gitOpt.CloneMode.Equals("host", StringComparison.OrdinalIgnoreCase))
            {
                // Host mode: token do dono (ou system) via http.extraHeader.
                var creds = scope.ServiceProvider.GetRequiredService<
                    Git.GitCredentialProvider>();
                var host = TryRepoHost(conv.SelectedRepository);
                var token = host is null ? null
                    : await creds.ResolveTokenAsync(host, conv.CreatedByUserId, ct);
                await workspaces.CloneRepositoryAsync(
                    conv.Id, conv.SelectedRepository, branch, token, ct);
                return;
            }
            // Default: clone dentro do container (isolado; token via askpass).
            var sbSingle = sbForProvision
                ?? await sandboxService.GetAsync(sandbox.Id, ct);
            if (sbSingle is not null)
            {
                await gitService.CloneAsync(
                    sbSingle, conv.SelectedRepository, branch,
                    conv.CreatedByUserId, ct);
            }
            return;
        }

        // SPEC-146 RF-002: multi-repo — clone paralelo em
        // /workspace/repos/{short}, /workspace/repo → symlink do primário;
        // credencial resolvida por repo (conexão SCM → fallback user cred).
        var credResolver = scope.ServiceProvider
            .GetRequiredService<Scm.RepoCloneCredentialResolver>();
        IReadOnlyList<MultiRepoCloner.RepoCloneOutcome> outcomes;
        var hostMode = gitOpt.CloneMode.Equals(
            "host", StringComparison.OrdinalIgnoreCase);
        var dirs = Application.Conversations.MultiRepoLayout.DirNames(repos);
        if (hostMode)
        {
            outcomes = await MultiRepoCloner.CloneAsync(repos,
                async (repo, path, c) =>
                {
                    var token = await credResolver.ResolveAsync(
                        repo, conv.OrgId, conv.CreatedByUserId, c);
                    // "/workspace/repos/{dir}" → "repos/{dir}" no host.
                    await workspaces.CloneRepositoryToAsync(
                        conv.Id,
                        path["/workspace/".Length..], repo, branch,
                        token, c);
                }, logger, ct);
            workspaces.TryCreatePrimaryLink(conv.Id, $"repos/{dirs[0]}");
        }
        else
        {
            var sbDomain = sbForProvision
                ?? await sandboxService.GetAsync(sandbox.Id, ct);
            if (sbDomain is null)
            {
                return;
            }
            outcomes = await MultiRepoCloner.CloneAsync(repos,
                async (repo, path, c) =>
                {
                    var token = await credResolver.ResolveAsync(
                        repo, conv.OrgId, conv.CreatedByUserId, c);
                    await gitService.CloneAsync(
                        sbDomain, repo, branch, conv.CreatedByUserId, c,
                        repoPath: path, credentialToken: token);
                }, logger, ct);
            // /workspace/repo → primário (compat de tools/scripts legados).
            var executor = scope.ServiceProvider
                .GetRequiredService<ISandboxExecutor>();
            var link = await executor.ExecAsync(sbDomain,
                $"ln -sfnT '{outcomes[0].Path}' " +
                $"'{Application.Conversations.MultiRepoLayout.LegacyRepoPath}'",
                TimeSpan.FromSeconds(15), ct);
            if (link.ExitCode != 0)
            {
                logger.LogWarning(
                    "legacy /workspace/repo symlink failed: {Err}",
                    link.StdErr.Trim());
            }
        }

        // Reporta o resultado dos clones (secundários falhos incluídos) no
        // stream — visível no setup da conversa e no system prompt.
        var eventSvc = scope.ServiceProvider
            .GetRequiredService<Application.Events.IEventService>();
        await eventSvc.AppendAsync(conv.Id,
            new Domain.Events.ConversationStateUpdateEvent
            {
                Key = "repositories",
                Value = System.Text.Json.JsonSerializer.SerializeToElement(
                    MultiRepoCloner.ToStateValue(outcomes),
                    Domain.Events.EventJson.Options),
            }, ct);
    }

    /// <summary>
    /// SPEC-150: quando a conversa nasceu de <c>POST /conversations/handoff</c>
    /// (<see cref="ConversationRecord.HandoffMetaJson"/>), aplica o patch no
    /// sandbox, grava <c>applied_stats</c> e emite o evento
    /// <c>ConversationStateUpdateEvent(key="handoff_applied")</c>.
    /// </summary>
    private async Task ApplyHandoffAsync(
        IServiceScope scope, AppDbContext db,
        Domain.Sandbox.Sandbox? sbForProvision,
        Domain.Sandbox.Sandbox sandbox,
        ConversationRecord conv, CancellationToken ct)
    {
        if (conv.HandoffMetaJson is not { } metaJson)
        {
            return;
        }

        var meta = Handoff.HandoffService.Deserialize<Application
            .Conversations.HandoffMeta>(metaJson);
        if (meta?.DiffPatch is not { Length: > 0 })
        {
            return;
        }

        try
        {
            var gitOpt = scope.ServiceProvider
                .GetRequiredService<IOptions<Git.GitOptions>>().Value;
            var workBranch = gitOpt.WorkBranch
                .Replace("{id}", conv.Id.ToString("N")[..8],
                    StringComparison.Ordinal);
            var sb = sbForProvision
                ?? await scope.ServiceProvider
                    .GetRequiredService<ISandboxService>()
                    .GetAsync(sandbox.Id, ct) ?? sandbox;
            var stats = await scope.ServiceProvider
                .GetRequiredService<Application.Conversations.IHandoffApplier>()
                .ApplyAsync(sb, meta, workBranch, ct);

            conv.HandoffMetaJson = Handoff.HandoffService.Serialize(
                meta with { AppliedStats = stats });
            conv.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            await scope.ServiceProvider
                .GetRequiredService<Application.Events.IEventService>()
                .AppendAsync(conv.Id,
                    new Domain.Events.ConversationStateUpdateEvent
                    {
                        Key = "handoff_applied",
                        Value = System.Text.Json.JsonSerializer
                            .SerializeToElement(new
                            {
                                origin = meta.Origin,
                                base_sha = meta.BaseSha,
                                base_branch = meta.BaseBranch,
                                applied = stats.Applied,
                                files = stats.Files,
                                additions = stats.Additions,
                                deletions = stats.Deletions,
                                rejected_files = stats.RejectedFiles,
                                rejected_hunks = stats.RejectedHunks,
                                cli_version = meta.CliVersion,
                                error = stats.Error,
                            }, Domain.Events.EventJson.Options),
                    }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // apply best-effort — READY não depende dele; o erro fica
            // visível nos stats do evento handoff_applied.
            logger.LogWarning(ex,
                "handoff apply falhou na conversa {Id}", conv.Id);
        }
    }

    // SPEC-11 RF-005: resolve + persiste skills da conversa antes de READY.
    private static async Task ResolveSkillsAsync(
        IServiceScope scope, ConversationRecord conv, CancellationToken ct)
    {
        var skillSvc = scope.ServiceProvider
            .GetService<Application.Skills.ISkillService>();
        if (skillSvc is null)
        {
            return;
        }
        // keyword triggers casam contra a última mensagem do usuário
        // já persistida no event stream (initial_message).
        var eventSvc = scope.ServiceProvider
            .GetRequiredService<Application.Events.IEventService>();
        var page = await eventSvc.SearchAsync(conv.Id,
            new Application.Events.EventQuery(Limit: 100), ct);
        var firstMessage = page.Items
            .OfType<Domain.Events.MessageEvent>()
            .Where(m => m.Source == Domain.Events.EventSource.User)
            .Select(m => m.LlmMessage.GetRawText())
            .LastOrDefault();
        var resolved = await skillSvc.ResolveForConversationAsync(
            conv.Id, firstMessage, null,
            conv.CreatedByUserId, conv.OrgId?.ToString(), ct);
        if (skillSvc is Infrastructure.Skills.SkillService concrete)
        {
            await concrete.PinAsync(
                conv.Id, resolved.Select(s => s.Id), ct);
        }
    }

    /// <summary>
    /// SPEC-25 RF-001: roda a descoberta no sandbox e persiste o manifesto
    /// como <c>ConversationStateUpdateEvent(key="repo_context")</c> —
    /// falha nunca bloqueia o start-task.
    /// </summary>
    internal static async Task RunDiscoveryAsync(
        IServiceScope scope, ConversationRecord conv,
        Domain.Sandbox.Sandbox sandbox, CancellationToken ct)
    {
        try
        {
            var discovery = scope.ServiceProvider
                .GetRequiredService<Application.Discovery.IRepoDiscoveryService>();
            var executor = scope.ServiceProvider
                .GetRequiredService<ISandboxExecutor>();
            var eventSvc = scope.ServiceProvider
                .GetRequiredService<Application.Events.IEventService>();
            var manifest = await discovery.DiscoverAsync(
                executor, sandbox, ct);
            await eventSvc.AppendAsync(conv.Id,
                new Domain.Events.ConversationStateUpdateEvent
                {
                    Key = "repo_context",
                    Value = System.Text.Json.JsonSerializer
                        .SerializeToElement(new
                        {
                            instructions_files = manifest.InstructionFiles,
                            skills = manifest.Skills.Select(s => new
                            {
                                name = s.Name,
                                path = s.Path,
                                description = s.Description,
                            }),
                            config = manifest.HasRepoConfig,
                            readme = manifest.ReadmePath,
                        }, Domain.Events.EventJson.Options),
                }, ct);
            // o cache de repo-skills do sandbox pode estar stale — descarta.
            scope.ServiceProvider.GetService<Skills.RepoSkillProvider>()
                ?.Invalidate(sandbox.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // descoberta é best-effort — READY não depende dela.
            var logger = scope.ServiceProvider
                .GetService<ILogger<ConversationStartWorker>>();
            logger?.LogWarning(ex,
                "repo discovery falhou na conversa {Id}", conv.Id);
        }
    }

    /// <summary>Extrai o host de <c>selected_repository</c> (url ou owner/repo).</summary>
    private static string? TryRepoHost(string selectedRepository)
    {
        if (Uri.TryCreate(selectedRepository, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return selectedRepository.Count(c => c == '/') == 1
            && !selectedRepository.StartsWith('/')
            && !selectedRepository.StartsWith("file:", StringComparison.Ordinal)
            ? "github.com"
            : null;
    }

    private static async Task AdvanceAsync(
        AppDbContext db, StartTaskRecord task, StartTaskStatus status,
        CancellationToken ct)
    {
        task.Status = status.ToString();
        task.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task FailAsync(Guid taskId, string detail, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var task = await db.StartTasks.FindAsync([taskId], ct);
        if (task is null)
        {
            return;
        }

        task.Status = StartTaskStatus.Error.ToString();
        task.Detail = detail;
        task.UpdatedAt = DateTimeOffset.UtcNow;
        if (task.AppConversationId is not null)
        {
            var conv = await db.Conversations.FindAsync([task.AppConversationId.Value], ct);
            if (conv is not null)
            {
                conv.ExecutionStatus = ConversationExecutionStatus.Error.ToString();
                conv.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        await db.SaveChangesAsync(ct);
    }
}