using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Infrastructure.Agent.Tools;

/// <summary>
/// SPEC-125 RF-004: tool <c>advisor</c> (deferred — carrega via
/// <c>tool_search</c>) que o modelo invoca pedindo segunda opinião ao
/// <see cref="IAdvisorService"/>. A resposta vai na observação como
/// <c>&lt;advisor&gt;…&lt;/advisor&gt;</c> e vira evento persistido
/// <c>advisor</c> no stream (via <see cref="ToolCallContext.EmitAsync"/>).
/// </summary>
public static class BuiltinAdvisor
{
    /// <summary>Pede parecer do advisor sobre uma decisão/questão.</summary>
    public static AgentTool Advisor() => new(
        new ToolSchema("advisor",
            "Ask a second model (advisor) for a short opinion about a "
            + "decision, risky action or approach — read-only, never "
            + "executes tools.",
            Schema("""
                {"type":"object","properties":{
                  "question":{"type":"string","description":"What to ask the advisor"},
                  "context":{"type":"string","description":"Relevant transcript/decision context"}},
                 "required":["question"]}
                """)),
        Risky: false,
        Handler: async (ctx, a, ct) =>
        {
            var advisor = ctx.Services.GetService<IAdvisorService>();
            if (advisor is null || !advisor.Enabled)
            {
                return ToolResult.Error(
                    "advisor indisponível — configure Agent:AdvisorModel");
            }

            var question = Str(a, "question");
            if (question is null)
            {
                return ToolResult.Error("question é obrigatório");
            }

            var verdict = await advisor.ConsultAsync(
                Str(a, "context") ?? "(sem contexto adicional)",
                question, ct);
            if (verdict is null)
            {
                return ToolResult.Error(
                    "advisor não respondeu (timeout/provider) — prossiga "
                    + "sem a consulta");
            }

            await ctx.EmitAsync(new Domain.Events.AdvisorEvent
            {
                Question = question,
                Answer = verdict.Answer,
                Model = verdict.Model,
                ElapsedMs = verdict.ElapsedMs,
                ParentId = ctx.ActionId,
            }, ct);
            return ToolResult.Ok(new
            {
                advisor = verdict.Answer,
                model = verdict.Model,
            });
        });

    private static string? Str(JsonElement a, string n) =>
        a.TryGetProperty(n, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static JsonElement Schema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}