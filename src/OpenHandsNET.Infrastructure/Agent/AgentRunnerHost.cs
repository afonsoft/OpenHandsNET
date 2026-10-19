using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Agent;

/// <summary>
/// Singleton owning one active <see cref="AgentRunner"/> per conversation
/// (SPEC-09 single-writer rule). Runners are spawned lazily on the first
/// runnable message and removed when the loop terminates.
/// </summary>
public sealed class AgentRunnerHost(
    IServiceScopeFactory scopes,
    ILogger<AgentRunnerHost> logger,
    Sandbox.SandboxSleepService sleep) : IAgentRunnerHost
{
    private sealed record RunnerEntry(
        AgentRunner Runner, Task Task, CancellationTokenSource Interrupt);

    private readonly ConcurrentDictionary<Guid, RunnerEntry> _runners = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    /// <inheritdoc />
    public bool IsRunning(Guid conversationId) =>
        _runners.TryGetValue(conversationId, out var e) && !e.Task.IsCompleted;

    /// <inheritdoc />
    public async Task NotifyMessageAsync(Guid conversationId, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // SPEC-16: atividade cancela sleep pendente do sandbox.
            sleep.CancelSleep(conversationId);
            if (_runners.TryGetValue(conversationId, out var e)
                && !e.Task.IsCompleted)
            {
                return; // já ativo — a mensagem entra via pump do stream.
            }

            var scope = scopes.CreateAsyncScope();
            var runner = new AgentRunner(conversationId,
                scope.ServiceProvider,
                scope.ServiceProvider
                    .GetRequiredService<ILogger<AgentRunner>>());
            var interrupt = new CancellationTokenSource();
            var task = Task.Run(async () =>
            {
                try
                {
                    await runner.RunAsync(interrupt.Token);
                }
                catch (OperationCanceledException)
                { // best-effort — falha tolerada
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Agent runner {ConversationId} crashed", conversationId);
                }
                finally
                {
                    _runners.TryRemove(conversationId, out _);
                    await scope.DisposeAsync();
                    // SPEC-16 RF-001: fim do run → agenda sleep com graça.
                    sleep.ScheduleSleep(conversationId);
                }
            }, CancellationToken.None);
            _runners[conversationId] = new RunnerEntry(runner, task, interrupt);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ConfirmActionAsync(
        Guid conversationId, string actionId, bool approved, string? reason,
        CancellationToken ct)
    {
        // Auditoria primeiro (mesmo payload de antes: reject → UserRejectObservation).
        await using var scope = scopes.CreateAsyncScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventService>();
        EventBase e = approved
            ? new MessageEvent
            {
                Source = EventSource.User,
                LlmMessage = JsonSerializer.SerializeToElement(new
                {
                    role = "user",
                    content = new[]
                    {
                        new { type = "text", text = $"[confirmed] action {actionId}" },
                    },
                }, EventJson.Options),
            }
            : new UserRejectObservation
            {
                Source = EventSource.Environment,
                ToolName = "",
                ToolCallId = "",
                RejectionReason = reason ?? "rejected by user",
                ActionId = actionId,
            };
        await events.AppendAsync(conversationId, e, ct);

        if (_runners.TryGetValue(conversationId, out var entry))
        {
            entry.Runner.ResolveConfirmation(actionId, approved);
        }
    }

    /// <inheritdoc />
    public async Task InterruptAsync(Guid conversationId, CancellationToken ct)
    {
        await AppendControlAsync(conversationId, new InterruptEvent(), ct);
        if (_runners.TryGetValue(conversationId, out var entry))
        {
            await entry.Interrupt.CancelAsync();
        }
    }

    /// <inheritdoc />
    public async Task PauseAsync(Guid conversationId, CancellationToken ct)
    {
        await AppendControlAsync(conversationId, new PauseEvent(), ct);
        if (_runners.TryGetValue(conversationId, out var entry))
        {
            entry.Runner.OnEvent(new PauseEvent());
            await entry.Interrupt.CancelAsync();
        }
    }

    /// <inheritdoc />
    public Task ResumeAsync(Guid conversationId, CancellationToken ct) =>
        // SPEC-41 RF-005: a history reconstruída do stream é resume-safe —
        // acordar o host equivale a retomar o turno interrompido.
        NotifyMessageAsync(conversationId, ct);

    private async Task AppendControlAsync(
        Guid conversationId, EventBase e, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IEventService>()
            .AppendAsync(conversationId, e, ct);
    }
}