using System.Net.Http.Json;
using System.Text.Json;
using OpenHandsNET.Eval;

namespace OpenHandsNET.Cli.Services;

/// <summary>
/// SPEC-39 RF-002: <see cref="IEvalBackend"/> sobre HTTP — conversa +
/// start-task + events + exec/file do sandbox + juiz OpenAI-compat.
/// </summary>
public sealed class EvalBackend(CloudApiClient api, HttpClient http) : IEvalBackend
{
    private readonly RunExecutor _runner = new(api);

    /// <inheritdoc />
    public async Task<string> CreateConversationAsync(
        string task, string? repo, string? model, CancellationToken ct)
    {
        var created = await api.CreateConversationAsync(
            task, repo, model: model, ct: ct);
        return created.GetProperty("id").GetString()!;
    }

    /// <inheritdoc />
    public async Task<string> WaitReadyAsync(
        string taskId, CancellationToken ct)
    {
        var (convId, error) = await _runner.WaitReadyAsync(taskId, ct: ct);
        return convId ?? throw new InvalidOperationException(
            error ?? "start task failed");
    }

    /// <inheritdoc />
    public async Task RunSetupAsync(
        string conversationId, IReadOnlyList<string> commands,
        CancellationToken ct)
    {
        foreach (var cmd in commands)
        {
            var (code, stdout) = await ExecAsync(conversationId, cmd, ct);
            if (code != 0)
            {
                throw new InvalidOperationException(
                    $"setup `{cmd}` exit {code}: {stdout[..Math.Min(200, stdout.Length)]}");
            }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JsonElement>> WatchAsync(
        string conversationId, TimeSpan timeout, CancellationToken ct)
    {
        var events = new List<JsonElement>();
        var result = await _runner.WatchAsync(conversationId, timeout,
            e => events.Add(e.Clone()), ct);
        if (result.ExitCode == 3)
        {
            throw new TaskCanceledException(result.Detail);
        }

        return events;
    }

    /// <inheritdoc />
    public async Task<(int ExitCode, string StdOut)> ExecAsync(
        string conversationId, string command, CancellationToken ct)
    {
        var resp = await http.PostAsJsonAsync(
            $"{api.BaseUrl}/api/v1/app-conversations/{conversationId}/exec",
            new { command }, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return (-1, $"http {(int)resp.StatusCode}");
        }

        var el = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        return (
            el.GetProperty("exit_code").GetInt32(),
            el.TryGetProperty("stdout", out var o) ? o.GetString() ?? "" : "");
    }

    /// <inheritdoc />
    public async Task<string?> ReadFileAsync(
        string conversationId, string path, CancellationToken ct)
    {
        var resp = await http.GetAsync(
            $"{api.BaseUrl}/api/v1/app-conversations/{conversationId}/file"
            + $"?path={Uri.EscapeDataString(
                path.StartsWith("/workspace", StringComparison.Ordinal)
                    ? path : $"/workspace/{path.TrimStart('/')}")}", ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var el = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (el.TryGetProperty("encoding", out var enc)
            && enc.GetString() == "base64")
        {
            return Convert.ToBase64String(Convert.FromBase64String(
                el.GetProperty("content").GetString()!));
        }

        return el.TryGetProperty("content", out var c) ? c.GetString() : null;
    }

    /// <inheritdoc />
    public async Task<(double Score, string Rationale)?> JudgeAsync(
        string rubric, string transcript, CancellationToken ct)
    {
        // Juiz via endpoint OpenAI-compat dedicado (env); sem config → skipped.
        var url = Environment.GetEnvironmentVariable("OPENHANDSNET_EVAL_JUDGE_URL");
        var model = Environment.GetEnvironmentVariable("OPENHANDSNET_EVAL_JUDGE_MODEL")
            ?? "summarizer";
        var key = Environment.GetEnvironmentVariable("OPENHANDSNET_EVAL_JUDGE_KEY");
        if (url is null)
        {
            return null;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{url.TrimEnd('/')}/chat/completions");
        if (key is not null)
        {
            req.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", key);
        }

        req.Content = JsonContent.Create(new
        {
            model,
            temperature = 0.0,
            messages = new object[]
            {
                new { role = "system", content =
                    "You are an eval judge. Reply with JSON only: "
                    + "{\"score\": <0..1>, \"rationale\": \"...\"}" },
                new { role = "user", content =
                    $"Rubric: {rubric}\n\n--- agent transcript ---\n"
                    + transcript[..Math.Min(16000, transcript.Length)] },
            },
        });

        var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var el = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        var content = el.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString() ?? "";
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return (0, "judge returned non-JSON");
        }

        var parsed = JsonDocument.Parse(content[start..(end + 1)]).RootElement;
        return (parsed.GetProperty("score").GetDouble(),
            parsed.TryGetProperty("rationale", out var r)
                ? r.GetString() ?? "" : "");
    }
}
