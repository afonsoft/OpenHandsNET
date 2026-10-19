using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Sandboxing;

namespace OpenHandsNET.Infrastructure.Sandbox;

/// <summary>
/// SPEC-16 RF-001: schedules a sandbox pause after the agent run ends —
/// <c>Sandbox:IdleSleepSeconds</c> of grace (0 = immediate, -1 = never).
/// A new user message cancels the pending sleep.
/// </summary>
public sealed class SandboxSleepService(
    IServiceScopeFactory scopes,
    IOptions<SandboxOptions> options,
    ILogger<SandboxSleepService> logger)
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _pending = new();

    /// <summary>Schedules (or reschedules) the sleep for a conversation's sandbox.</summary>
    public void ScheduleSleep(Guid conversationId)
    {
        var seconds = options.Value.IdleSleepSeconds;
        if (seconds < 0)
        {
            return; // auto-sleep desligado
        }

        CancelSleep(conversationId);
        var cts = new CancellationTokenSource();
        _pending[conversationId] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                if (seconds > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token);
                }

                await SleepAsync(conversationId, cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Scheduled sleep failed for conversation {ConversationId}",
                    conversationId);
            }
            finally
            {
                _pending.TryRemove(conversationId, out _);
                cts.Dispose();
            }
        });
    }

    /// <summary>Cancels a pending sleep (user activity / wake).</summary>
    public void CancelSleep(Guid conversationId)
    {
        if (_pending.TryRemove(conversationId, out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }
    }

    private async Task SleepAsync(Guid conversationId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Persistence.AppDbContext>();
        var sandboxes = scope.ServiceProvider.GetRequiredService<ISandboxService>();

        var conv = await db.Conversations.FindAsync([conversationId], ct);
        if (conv?.SandboxId is not { } sandboxId)
        {
            return;
        }

        var record = await db.Sandboxes.FindAsync([sandboxId], ct);
        if (record is null || record.Status != Domain.Sandbox.SandboxStatus.Running.ToString())
        {
            return; // já dormindo ou em estado terminal
        }

        // Não dorme com run ativo — o schedule dispara no fim do run, mas uma
        // mensagem tardia pode ter iniciado um novo turno.
        if (conv.ExecutionStatus ==
            nameof(Domain.Conversations.ConversationExecutionStatus.Running))
        {
            return;
        }

        logger.LogInformation(
            "Sandbox {SandboxId} sleeping after run grace (conversation {ConversationId})",
            sandboxId, conversationId);
        await sandboxes.PauseAsync(sandboxId, ct);
    }
}