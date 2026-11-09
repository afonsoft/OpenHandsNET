namespace Sandcastle.Client.Services;

/// <summary>
/// Mapeia nomes de tools (builtins + mcp__server__tool) para linhas de
/// atividade amigáveis (SPEC-120, port OWUIC ToolActivityMap): a linha
/// mostra o que o agente está fazendo — "Searching the web", "Editing
/// file" — sem expor nome de tool nem JSON de args por padrão.
/// </summary>
public static class ToolActivityMap
{
    private const int TargetMax = 40;

    /// <summary>Campos de args candidatos a virar o alvo curto da linha,
    /// em ordem de preferência.</summary>
    private static readonly string[] TargetFields =
        ["query", "url", "path", "command", "prompt", "pattern", "task"];

    /// <summary>
    /// Resolve (key i18n, alvo curto) para uma tool_call.
    /// mcp__server__tool → sufixo após o último '__'; classificação por
    /// palavra-chave, case-insensitive.
    /// </summary>
    public static (string Key, string? Target) Map(string toolName, string? argsJson)
    {
        var suffix = toolName.StartsWith("mcp__", StringComparison.Ordinal)
            ? toolName.Split("__")[^1]
            : toolName.Split(':', '/', StringSplitOptions.None)[^1];
        var key = KeyFor(suffix.AsSpan());
        var target = Target(argsJson);
        return (key, target);
    }

    /// <summary>Somente a chave i18n da atividade.</summary>
    public static string KeyFor(string toolName) => Map(toolName, null).Key;

    private static string KeyFor(ReadOnlySpan<char> suffix)
    {
        var name = suffix.ToString().ToLowerInvariant();
        return name switch
        {
            _ when Contains(name, "web_search", "search_web", "brave",
                "google_search") => "chat.activity.web_search",
            _ when Contains(name, "fetch", "open_url", "visit", "browser")
                => "chat.activity.fetch",
            _ when Contains(name, "screenshot") => "chat.activity.screenshot",
            _ when Contains(name, "file_read", "read_file", "read", "view")
                => "chat.activity.read",
            _ when Contains(name, "file_write", "file_edit", "edit",
                "apply_patch", "write", "worktree_merge", "git_commit")
                => "chat.activity.edit",
            _ when Contains(name, "bash", "terminal", "exec", "job_",
                "run") => "chat.activity.exec",
            _ when Contains(name, "delegate", "subagent", "spawn", "team_",
                "task_status", "task_result", "task") => "chat.activity.delegate",
            _ when Contains(name, "memory", "context_") => "chat.activity.memory",
            _ when Contains(name, "file_list", "file_grep", "file_glob",
                "grep", "glob", "find", "lsp_", "worktree_diff",
                "tool_search", "agent_catalog") => "chat.activity.search_code",
            _ when Contains(name, "pull_request", "git") => "chat.activity.git",
            _ when Contains(name, "plan", "spec") => "chat.activity.plan",
            _ when Contains(name, "ask_user", "question") => "chat.activity.question",
            _ when Contains(name, "think") => "chat.activity.think",
            _ when Contains(name, "invoke_skill", "skill") => "chat.activity.skill",
            _ => "chat.activity.tool",
        };
    }

    private static bool Contains(string name, params string[] keywords) =>
        keywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>Alvo curto da linha: campo principal dos args
    /// (ToolArgsPreview) — ≤40 chars.</summary>
    private static string? Target(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
        {
            return null;
        }
        foreach (var field in TargetFields)
        {
            if (ToolArgsPreview.TryGetField(argsJson, field) is { Length: > 0 } value)
            {
                var trimmed = value.Trim();
                return trimmed.Length <= TargetMax
                    ? trimmed
                    : string.Concat(trimmed.AsSpan(0, TargetMax), "…");
            }
        }
        return null;
    }
}