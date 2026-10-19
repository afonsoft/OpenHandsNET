using System.Text;
using System.Text.Json;

using OpenHandsNET.Api.ProblemDetails;
using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.Conversations;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Domain.Conversations;
using OpenHandsNET.Domain.Events;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// SPEC-40 RF-001: gateway OpenAI-compatível (<c>POST /v1/chat/completions</c>).
/// Cria conversa efêmera (ou anexa via <c>X-Conversation-Id</c>), roda o
/// agente e responde no formato OpenAI — <c>stream:true</c> emite
/// <c>chat.completion.chunk</c> SSE alimentado pelos
/// <c>streaming_delta</c> da SPEC-34. Tool calls internas NÃO vazam como
/// tools OpenAI; erros no formato <c>{error:{message,type,code}}</c>.
/// </summary>
public static class OpenAiGatewayEndpoints
{
    private static readonly HashSet<string> Rejected = new(
        StringComparer.OrdinalIgnoreCase)
        { "tools", "functions", "response_format" };

    /// <summary>Maps <c>/v1/chat/completions</c> (auth: cookie ou sk-*).</summary>
    public static IEndpointRouteBuilder MapOpenAiGatewayEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/chat/completions", ChatCompletionsAsync)
            .RequireAuthorization()
            .WithTags("OpenAI Gateway");
        return app;
    }

    private static async Task ChatCompletionsAsync(
        HttpContext http,
        IAppConversationService conversations,
        IStartTaskService tasks,
        IEventService events,
        IAgentActivityBus activity,
        IAgentRunnerHost runnerHost,
        CancellationToken ct)
    {
        ChatRequest req;
        try
        {
            req = await ParseAsync(http.Request, ct);
        }
        catch (GatewayException ex)
        {
            await WriteErrorAsync(http, 400, ex.Message, "invalid_request_error");
            return;
        }

        if (!http.Request.Headers.TryGetValue("X-Conversation-Id", out var cidHeader)
            || !Guid.TryParse(cidHeader, out var convId))
        {
            convId = Guid.Empty;
        }

        var dispatchedAt = DateTimeOffset.UtcNow;
        try
        {
            if (convId == Guid.Empty)
            {
                convId = await CreateAndWaitAsync(
                    req, conversations, tasks, ct);
            }
            else
            {
                await AttachAndRunAsync(
                    convId, req, conversations, events, runnerHost, ct);
            }
        }
        catch (PlanLimitExceededException ex)
        {
            await WriteErrorAsync(http, 403,
                $"plan_limit_exceeded:{ex.Error.LimitKey} "
                    + $"({ex.Error.Used}/{ex.Error.Limit})",
                "permission_error", "plan_limit_exceeded");
            return;
        }
        catch (GatewayException ex)
        {
            await WriteErrorAsync(
                http, ex.Status, ex.Message, ex.Type, ex.Code);
            return;
        }

        var timeout = TimeSpan.FromMinutes(10);
        using var deadline = CancellationTokenSource
            .CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        // SPEC-40 RF-006: métricas do gateway (model+stream).
        var metricModel = string.IsNullOrWhiteSpace(req.Model)
            ? "default" : req.Model;
        Infrastructure.Telemetry.OpenNetTelemetry.GatewayRequests
            .WithLabels(metricModel).Inc();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (req.Stream)
            {
                await StreamCompletionAsync(http, convId, req.Model,
                    events, activity, conversations, dispatchedAt,
                    deadline.Token);
            }
            else
            {
                await JsonCompletionAsync(http, convId, req.Model,
                    events, conversations, dispatchedAt, deadline.Token);
            }
        }
        catch (OperationCanceledException)
            when (!ct.IsCancellationRequested)
        {
            // Deadline interno (não o client abort) → 504 estilo gateway.
            if (!http.Response.HasStarted)
            {
                await WriteErrorAsync(http, 504,
                    "completion timed out", "timeout", "deadline_exceeded");
            }
        }
        finally
        {
            Infrastructure.Telemetry.OpenNetTelemetry.GatewayLatency
                .WithLabels(metricModel, req.Stream ? "true" : "false")
                .Observe(sw.Elapsed.TotalSeconds);
        }
    }

    private sealed record ChatRequest(
        string Model, IReadOnlyList<(string Role, string Text)> Messages,
        bool Stream)
    {
        /// <summary>Última mensagem user (o prompt efetivo do run).</summary>
        public string Prompt => Messages
            .LastOrDefault(m => m.Role == "user").Text
            ?? Messages.Last().Text;
    }

    private static async Task<ChatRequest> ParseAsync(
        HttpRequest request, CancellationToken ct)
    {
        var body = await JsonDocument.ParseAsync(request.Body,
            cancellationToken: ct);
        var root = body.RootElement;
        foreach (var bad in Rejected)
        {
            if (root.TryGetProperty(bad, out _))
            {
                throw new GatewayException(
                    $"'{bad}' is not supported — internal agent tools are "
                    + "not exposed via the OpenAI tools surface");
            }
        }

        if (root.TryGetProperty("n", out var n)
            && n.TryGetInt32(out var nv) && nv > 1)
        {
            throw new GatewayException("'n' > 1 is not supported");
        }

        var messages = root.TryGetProperty("messages", out var m)
            && m.ValueKind == JsonValueKind.Array
            ? m.EnumerateArray().Select(ParseMessage)
                .Where(x => x is not null).Cast<(string, string)>().ToList()
            : null;
        if (messages is null or { Count: 0 })
        {
            throw new GatewayException("messages[] is required");
        }

        var model = root.TryGetProperty("model", out var mo)
            ? mo.GetString() ?? "" : "";
        var stream = root.TryGetProperty("stream", out var s)
            && s.ValueKind == JsonValueKind.True;

        return new ChatRequest(model, messages, stream);
    }

    private static (string, string)? ParseMessage(JsonElement m)
    {
        if (m.ValueKind != JsonValueKind.Object
            || !m.TryGetProperty("role", out var r))
        {
            return null;
        }

        var text = m.TryGetProperty("content", out var c)
            ? c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? ""
                : c.ValueKind == JsonValueKind.Array
                    ? string.Concat(c.EnumerateArray()
                        .Where(p => p.TryGetProperty("type", out var t)
                            && t.GetString() == "text")
                        .Select(p => p.TryGetProperty("text", out var tx)
                            ? tx.GetString() : ""))
                    : ""
            : "";
        return (r.GetString() ?? "user", text);
    }

    /// <summary>Cria conversa efêmera com a mensagem e espera READY.</summary>
    private static async Task<Guid> CreateAndWaitAsync(
        ChatRequest req, IAppConversationService conversations,
        IStartTaskService tasks, CancellationToken ct)
    {
        // Histórico vira contexto; prompt = última mensagem do usuário.
        var history = req.Messages.Count > 1
            ? string.Join("\n",
                req.Messages.Take(req.Messages.Count - 1)
                    .Select(m => $"{m.Role}: {m.Text}"))
            : null;
        var prompt = history is null ? req.Prompt
            : $"[context]\n{history}\n\n[user]\n{req.Prompt}";

        var task = await conversations.CreateAsync(
            new CreateConversationRequest(
                new InitialMessage("user",
                    [new { type = "text", text = prompt }]),
                SelectedRepository: null,
                AgentType: null,
                LlmModel: string.IsNullOrWhiteSpace(req.Model)
                    ? null : req.Model,
                SandboxSpecId: null,
                ConfirmationMode: false,
                SandboxProfile: null,
                Permissions: null,
                Mode: null,
                Secrets: null),
            ct);

        await foreach (var update in tasks.WatchAsync(task.Id, ct))
        {
            if (update.Status is Domain.Conversations.StartTaskStatus.Ready)
            {
                return update.AppConversationId
                    ?? throw new GatewayException(
                        "conversation id missing on READY task",
                        Status: 502, Type: "api_error");
            }

            if (update.Status is Domain.Conversations.StartTaskStatus.Error)
            {
                throw new GatewayException(
                    update.Detail ?? "start task failed",
                    Status: 502, Type: "api_error");
            }
        }

        throw new GatewayException("start task stream closed",
            Status: 502, Type: "api_error");
    }

    /// <summary>Anexa mensagem a conversa existente e dispara o run.</summary>
    private static async Task AttachAndRunAsync(
        Guid convId, ChatRequest req, IAppConversationService svc,
        IEventService events, IAgentRunnerHost runnerHost,
        CancellationToken ct)
    {
        var error = await svc.ValidateSendMessageAsync(convId, ct);
        if (error is not null)
        {
            throw new GatewayException(
                $"conversation {convId}: {error}",
                Status: error is SendMessageError.NotFound ? 404
                    : error is SendMessageError.Gone ? 410 : 409,
                Type: "invalid_request_error");
        }

        await events.AppendAsync(convId, new MessageEvent
        {
            Source = EventSource.User,
            LlmMessage = JsonSerializer.SerializeToElement(new
            {
                role = "user",
                content = new[]
                {
                    new { type = "text", text = req.Prompt },
                },
            }, EventJson.Options),
        }, ct);
        await runnerHost.NotifyMessageAsync(convId, ct);
    }

    /// <summary>stream=false: espera terminal e devolve completion agregada.</summary>
    private static async Task JsonCompletionAsync(
        HttpContext http, Guid convId, string model,
        IEventService events, IAppConversationService conversations,
        DateTimeOffset notBefore, CancellationToken ct)
    {
        var status = await WaitTerminalAsync(
            conversations, convId, notBefore, ct);
        var (text, usage) = await AggregateAsync(events, convId, ct);
        var finish = status is "FINISHED" or "STOPPED" or "PAUSED"
            ? "stop" : "error";
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            id = $"chatcmpl-{convId:N}",
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content = text },
                    finish_reason = finish,
                },
            },
            usage = new
            {
                prompt_tokens = usage.Prompt,
                completion_tokens = usage.Completion,
                total_tokens = usage.Prompt + usage.Completion,
            },
        }), ct);
    }

    /// <summary>stream=true: SSE chunks alimentados pelo DeltaBatcher.</summary>
    private static async Task StreamCompletionAsync(
        HttpContext http, Guid convId, string model,
        IEventService events, IAgentActivityBus activity,
        IAppConversationService conversations,
        DateTimeOffset notBefore, CancellationToken ct)
    {
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";

        var id = $"chatcmpl-{convId:N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sentText = new HashSet<string>(
            StringComparer.Ordinal);

        async Task WriteChunkAsync(object payload)
        {
            await http.Response.WriteAsync(
                $"data: {JsonSerializer.Serialize(payload)}\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
        }

        // Primeiro chunk: role (convenção OpenAI streaming).
        await WriteChunkAsync(new
        {
            id,
            @object = "chat.completion.chunk",
            created,
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { role = "assistant" },
                    finish_reason = (string?)null,
                },
            },
        });

        var terminal = WaitTerminalAsync(
            conversations, convId, notBefore, ct);
        // O bus de deltas nunca completa sozinho — cancela a enumeração
        // assim que o run chega a estado terminal (senão o SSE travava
        // até o deadline quando o tail não emite frames).
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = terminal.ContinueWith(
            _ => linked.Cancel(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try
        {
            await foreach (var frame in activity
                .SubscribeAsync(convId, linked.Token))
            {
                sentText.Add(frame.Text);
                await WriteChunkAsync(new
                {
                    id,
                    @object = "chat.completion.chunk",
                    created,
                    model,
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            delta = new { content = frame.Text },
                            finish_reason = (string?)null,
                        },
                    },
                });
            }
        }
        catch (OperationCanceledException)
            when (!ct.IsCancellationRequested)
        {
            // Terminal atingido (linked cancelado) — emite o agregado.
        }

        var status = await terminal;
        var (text, usage) = await AggregateAsync(events, convId, ct);
        if (sentText.Count == 0 && text.Length > 0)
        {
            // Sem deltas (modelo não-streaming): manda o texto inteiro.
            await WriteChunkAsync(new
            {
                id,
                @object = "chat.completion.chunk",
                created,
                model,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        delta = new { content = text },
                        finish_reason = (string?)null,
                    },
                },
            });
        }

        await WriteChunkAsync(new
        {
            id,
            @object = "chat.completion.chunk",
            created,
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { },
                    finish_reason = status is "FINISHED" or "STOPPED"
                        or "PAUSED" ? "stop" : "error",
                },
            },
            usage = new
            {
                prompt_tokens = usage.Prompt,
                completion_tokens = usage.Completion,
                total_tokens = usage.Prompt + usage.Completion,
            },
        });
        await http.Response.WriteAsync("data: [DONE]\n\n", ct);
    }

    /// <summary>
    /// Espera <c>execution_status</c> terminal via poll do serviço —
    /// robusto a runs que terminam antes da subscription de eventos.
    /// Duas fases: primeiro espera o run ficar ATIVO (o notify spawna o
    /// runner de forma assíncrona; sem a fase 1 a primeira poll leria o
    /// estado terminal do run anterior e retornaria vazio), depois o
    /// terminal em si.
    /// </summary>
    private static async Task<string> WaitTerminalAsync(
        IAppConversationService svc, Guid convId, DateTimeOffset notBefore,
        CancellationToken ct)
    {
        static bool IsTerminal(string s) => s is "FINISHED" or "ERROR"
            or "STOPPED" or "PAUSED" or "ARCHIVED";

        // Fase 1 — ativação do run (janela de graça cobre o spin-up).
        // Estado terminal só conta se UpdatedAt >= notBefore: senão é o
        // resíduo do run ANTERIOR e a poll devolveria resposta vazia.
        var grace = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTimeOffset.UtcNow < grace)
        {
            var conv = await svc.GetAsync(convId, ct);
            var status = conv?.ExecutionStatus.ToString()
                .ToUpperInvariant() ?? "STOPPED";
            if (status is "RUNNING" or "STARTING")
            {
                break;
            }

            if (conv is not null && IsTerminal(status)
                && status is not "STOPPED"
                && conv.UpdatedAt >= notBefore)
            {
                return status; // run desta request terminou já na fase 1.
            }

            await Task.Delay(500, ct);
        }

        // Fase 2 — terminal (UpdatedAt revalidado: um STOPPED pré-run
        // vira terminal legítimo só se o run nunca ativou na graça).
        while (true)
        {
            var conv = await svc.GetAsync(convId, ct);
            var status = conv?.ExecutionStatus.ToString()
                .ToUpperInvariant() ?? "STOPPED";
            if (IsTerminal(status)
                && (status is "STOPPED" or "ARCHIVED"
                    || conv!.UpdatedAt >= notBefore))
            {
                return status;
            }

            await Task.Delay(1000, ct);
        }
    }

    /// <summary>
    /// Agrega o conteúdo das mensagens do agente + tokens — lê o backlog
    /// durable, nunca deltas efêmeros.
    /// </summary>
    private static async Task<(string Text, (long Prompt, long Completion) Usage)>
        AggregateAsync(IEventService events, Guid convId, CancellationToken ct)
    {
        var sb = new StringBuilder();
        long prompt = 0, completion = 0;
        string? cursor = null;
        do
        {
            var page = await events.SearchAsync(convId,
                new EventQuery(PageId: cursor, Limit: 200), ct);
            foreach (var e in page.Items)
            {
                switch (e)
                {
                    case MessageEvent { Source: EventSource.Agent } msg:
                        sb.Append(ExtractText(msg.LlmMessage));
                        break;
                    case TokenEvent tok:
                        prompt += tok.PromptTokens ?? 0;
                        completion += tok.CompletionTokens ?? 0;
                        break;
                }
            }

            cursor = page.NextPageId;
        }
        while (cursor is not null);

        return (sb.ToString(), (prompt, completion));
    }

    /// <summary>Extrai texto do LlmMessage (content string ou blocks).</summary>
    private static string ExtractText(JsonElement msg)
    {
        if (!msg.TryGetProperty("content", out var c))
        {
            return "";
        }

        return c.ValueKind switch
        {
            JsonValueKind.String => c.GetString() ?? "",
            JsonValueKind.Array => string.Concat(c.EnumerateArray()
                .Where(p => p.TryGetProperty("type", out var t)
                    && t.GetString() == "text")
                .Select(p => p.TryGetProperty("text", out var tx)
                    ? tx.GetString() : "")),
            _ => "",
        };
    }

    /// <summary>Erro no formato OpenAI.</summary>
    private static async Task WriteErrorAsync(
        HttpContext http, int status, string message,
        string type, string? code = null)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error = new { message, type, code },
        }), http.RequestAborted);
    }

    /// <summary>Erro de validação/execução mapeado p/ OpenAI.</summary>
    private sealed class GatewayException(
        string message, int Status = 400,
        string Type = "invalid_request_error", string? Code = null)
        : Exception(message)
    {
        public int Status { get; } = Status;
        public string Type { get; } = Type;
        public string? Code { get; } = Code;
    }
}