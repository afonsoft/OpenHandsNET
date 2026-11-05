using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Infrastructure.Agent.Tools;

/// <summary>
/// SPEC-123 (port OWUIC <c>tool_search</c>): busca tools no catálogo
/// por nome/descrição — permite ao modelo descobrir tools fora do set
/// inicial sem carregar todos os schemas no prompt. Retorna nome +
/// descrição + schema dos matches (cap 10).
/// </summary>
public static class BuiltinToolSearch
{
    /// <summary>Busca tools do registry por substring (nome+descrição).</summary>
    public static AgentTool ToolSearch() => new(
        new ToolSchema("tool_search",
            "Search the tool catalog by name/description substring. "
            + "Returns matching tool names with schemas — use to "
            + "discover tools not in your initial set.",
            Schema("""
                {"type":"object","properties":{
                  "query":{"type":"string","description":"Substring to match (e.g. 'lsp', 'worktree', 'file')"},
                  "limit":{"type":"integer","description":"Max matches (default 10)"}},
                 "required":["query"]}
                """)),
        Risky: false,
        Handler: (ctx, args, ct) =>
        {
            var query = args.GetProperty("query").GetString()!;
            var limit = args.TryGetProperty("limit", out var l)
                ? Math.Clamp(l.GetInt32(), 1, 25) : 10;
            var registry = ctx.Services.GetService<IToolRegistry>();
            if (registry is null)
            {
                return Task.FromResult(ToolResult.Error("registry indisponível"));
            }

            var hits = registry.All
                .Where(t =>
                    t.Schema.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || t.Schema.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(limit)
                .Select(t => new
                {
                    name = t.Schema.Name,
                    description = t.Schema.Description,
                    risky = t.Risky,
                    parameters = t.Schema.InputSchema,
                })
                .ToList();
            return Task.FromResult(ToolResult.Ok(
                new { query, count = hits.Count, tools = hits }));
        });

    private static JsonElement Schema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
