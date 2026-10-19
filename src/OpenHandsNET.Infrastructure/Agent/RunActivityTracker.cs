using System.Diagnostics;
using System.Text.Json;

using OpenHandsNET.Application.Events;
using OpenHandsNET.Domain.Events;

namespace OpenHandsNET.Infrastructure.Agent;

/// <summary>
/// SPEC-24 RF-003/RF-005: accumulates per-run counters and emits the
/// run-activity events — <c>run_progress</c> per iteration,
/// <c>work_summary</c> (deterministic) at finish/pause/error, and
/// <c>agent_diagnostic</c> for notable conditions.
/// </summary>
public sealed class RunActivityTracker
{
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly HashSet<string> _filesTouched =
        new(StringComparer.Ordinal);
    private int _toolsUsed;
    private int _commandsRun;

    /// <summary>Notes a completed tool call (name + raw args JSON).</summary>
    public void NoteToolCall(string toolName, string? argumentsJson)
    {
        _toolsUsed++;
        if (toolName is "bash")
        {
            _commandsRun++;
        }
        else if (toolName is "file_write" or "file_edit")
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson ?? "{}");
                if (doc.RootElement.TryGetProperty("path", out var p)
                    && p.ValueKind == JsonValueKind.String)
                {
                    _filesTouched.Add(p.GetString() ?? "");
                }
            }
            catch (JsonException)
            {
                // args malformados — ignora o tracking, não o call.
            }
        }
    }

    /// <summary>Per-iteration progress; the UI replaces the previous one.</summary>
    public async Task ProgressAsync(IEventService events,
        Guid conversationId, int iteration, int maxIterations,
        string phase, CancellationToken ct) =>
        await events.AppendAsync(conversationId, new RunProgressEvent
        {
            Iteration = iteration + 1,
            MaxIterations = maxIterations,
            Phase = phase,
            ElapsedMs = _elapsed.ElapsedMilliseconds,
            ToolsUsed = _toolsUsed,
            FilesTouched = _filesTouched.Count,
        }, ct);

    /// <summary>
    /// SPEC-41 RF-004: liveness ping durante calls longas (LLM/tool) —
    /// o watchdog do client mede silêncio de eventos; sem heartbeat uma
    /// tool de 60s pareceria travada. Iteration=0 marca "não-iteração".
    /// </summary>
    public async Task HeartbeatAsync(IEventService events,
        Guid conversationId, CancellationToken ct) =>
        await events.AppendAsync(conversationId, new RunProgressEvent
        {
            Iteration = 0,
            MaxIterations = 0,
            Phase = "heartbeat",
            ElapsedMs = _elapsed.ElapsedMilliseconds,
            ToolsUsed = _toolsUsed,
            FilesTouched = _filesTouched.Count,
        }, ct);

    /// <summary>
    /// Deterministic end-of-run summary (SPEC-24 RF-005): counts and the
    /// changed-file list — no LLM call, so it is available even on
    /// error/pause paths.
    /// </summary>
    public async Task SummaryAsync(IEventService events,
        Guid conversationId, string reason, string? detail,
        CancellationToken ct)
    {
        var summary = reason switch
        {
            "completed" => detail is { Length: > 0 }
                ? detail
                : $"Run completed: {_toolsUsed} tool calls, "
                    + $"{_filesTouched.Count} files changed.",
            "paused" => $"Run paused after {_toolsUsed} tool calls "
                + $"({_elapsed.Elapsed:mm\\:ss}).",
            "interrupted" => $"Run interrupted after {_toolsUsed} tool calls.",
            "stuck" => $"Stopped: max iterations reached after "
                + $"{_toolsUsed} tool calls.",
            _ => $"Run ended ({reason}) after {_toolsUsed} tool calls.",
        };
        await events.AppendAsync(conversationId, new WorkSummaryEvent
        {
            Summary = summary,
            FilesChanged = _filesTouched.Order().ToList(),
            CommandsRun = _commandsRun,
            DurationMs = _elapsed.ElapsedMilliseconds,
        }, ct);
    }

    /// <summary>Diagnostic event for notable runner conditions.</summary>
    public async Task DiagnosticAsync(IEventService events,
        Guid conversationId, string severity, string code, string message,
        CancellationToken ct) =>
        await events.AppendAsync(conversationId, new AgentDiagnosticEvent
        {
            Severity = severity,
            Code = code,
            Message = SecretRedactor.Redact(message),
        }, ct);
}