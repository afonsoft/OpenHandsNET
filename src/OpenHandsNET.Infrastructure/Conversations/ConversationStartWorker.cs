using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
            await workspaces.CloneRepositoryAsync(
                conv.Id, conv.SelectedRepository, ct);
        }

        await AdvanceAsync(db, task, StartTaskStatus.SettingUpSkills, ct);
        // Skills/hooks provisioning lands with SPEC-11; nothing to install yet.

        conv.ExecutionStatus = ConversationExecutionStatus.Running.ToString();
        conv.UpdatedAt = DateTimeOffset.UtcNow;
        task.Status = StartTaskStatus.Ready.ToString();
        task.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Conversation {ConversationId} READY (sandbox {SandboxId})",
            conv.Id, sandbox.Id);
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
