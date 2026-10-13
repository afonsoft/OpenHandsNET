using System.Text;
using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Infrastructure.Agent;

/// <summary>
/// Builds the agent system prompt: persona + workspace layout + tool usage +
/// safety rules (SPEC-09 RF-002.1). Skills merge in when SPEC-11 lands.
/// </summary>
public static class SystemPromptBuilder
{
    /// <summary>Default agent persona + rules.</summary>
    public static string Build(string? selectedRepository,
        IReadOnlyList<ToolSchema> tools,
        IReadOnlyList<OpenHandsNET.Domain.Skills.Skill>? skills = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            You are OpenHandsNET, an autonomous AI software engineer running
            inside a sandboxed Linux container. Your workspace is mounted at
            /workspace — all project files live there.

            Rules:
            - Work step by step. Prefer small, verifiable actions.
            - Use the `bash` tool for shell commands and `file_read`/`file_write`/
              `file_edit` for file operations. Nothing runs outside the sandbox.
            - Use `task_tracker` to keep an up-to-date plan visible to the user.
            - Never print secrets or credentials; they are provided via
              environment variables only.
            - When the task is complete, call `finish` with a concise
              final_message summarizing what was done.
            """);
        if (!string.IsNullOrEmpty(selectedRepository))
        {
            sb.AppendLine()
                .AppendLine($"The repository '{selectedRepository}' is cloned at /workspace.");
        }

        sb.AppendLine().AppendLine("Available tools:");
        foreach (var t in tools)
        {
            sb.AppendLine($"- {t.Name}: {t.Description}");
        }

        // Skills (SPEC-11): conteúdo injetado como contexto, nunca como
        // instruções com prioridade sobre as regras acima.
        if (skills is { Count: > 0 })
        {
            sb.AppendLine().AppendLine("Additional context (skills):");
            foreach (var s in skills)
            {
                sb.AppendLine().AppendLine($"### {s.Name}").AppendLine(s.ContentMd);
            }
        }

        return sb.ToString();
    }
}
