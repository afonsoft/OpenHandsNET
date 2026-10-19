using System.Text.Json;

namespace OpenHandsNET.Cli.Services;

/// <summary>
/// Fluxo headless: cria conversa → poll start-task até READY → poll
/// eventos até execution_status terminal. Exit codes (SPEC-06 §10):
/// 0 finished · 1 erro genérico · 2 agent error/stopped · 3 timeout.
/// </summary>
public sealed class RunExecutor(CloudApiClient api)
{
    public sealed record Result(
        int ExitCode, string? ConversationId, string ExecutionStatus,
        string? Detail);

    private static readonly HashSet<string> TerminalStates = new(
        StringComparer.OrdinalIgnoreCase)
        { "FINISHED", "STOPPED", "ERROR", "PAUSED" };

    /// <summary>Espera o start-task chegar a READY; devolve conversation id.</summary>
    public async Task<(string? ConversationId, string? Error)> WaitReadyAsync(
        string taskId, Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        var last = "";
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var t = await api.GetStartTaskAsync(taskId, ct);
            var status = t?.TryGetProperty("status", out var s) is true
                ? s.GetString() ?? "" : "";
            if (status != last)
            {
                onStatus?.Invoke(status);
                last = status;
            }

            if (status is "READY")
            {
                return (t!.Value.GetProperty("app_conversation_id").GetString(), null);
            }

            if (status is "ERROR")
            {
                return (null, t!.Value.TryGetProperty("detail", out var d)
                    ? d.GetString() : "start task failed");
            }

            await Task.Delay(1500, ct);
        }
    }

    /// <summary>
    /// Observa eventos da conversa até status terminal ou timeout.
    /// <paramref name="onEvent"/> recebe cada evento cru (JsonElement).
    /// </summary>
    public async Task<Result> WatchAsync(
        string conversationId, TimeSpan timeout,
        Action<JsonElement>? onEvent = null, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        var seen = new HashSet<string>();
        string? lastPage = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            string status = "";
            var conv = await api.GetConversationAsync(conversationId, ct);
            if (conv is { } c && c.TryGetProperty("execution_status", out var es))
            {
                status = es.GetString() ?? "";
            }

            var page = await api.GetEventsAsync(conversationId, lastPage, ct);
            if (page.TryGetProperty("items", out var items))
            {
                foreach (var e in items.EnumerateArray())
                {
                    var id = e.TryGetProperty("id", out var i)
                        ? i.GetString() ?? "" : "";
                    if (seen.Add(id))
                    {
                        onEvent?.Invoke(e);
                    }
                }
            }

            lastPage = page.TryGetProperty("next_page_id", out var np)
                ? np.GetString() : null;

            if (TerminalStates.Contains(status))
            {
                return new Result(
                    status is "FINISHED" ? 0 : 2,
                    conversationId, status, null);
            }

            await Task.Delay(2000, ct);
        }

        return new Result(3, conversationId, "TIMEOUT",
            $"timeout após {timeout.TotalMinutes:N0} min");
    }

    /// <summary>JSON de saída CI-safe (uma linha).</summary>
    public static string ToJson(Result r) => JsonSerializer.Serialize(new
    {
        conversation_id = r.ConversationId,
        execution_status = r.ExecutionStatus,
        exit_code = r.ExitCode,
        detail = r.Detail,
    });
}