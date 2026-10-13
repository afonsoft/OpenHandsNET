using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Infrastructure.Agent.Tools;

/// <summary>
/// Built-in tool handlers over <see cref="ToolCallContext.Executor"/>
/// (SPEC-09 RF-003). <c>finish</c>/<c>think</c>/<c>task_tracker</c> are handled
/// here; <c>git_commit</c> runs inside the workspace via bash so it works before
/// SPEC-12 lands the dedicated service.
/// </summary>
public static class BuiltinTools
{
    /// <summary>Creates the bash exec tool (risky — confirmation-gated).</summary>
    public static AgentTool Bash(TimeSpan timeout) => new(
        new ToolSchema("bash",
            "Run a bash command in the workspace (/workspace). Returns exit_code, stdout and stderr.",
            Schema("""
                {"type":"object","properties":{
                  "command":{"type":"string","description":"The bash command to run"},
                  "timeout_seconds":{"type":"integer","description":"Optional timeout override"}},
                 "required":["command"]}
                """)),
        Risky: true,
        Handler: async (ctx, args, ct) =>
        {
            var command = args.GetProperty("command").GetString()!;
            var t = args.TryGetProperty("timeout_seconds", out var ts)
                ? TimeSpan.FromSeconds(Math.Clamp(ts.GetInt32(), 1, 900))
                : timeout;
            var result = await ctx.Executor.ExecAsync(ctx.Sandbox, command, t, ct);
            return ToolResult.Ok(new
            {
                exit_code = result.ExitCode,
                output = result.StdOut,
                error = result.StdErr,
                timed_out = result.TimedOut,
            });
        });

