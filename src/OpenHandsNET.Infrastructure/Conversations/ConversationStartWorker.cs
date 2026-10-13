using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Conversations;
using OpenHandsNET.Infrastructure.Persistence;
using OpenHandsNET.Infrastructure.Sandbox;

namespace OpenHandsNET.Infrastructure.Conversations;

/// <summary>
/// Background worker materializing conversations: drains the start-task queue,
/// drives each through <c>WAITING_FOR_SANDBOX → PREPARING_REPOSITORY →
/// SETTING_UP_SKILLS → READY</c> (or <c>ERROR</c>), creating the sandbox and
/// cloning the selected repository.
/// </summary>
public sealed class ConversationStartWorker(
    StartTaskService tasks,
    IServiceScopeFactory scopes,
    ILogger<ConversationStartWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

    private async Task ProcessAsync(Guid taskId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sandboxService = scope.ServiceProvider.GetRequiredService<ISandboxService>();
        var workspaces = scope.ServiceProvider.GetRequiredService<WorkspaceProvisioner>();

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

        await AdvanceAsync(db, task, StartTaskStatus.WaitingForSandbox, ct);

        var sandbox = await sandboxService.StartAsync(
            Guid.Empty /* default spec */, conv.Id, ct);
        task.SandboxId = sandbox.Id;
        conv.SandboxId = sandbox.Id;
        conv.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await AdvanceAsync(db, task, StartTaskStatus.PreparingRepository, ct);
        if (!string.IsNullOrEmpty(conv.SelectedRepository))
        {
            var gitOpt = scope.ServiceProvider
                .GetRequiredService<IOptions<Git.GitOptions>>().Value;
            var branch = gitOpt.WorkBranch
                .Replace("{id}", conv.Id.ToString("N")[..8],
                    StringComparison.Ordinal);
            if (gitOpt.CloneMode.Equals("host", StringComparison.OrdinalIgnoreCase))
            {
                await workspaces.CloneRepositoryAsync(
                    conv.Id, conv.SelectedRepository, branch, ct);
            }
            else
            {
                // Default: clone dentro do container (isolado; token via askpass).
                var git = scope.ServiceProvider
                    .GetRequiredService<Application.Git.IGitService>();
                var sbDomain = await sandboxService.GetAsync(sandbox.Id, ct);
                if (sbDomain is not null)
                {
                    await git.CloneAsync(
                        sbDomain, conv.SelectedRepository, branch, ct);
                }
            }
        }

        await AdvanceAsync(db, task, StartTaskStatus.SettingUpSkills, ct);
        // SPEC-11 RF-005: resolve + persiste skills da conversa antes de READY.
        {
            var skillSvc = scope.ServiceProvider
                .GetService<Application.Skills.ISkillService>();
            if (skillSvc is not null)
            {
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
        }

        conv.ExecutionStatus = ConversationExecutionStatus.Running.ToString();
        conv.UpdatedAt = DateTimeOffset.UtcNow;
        task.Status = StartTaskStatus.Ready.ToString();
        task.UpdatedAt = DateTimeOffset.UtcNow;
        var kickoff = conv.PendingKickoff;
        conv.PendingKickoff = false;
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Conversation {ConversationId} READY (sandbox {SandboxId})",
            conv.Id, sandbox.Id);

        // initial_message.run → acorda o agente (SPEC-09).
        if (kickoff)
        {
            var host = scope.ServiceProvider
                .GetRequiredService<OpenHandsNET.Application.Agent.IAgentRunnerHost>();
            await host.NotifyMessageAsync(conv.Id, ct);
        }
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
