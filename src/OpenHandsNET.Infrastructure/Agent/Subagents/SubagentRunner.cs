using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Domain.Sandbox;
using OpenHandsNET.Infrastructure.Agent.Tools;

namespace OpenHandsNET.Infrastructure.Agent.Subagents;

/// <summary>
/// SPEC-28 RF-002: child ReAct loop — compact prompt, preset-filtered
/// tools, iteration cap, namespaced events (<c>parent_id</c> = the parent
/// <c>task</c> action), final text/<c>finish</c> becomes the result.
/// </summary>
public sealed class SubagentRunner(
    IAgentLlmProvider llm,
    IOptions<AgentLlmOptions> options)
{
    /// <summary>Runs the child loop; returns the result summary text.</summary>
    public async Task<string> RunAsync(
        Guid conversationId, Domain.Sandbox.Sandbox sandbox,
        ISandboxExecutor executor,
        IEventService events, IServiceProvider services,
        string parentActionId, string subagentType, string description,
        string prompt, string taskId, CancellationToken ct)
    {
        var opt = options.Value;
        var registry = services.GetRequiredService<IToolRegistry>();
        var allow = SubagentPresets.Resolve(subagentType)
            ?? throw new InvalidOperationException(
                $"unknown subagent_type '{subagentType}'");
        var filtered = new FilterToolRegistry(registry, allow);
        var schemas = filtered.All.Select(t => t.Schema).ToList();
        var router = services.GetService<IModelRouter>();
        var selection = router is not null
            ? await router.SelectAsync(
                conversationId, ModelRole.Subagent, null, ct)
            : null;

        await events.AppendAsync(conversationId,
            new ConversationStateUpdateEvent
            {
                Key = "subagent_run",
                Value = JsonSerializer.SerializeToElement(new
                {
                    task_id = taskId,
                    subagent_type = subagentType,
                    description,
                    status = "running",
                    model = selection?.Model,
                }, EventJson.Options),
                ParentId = parentActionId,
            }, ct);

        var messages = new List<LlmMessage>
        {
            new("system",
                $"You are a {subagentType} subagent inside OpenHandsNET. "
                + "Work autonomously on the task below using ONLY your "
                + "allowed tools, then call finish with a concise summary "
                + "for the parent agent. Repository root: /workspace/repo. "
                + "Never ask questions — report findings via finish."),
            LlmMessage.User(prompt),
        };

        string? result = null;
        string? lastError = null;
        var iterations = Math.Max(1, opt.Subagents.MaxIterations);
        for (var i = 0; i < iterations && result is null; i++)
        {
            var text = new StringBuilder();
            var calls = new Dictionary<int,
                (string id, string name, StringBuilder args)>();
            await foreach (var chunk in llm.ChatStreamAsync(messages,
                schemas, new LlmOptions(Model: selection?.Model), ct))
            {
                switch (chunk)
                {
                    case TextDelta d:
                        text.Append(d.Text);
                        break;
                    case ToolCallDelta tcd:
                        if (!calls.TryGetValue(tcd.Index, out var cur))
                        {
                            cur = ("", "", new StringBuilder());
                        }

                        cur.id = tcd.Id ?? cur.id;
                        cur.name = tcd.Name ?? cur.name;
                        if (tcd.ArgumentsFragment is { } frag)
                        {
                            cur.args.Append(frag);
                        }

                        calls[tcd.Index] = cur;
                        break;
                }
            }

            var ordered = calls.OrderBy(kv => kv.Key)
                .Select(kv => new LlmToolCall(
                    kv.Value.id, kv.Value.name, kv.Value.args.ToString()))
                .ToList();
            messages.Add(LlmMessage.AssistantToolCall(
                text.Length > 0 ? text.ToString() : null, ordered));
            if (ordered.Count == 0)
            {
                result = text.ToString();
                break;
            }

            foreach (var call in ordered)
            {
                var parsed = TryParse(call.ArgumentsJson);
                var actionEvent = new ActionEvent
                {
                    ToolName = call.Name,
                    Action = parsed,
                    ToolCallId = call.Id,
                    LlmResponseId = "",
                    ParentId = parentActionId,
                };
                await events.AppendAsync(conversationId, actionEvent, ct);

                // finish interceptado aqui — o registro do filho nunca
                // executa o Finish real do pai.
                if (call.Name == "finish")
                {
                    result = parsed.TryGetProperty("final_message", out var m)
                        ? m.GetString() ?? "done" : "done";
                    continue;
                }

                var tool = filtered.Find(call.Name);
                ToolResult toolResult;
                if (tool is null)
                {
                    toolResult = ToolResult.Error(
                        $"tool '{call.Name}' is not available to "
                        + $"a {subagentType} subagent");
                }
                else
                {
                    try
                    {
                        using var toolCts = CancellationTokenSource
                            .CreateLinkedTokenSource(ct);
                        toolCts.CancelAfter(
                            TimeSpan.FromSeconds(opt.ToolTimeoutSeconds));
                        var ctx = new ToolCallContext(
                            conversationId, sandbox, executor, events,
                            services,
                            TimeSpan.FromSeconds(opt.ToolTimeoutSeconds),
                            call.Id, actionEvent.Id);
                        toolResult = await tool.Handler(
                            ctx, parsed, toolCts.Token);
                    }
                    catch (OperationCanceledException)
                        when (!ct.IsCancellationRequested)
                    {
                        toolResult = ToolResult.Error("tool timed out");
                    }
                    catch (Exception ex)
                        when (ex is not OperationCanceledException)
                    {
                        toolResult = ToolResult.Error(
                            SecretRedactor.Redact(ex.Message));
                        lastError = ex.Message;
                    }
                }

                await events.AppendAsync(conversationId,
                    new ObservationEvent
                    {
                        ToolName = call.Name,
                        ToolCallId = call.Id,
                        ActionId = actionEvent.Id,
                        Observation = toolResult.Observation,
                        ParentId = actionEvent.Id,
                    }, ct);
                messages.Add(LlmMessage.ToolResult(
                    call.Id, toolResult.Observation.GetRawText()));
            }
        }

        if (result is null)
        {
            result = $"subagent reached iteration cap ({iterations})"
                + (lastError is not null ? $"; last error: {lastError}" : "");
        }

        // Cap 8KB — a exploração não polui a janela do pai.
        var (capped, truncated) = OutputPolicy.Truncate(result, 8 * 1024);
        if (truncated)
        {
            capped += "\n[summary truncated to 8KB]";
        }

        await events.AppendAsync(conversationId,
            new ConversationStateUpdateEvent
            {
                Key = "subagent_run",
                Value = JsonSerializer.SerializeToElement(new
                {
                    task_id = taskId,
                    subagent_type = subagentType,
                    status = "completed",
                }, EventJson.Options),
                ParentId = parentActionId,
            }, CancellationToken.None);
        return capped;
    }

    private static JsonElement TryParse(string? json)
    {
        try
        {
            return JsonDocument.Parse(
                string.IsNullOrWhiteSpace(json) ? "{}" : json)
                .RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }
}