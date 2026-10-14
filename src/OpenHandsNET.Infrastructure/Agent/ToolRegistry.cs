using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Agent;
using OpenHandsNET.Infrastructure.Agent.Tools;

namespace OpenHandsNET.Infrastructure.Agent;

/// <summary>
/// Static catalog of built-in tools (SPEC-09 RF-003); <c>mcp__{server}__{tool}</c>
/// names resolve to a stub bridge until SPEC-07 lands.
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly IReadOnlyList<AgentTool> _tools;

    /// <summary>Builds the built-in catalog.</summary>
    public ToolRegistry(ITaskTrackerService tracker, IOptions<AgentLlmOptions> options)
    {
        var bashTimeout = TimeSpan.FromSeconds(options.Value.BashToolTimeoutSeconds);
        _tools =
        [
            BuiltinTools.Bash(bashTimeout),
            BuiltinTools.FileRead(),
            BuiltinTools.FileWrite(),
            BuiltinTools.FileEdit(),
            BuiltinTools.Think(),
            BuiltinTools.TaskTracker(tracker),
            BuiltinTools.GitCommit(bashTimeout),
            BuiltinTools.Browser(),
            // SPEC-25 RF-003: search/web_fetch + file_read/file_edit v2.
            BuiltinTools.Search(bashTimeout),
            BuiltinTools.WebFetch(),
            BuiltinTools.InvokeSkill(),
            BuiltinTools.Finish(),
        ];
    }

    /// <inheritdoc />
    public IReadOnlyList<AgentTool> All => _tools;

    /// <inheritdoc />
    public AgentTool? Find(string name)
    {
        var hit = _tools.FirstOrDefault(t =>
            t.Schema.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (hit is not null)
        {
            return hit;
        }

        // mcp__{server}__{tool} — SPEC-07: bridge para IMcpClientService.
        if (!Application.Mcp.McpToolCatalog.TryParse(
            name, out var server, out var tool))
        {
            return null;
        }

        return new AgentTool(
            new ToolSchema(name, $"MCP tool {tool}@{server}",
                JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()),
            Risky: true,
            Handler: async (ctx, args, ct) =>
            {
                var mcp = ctx.Services.GetService<
                    Application.Mcp.IMcpClientService>();
                return mcp is null
                    ? ToolResult.Error("MCP client service indisponível")
                    : await mcp.CallToolAsync(
                        ctx.ConversationId, server, tool, args, ct);
            });
    }
}
