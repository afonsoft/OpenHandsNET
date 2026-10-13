using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Infrastructure.Agent;

/// <summary>
/// OpenAI-compatible streaming provider (SPEC-09 RF-006): plain HTTP+SSE
/// against <c>{BaseUrl}/chat/completions</c> — covers OpenAI, Azure, Ollama,
/// LiteLLM and LLMRouter without extra dependencies.
/// </summary>
public sealed class OpenAiCompatLlmProvider(
    IHttpClientFactory httpFactory,
    IOptions<AgentLlmOptions> options,
    ILogger<OpenAiCompatLlmProvider> logger) : IAgentLlmProvider
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <inheritdoc />
    public string ModelId => options.Value.Model;

    /// <inheritdoc />
    public Task<ModelInfo> GetModelInfoAsync(CancellationToken ct) =>
        Task.FromResult(new ModelInfo(ModelId, "openai_compat"));

    /// <inheritdoc />
    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<ToolSchema> tools,
        LlmOptions llmOptions,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cfg = options.Value;
        var request = BuildRequest(messages, tools, llmOptions, cfg);
        var response = await SendWithRetryAsync(request, cfg, ct);
        await using var stream = await response.Content
            .ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]")
            {
                break;
            }

            foreach (var chunk in ParseChunk(data))
            {
                yield return chunk;
            }
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpRequestMessage request, AgentLlmOptions cfg, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= cfg.MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                logger.LogWarning("LLM request retry {Attempt} em {Delay}s", attempt, delay.TotalSeconds);
                await Task.Delay(delay, ct);
                request = CloneRequest(request);
            }

            var client = httpFactory.CreateClient("agent-llm");
            client.Timeout = TimeSpan.FromSeconds(cfg.TimeoutSeconds);
            try
            {
                var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                var body = await response.Content.ReadAsStringAsync(ct);
                last = new InvalidOperationException(
                    $"LLM endpoint {(int)response.StatusCode}: {SecretRedactor.Redact(body)}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }
        }

        throw last!;
    }

    private HttpRequestMessage BuildRequest(
        IReadOnlyList<LlmMessage> messages, IReadOnlyList<ToolSchema> tools,
        LlmOptions llmOptions, AgentLlmOptions cfg)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = llmOptions.Model ?? cfg.Model,
            ["messages"] = messages.Select(ToWire).ToList(),
            ["stream"] = true,
            ["stream_options"] = new { include_usage = true },
        };
        if (tools.Count > 0)
        {
            payload["tools"] = tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = t.InputSchema,
                },
            }).ToList();
        }

        if (llmOptions.Temperature is not null)
        {
            payload["temperature"] = llmOptions.Temperature;
        }

        if (llmOptions.MaxTokens is not null)
        {
            payload["max_tokens"] = llmOptions.MaxTokens;
        }

        var baseUrl = (cfg.BaseUrl ?? "http://localhost:4000").TrimEnd('/');
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"{baseUrl}/chat/completions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, Json),
                Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(cfg.ApiKey))
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
        }

        return request;
    }

    private static object ToWire(LlmMessage m)
    {
        if (m.ToolCalls is { Count: > 0 })
        {
            return new
            {
                role = m.Role,
                content = m.Content,
                tool_calls = m.ToolCalls.Select(c => new
                {
                    id = c.Id,
                    type = "function",
                    function = new { name = c.Name, arguments = c.ArgumentsJson },
                }).ToList(),
            };
        }

        return m.ToolCallId is not null
            ? (object)new
            {
                role = m.Role,
                content = m.Content,
                tool_call_id = m.ToolCallId,
            }
            : new { role = m.Role, content = m.Content };
    }

    private static IEnumerable<LlmStreamChunk> ParseChunk(string data)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        var responseId = root.TryGetProperty("id", out var rid)
            ? rid.GetString() ?? "" : "";

        if (root.TryGetProperty("usage", out var usage)
            && usage.ValueKind == JsonValueKind.Object)
        {
            yield return new CompletionEnd(
                new LlmUsage(
                    usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0,
                    usage.TryGetProperty("completion_tokens", out var ctok) ? ctok.GetInt32() : 0,
                    null),
                FinishReasonOf(root), responseId);
        }

        if (!root.TryGetProperty("choices", out var choices)
            || choices.GetArrayLength() == 0)
        {
            yield break;
        }

        var delta = choices[0].TryGetProperty("delta", out var d) ? d : default;
        if (delta.ValueKind == JsonValueKind.Object)
        {
            if (delta.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                yield return new TextDelta(content.GetString()!);
            }

            if (delta.TryGetProperty("tool_calls", out var calls))
            {
                foreach (var call in calls.EnumerateArray())
                {
                    var fn = call.TryGetProperty("function", out var f) ? f : default;
                    yield return new ToolCallDelta(
                        call.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0,
                        call.TryGetProperty("id", out var id) ? id.GetString() : null,
                        fn.ValueKind == JsonValueKind.Object
                            && fn.TryGetProperty("name", out var n) ? n.GetString() : null,
                        fn.ValueKind == JsonValueKind.Object
                            && fn.TryGetProperty("arguments", out var a) ? a.GetString() : null);
                }
            }
        }

        var finish = FinishReasonOf(root);
        if (finish is not null && !root.TryGetProperty("usage", out _))
        {
            yield return new CompletionEnd(new LlmUsage(0, 0, null), finish, responseId);
        }
    }

    private static string? FinishReasonOf(JsonElement root) =>
        root.TryGetProperty("choices", out var ch)
            && ch.GetArrayLength() > 0
            && ch[0].TryGetProperty("finish_reason", out var fr)
            && fr.ValueKind == JsonValueKind.String
            ? fr.GetString()
            : null;

    private static HttpRequestMessage CloneRequest(HttpRequestMessage source)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri);
        if (source.Content is StringContent sc)
        {
            var body = sc.ReadAsStringAsync().GetAwaiter().GetResult();
            clone.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        foreach (var h in source.Headers)
        {
            clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        return clone;
    }
}
