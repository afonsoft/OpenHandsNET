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

    /// <summary>
    /// SPEC-123: tools deferidas — fora do anúncio inicial até
    /// <c>tool_search</c> promover. Pesadas/raras (LSP, worktree, jobs,
    /// notebooks) economizam tokens de system prompt.
    /// </summary>
    public static readonly IReadOnlySet<string> DeferredNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "lsp_diagnostics", "lsp_symbols", "lsp_workspace_symbols",
            "lsp_definition", "lsp_references", "lsp_hover",
            "worktree_list", "worktree_add", "worktree_diff", "worktree_merge",
            "job_run", "job_list", "job_output", "job_kill",
            "notebook_read", "notebook_edit",
            // SPEC-128: context/memória DB-backed — sob demanda via tool_search.
            "context_save", "context_get", "context_list",
            "context_delete", "context_summary",
            "memory_get", "memory_set", "memory_search",
            // SPEC-125: segunda opinião sob demanda.
            "advisor",
        };

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
            // SPEC-121: file ops expandidas (list/grep/glob/apply_patch).
            BuiltinFileOps.FileList(),
            BuiltinFileOps.FileGrep(),
            BuiltinFileOps.FileGlob(),
            BuiltinFileOps.ApplyPatch(),
            // SPEC-121: jobs em background + git worktrees.
            BuiltinJobs.JobRun(),
            BuiltinJobs.JobList(),
            BuiltinJobs.JobOutput(),
            BuiltinJobs.JobKill(),
            BuiltinWorktree.WorktreeList(),
            BuiltinWorktree.WorktreeAdd(),
            BuiltinWorktree.WorktreeDiff(),
            BuiltinWorktree.WorktreeMerge(),
            // SPEC-123: descoberta de tools por busca.
            BuiltinToolSearch.ToolSearch(),
            // SPEC-121: specs/plans/context-notes + notebooks.
            BuiltinSpecs.Specs(),
            BuiltinSpecs.WritePlan(),
            BuiltinSpecs.ContextNotes(),
            BuiltinNotebook.NotebookRead(),
            BuiltinNotebook.NotebookEdit(),
            // SPEC-128: context-notes + memória granular DB-backed.
            BuiltinContextDb.ContextSave(),
            BuiltinContextDb.ContextGet(),
            BuiltinContextDb.ContextList(),
            BuiltinContextDb.ContextDelete(),
            BuiltinContextDb.ContextSummary(),
            BuiltinContextDb.MemorySet(),
            BuiltinContextDb.MemoryGet(),
            BuiltinContextDb.MemorySearch(),
            // SPEC-125: segunda opinião do advisor (opt-in AdvisorModel).
            BuiltinAdvisor.Advisor(),
            // SPEC-122: code intelligence LSP no sandbox.
            BuiltinLsp.LspDiagnostics(),
            BuiltinLsp.LspSymbols(),
            BuiltinLsp.LspWorkspaceSymbols(),
            BuiltinLsp.LspDefinition(),
            BuiltinLsp.LspReferences(),
            BuiltinLsp.LspHover(),
            BuiltinTools.Think(),
            BuiltinTools.TaskTracker(tracker),
            BuiltinTools.GitCommit(bashTimeout),
            // SPEC-52 RF-003: PR a partir do workspace (push + provider).
            BuiltinTools.CreatePullRequest(),
            // SPEC-43 RF-002: snapshot sob demanda (pre-refactor etc.).
            BuiltinTools.Checkpoint(),
            // SPEC-44 RF-001: pergunta ao usuário (interceptada no runner).
            BuiltinTools.AskUser(),
            BuiltinTools.Browser(),
            // SPEC-25 RF-003: search/web_fetch + file_read/file_edit v2.
            BuiltinTools.Search(bashTimeout),
            BuiltinTools.WebFetch(),
            // SPEC-30 RF-002: memória persistente (project + org).
            BuiltinTools.Memory(),
            // SPEC-47 RF-005: routines do owner da conversa.
            BuiltinTools.Routine(),
            BuiltinTools.InvokeSkill(),
            .. options.Value.Subagents.MaxDepth >= 1
                // SPEC-28: task/task_status/task_result só no nível do pai
                // (filhos nunca recebem task — depth cap via preset).
                ? (AgentTool[])[
                    BuiltinTools.SpawnTask(), BuiltinTools.TaskStatusTool(),
                    BuiltinTools.TaskResultTool(),
                    // SPEC-95 RF-003: catálogo efetivo de subagents.
                    BuiltinTools.AgentCatalogTool()]
                : [],
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