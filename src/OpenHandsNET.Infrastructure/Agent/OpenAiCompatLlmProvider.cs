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
    IOptions<AgentLlmOptions> agentOptions,
    ILogger<OpenAiCompatLlmProvider> logger) : IAgentLlmProvider
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <inheritdoc />
    public string ModelId => agentOptions.Value.Model;

    /// <inheritdoc />
    public Task<ModelInfo> GetModelInfoAsync(CancellationToken ct) =>
        Task.FromResult(new ModelInfo(ModelId, "openai_compat"));

    /// <inheritdoc />
    public async IAsyncEnumerable<ILlmStreamChunk> ChatStreamAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<ToolSchema> tools,
        LlmOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cfg = agentOptions.Value;
        var request = BuildRequest(messages, tools, options, cfg);
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
                var redacted = SecretRedactor.Redact(body);
                // SPEC-22 RF-004: overflow é classificado — o runner compacta
                // (ou promove o modelo) e retenta, em vez de falhar a run.
                if ((int)response.StatusCode == 400 && IsContextOverflow(body))
                {
                    throw new ContextOverflowException(
                        $"LLM context overflow: {redacted[..Math.Min(redacted.Length, 300)]}");
                }

                // SPEC-23 RF-003: classificação — kinds não retentáveis
                // (Auth, ModelUnavailable) abortam sem gastar retries.
                var kind = Classify(response.StatusCode, body);
                last = new LlmCallException(kind,
                    $"LLM endpoint {(int)response.StatusCode}: {redacted}",
                    (int)response.StatusCode);
                if (!IsRetryable(kind))
                {
                    throw last;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                and not ContextOverflowException)
            {
                if (ex is LlmCallException lce && !IsRetryable(lce.Kind))
                {
                    throw;
                }

                last = ex is HttpRequestException or TaskCanceledException
                    ? new LlmCallException(
                        LlmErrorKind.Transient, ex.Message, null, ex)
                    : ex;
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

        // SPEC-50: BYOR override — a chamada vai ao proxy LiteLLM com a
        // virtual key do owner; X-LiteLLM-Key carrega o key_id (tracking).
        var byor = llmOptions.Byor;
        var baseUrl = (byor?.BaseUrl ?? cfg.BaseUrl
            ?? "http://localhost:4000").TrimEnd('/');
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"{baseUrl}/chat/completions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, Json),
                Encoding.UTF8, "application/json"),
        };
        if (byor is not null)
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", byor.VirtualKey);
            request.Headers.TryAddWithoutValidation(
                "X-LiteLLM-Key", byor.KeyId);
        }
        else if (!string.IsNullOrEmpty(cfg.ApiKey))
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

    private static IEnumerable<ILlmStreamChunk> ParseChunk(string data)
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
            // SPEC-41 RF-001: reasoning viaja em canal separado do content —
            // reasoning_content (DeepSeek/OmniRoute), reasoning, e o array
            // reasoning_details[{text}] (Responses-style).
            if (delta.TryGetProperty("reasoning_content", out var rc)
                && rc.ValueKind == JsonValueKind.String)
            {
                yield return new ReasoningDelta(rc.GetString()!);
            }

            if (delta.TryGetProperty("reasoning", out var r)
                && r.ValueKind == JsonValueKind.String)
            {
                yield return new ReasoningDelta(r.GetString()!);
            }

            if (delta.TryGetProperty("reasoning_details", out var rd)
                && rd.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in rd.EnumerateArray())
                {
                    if (item.TryGetProperty("text", out var t)
                        && t.ValueKind == JsonValueKind.String)
                    {
                        yield return new ReasoningDelta(t.GetString()!);
                    }
                }
            }

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

    private static readonly string[] OverflowMarkers =
    [
        "context_length", "context window", "maximum context",
        "too many tokens", "token limit", "reduce the length",
        "exceeds the context", "context_length_exceeded",
    ];

    private static bool IsContextOverflow(string body) =>
        OverflowMarkers.Any(m =>
            body.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>SPEC-23 RF-003: status/body → kind acionável pelo router.</summary>
    internal static LlmErrorKind Classify(
        System.Net.HttpStatusCode status, string body)
    {
        var code = (int)status;
        return code switch
        {
            401 or 403 => LlmErrorKind.Auth,
            404 when body.Contains("model", StringComparison.OrdinalIgnoreCase)
                => LlmErrorKind.ModelUnavailable,
            429 => LlmErrorKind.RateLimited,
            >= 500 and < 600 => LlmErrorKind.Transient,
            408 => LlmErrorKind.Transient,
            _ when body.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
                => LlmErrorKind.RateLimited,
            _ when body.Contains("model_not_found",
                    StringComparison.OrdinalIgnoreCase)
                || body.Contains("does not exist",
                    StringComparison.OrdinalIgnoreCase)
                => LlmErrorKind.ModelUnavailable,
            _ => LlmErrorKind.Unknown,
        };
    }

    private static bool IsRetryable(LlmErrorKind kind) =>
        kind is LlmErrorKind.RateLimited or LlmErrorKind.Transient
            or LlmErrorKind.Unknown;

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