    /// <summary>Reads a workspace file (UTF-8).</summary>
    public static AgentTool FileRead() => new(
        new ToolSchema("file_read",
            "Read a UTF-8 file from the workspace.",
            Schema("""
                {"type":"object","properties":{
                  "path":{"type":"string","description":"Absolute path inside the container"}},
                 "required":["path"]}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            try
            {
                var content = await ctx.Executor.ReadFileAsync(
                    ctx.Sandbox, args.GetProperty("path").GetString()!, ct);
                return ToolResult.Ok(new { content });
            }
            catch (Exception ex)
            {
                return ToolResult.Error(ex.Message);
            }
        });

    /// <summary>Writes a workspace file (creates parents).</summary>
    public static AgentTool FileWrite() => new(
        new ToolSchema("file_write",
            "Write UTF-8 content to a file in the workspace (creates parents).",
            Schema("""
                {"type":"object","properties":{
                  "path":{"type":"string"},
                  "content":{"type":"string"}},
                 "required":["path","content"]}
                """)),
        Risky: true,
        Handler: async (ctx, args, ct) =>
        {
            try
            {
                await ctx.Executor.WriteFileAsync(ctx.Sandbox,
                    args.GetProperty("path").GetString()!,
                    args.GetProperty("content").GetString()!, ct);
                return ToolResult.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                return ToolResult.Error(ex.Message);
            }
        });

    /// <summary>String-replace edit of a workspace file.</summary>
    public static AgentTool FileEdit() => new(
        new ToolSchema("file_edit",
            "Replace old_string with new_string in a workspace file (exact match, single occurrence).",
            Schema("""
                {"type":"object","properties":{
                  "path":{"type":"string"},
                  "old_string":{"type":"string"},
                  "new_string":{"type":"string"}},
                 "required":["path","old_string","new_string"]}
                """)),
        Risky: true,
        Handler: async (ctx, args, ct) =>
        {
            try
            {
                var path = args.GetProperty("path").GetString()!;
                var oldStr = args.GetProperty("old_string").GetString()!;
                var newStr = args.GetProperty("new_string").GetString()!;
                var content = await ctx.Executor.ReadFileAsync(ctx.Sandbox, path, ct);
                var idx = content.IndexOf(oldStr, StringComparison.Ordinal);
                if (idx < 0)
                {
                    return ToolResult.Error("old_string not found in file");
                }

                await ctx.Executor.WriteFileAsync(
                    ctx.Sandbox, path,
                    content[..idx] + newStr + content[(idx + oldStr.Length)..], ct);
                return ToolResult.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                return ToolResult.Error(ex.Message);
            }
        });

    /// <summary>Internal reasoning — no-op observation.</summary>
    public static AgentTool Think() => new(
        new ToolSchema("think",
            "Record internal reasoning. Does not change the workspace.",
            Schema("""
                {"type":"object","properties":{
                  "thought":{"type":"string"}},
                 "required":["thought"]}
                """)),
        Risky: false,
        Handler: (ctx, args, ct) => Task.FromResult(
            ToolResult.Ok(new { ok = true, note = "thought recorded" })));

    /// <summary>Plan checklist → ConversationStateUpdateEvent(key=plan).</summary>
    public static AgentTool TaskTracker(ITaskTrackerService tracker) => new(
        new ToolSchema("task_tracker",
            "Update the visible task checklist (plan) for this conversation.",
            Schema("""
                {"type":"object","properties":{
                  "items":{"type":"array","items":{"type":"object","properties":{
                    "title":{"type":"string"},
                    "status":{"type":"string","enum":["todo","in_progress","done"]},
                    "notes":{"type":"string"}},
                   "required":["title","status"]}}},
                 "required":["items"]}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            var items = args.GetProperty("items").EnumerateArray()
                .Select(i => new TaskTrackerItem(
                    i.GetProperty("title").GetString()!,
                    i.GetProperty("status").GetString()!,
                    i.TryGetProperty("notes", out var n) ? n.GetString() : null))
                .ToList();
            await tracker.SetPlanAsync(ctx.ConversationId, items, ct);
            return ToolResult.Ok(new { ok = true, count = items.Count });
        });

    /// <summary>Commits the workspace repo via <c>IGitService</c> (SPEC-12).</summary>
    public static AgentTool GitCommit(TimeSpan timeout) => new(
        new ToolSchema("git_commit",
            "Stage changes in /workspace/repo and create a git commit as the agent author.",
            Schema("""
                {"type":"object","properties":{
                  "message":{"type":"string","description":"Commit message"},
                  "files":{"type":"array","items":{"type":"string"},
                    "description":"Optional explicit file list (default: all)"}},
                 "required":["message"]}
                """)),
        Risky: true,
        Handler: async (ctx, args, ct) =>
        {
            var git = ctx.Services
                .GetRequiredService<OpenHandsNET.Application.Git.IGitService>();
            var files = args.TryGetProperty("files", out var f)
                ? f.EnumerateArray().Select(e => e.GetString()!).ToList()
                : null;
            var result = await git.CommitAsync(ctx.Sandbox,
                args.GetProperty("message").GetString()!, files, ct);
            return result.Success
                ? ToolResult.Ok(new
                {
                    ok = true, hash = result.Hash, output = result.Output,
                })
                : ToolResult.Error(result.Error ?? result.Output);
        });

    /// <summary>Playwright browser — stub until SPEC-03 RF-005 lands.</summary>
    public static AgentTool Browser() => new(
        new ToolSchema("browser",
            "Interact with a web browser (not available yet).",
            Schema("""
                {"type":"object","properties":{
                  "action":{"type":"string"}},
                 "required":["action"]}
                """)),
        Risky: true,
        Handler: (ctx, args, ct) => Task.FromResult(
            ToolResult.Error("browser tool is not implemented yet (SPEC-03 RF-005)")));

    /// <summary>Skill invocation — stub until SPEC-11 lands.</summary>
    public static AgentTool InvokeSkill() => new(
        new ToolSchema("invoke_skill",
            "Invoke a named skill (not available yet).",
            Schema("""
                {"type":"object","properties":{
                  "name":{"type":"string"}},
                 "required":["name"]}
                """)),
        Risky: false,
        Handler: (ctx, args, ct) => Task.FromResult(
            ToolResult.Error("invoke_skill lands with SPEC-11")));

    /// <summary>Ends the run — handled by the loop (marker observation).</summary>
    public static AgentTool Finish() => new(
        new ToolSchema("finish",
            "Finish the task. Call when the work is complete.",
            Schema("""
                {"type":"object","properties":{
                  "final_message":{"type":"string"}},
                 "required":["final_message"]}
                """)),
        Risky: false,
        Handler: (ctx, args, ct) => Task.FromResult(ToolResult.Ok(new
        {
            ok = true,
            final_message = args.TryGetProperty("final_message", out var m)
                ? m.GetString() : null,
        })));

    private static JsonElement Schema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
