using System.Text.Json;

using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Infrastructure.Agent.Tools;

/// <summary>
/// SPEC-121: git-worktree tools (port OWUIC) — <c>worktree_list</c>,
/// <c>worktree_add</c>, <c>worktree_diff</c>, <c>worktree_merge</c> —
/// executados via <c>git</c> dentro do sandbox
/// (<see cref="Application.Sandbox.ISandboxExecutor.ExecAsync"/>).
/// Worktrees isolam experimentos do agente em <c>/workspace/wt-*</c>
/// fora do checkout principal.
/// </summary>
public static class BuiltinWorktree
{
    private const string Repo = "/workspace/repo";
    private const int CapBytes = 32 * 1024;

    /// <summary>Lista worktrees git registradas.</summary>
    public static AgentTool WorktreeList() => new(
        new ToolSchema("worktree_list",
            "List git worktrees (path, HEAD, branch) of the workspace repo.",
            Schema("""
                {"type":"object","properties":{},"required":[]}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            var r = await ctx.Executor.ExecAsync(ctx.Sandbox,
                $"cd {Repo} && git worktree list --porcelain",
                ctx.DefaultTimeout, ct);
            if (r.ExitCode != 0)
            {
                return ToolResult.Error(
                    $"worktree_list failed (exit {r.ExitCode}): {r.StdErr.Trim()}");
            }

            var items = new List<object>();
            string? path = null, head = null, branch = null;
            void Flush()
            {
                if (path is not null)
                {
                    items.Add(new { path, head, branch });
                }
                path = head = branch = null;
            }

            foreach (var line in r.StdOut.Split('\n'))
            {
                if (line.StartsWith("worktree ", StringComparison.Ordinal))
                {
                    path = line[9..].Trim();
                }
                else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
                {
                    head = line[5..].Trim();
                }
                else if (line.StartsWith("branch ", StringComparison.Ordinal))
                {
                    branch = line[7..].Trim();
                }
                else if (line.Length == 0)
                {
                    Flush();
                }
            }
            Flush();
            return ToolResult.Ok(new { worktrees = items });
        });

    /// <summary>Cria uma worktree em branch nova ou existente.</summary>
    public static AgentTool WorktreeAdd() => new(
        new ToolSchema("worktree_add",
            "Create a git worktree at /workspace/wt-{name} on a new or "
            + "existing branch, off HEAD (or a given ref).",
            Schema("""
                {"type":"object","properties":{
                  "name":{"type":"string","description":"Short worktree dir name (wt-{name})"},
                  "branch":{"type":"string","description":"Branch name to create/checkout (default wt-{name})"},
                  "base_ref":{"type":"string","description":"Base ref (default HEAD)"},
                  "existing":{"type":"boolean","description":"true = checkout existing branch instead of -b"}},
                 "required":["name"]}
                """)),
        Risky: true,
        Handler: async (ctx, args, ct) =>
        {
            var name = args.GetProperty("name").GetString()!;
            if (!SafeName(name))
            {
                return ToolResult.Error("name inválido — use [a-zA-Z0-9._-]");
            }

            var branch = Str(args, "branch") ?? $"wt-{name}";
            var baseRef = Str(args, "base_ref") ?? "HEAD";
            var existing = args.TryGetProperty("existing", out var e)
                && e.ValueKind == JsonValueKind.True;
            var path = $"/workspace/wt-{name}";
            var cmd = $"cd {Repo} && git worktree add {Sh(path)} "
                + (existing ? Sh(branch) : $"-b {Sh(branch)}")
                + $" {Sh(baseRef)}";
            var r = await ctx.Executor.ExecAsync(
                ctx.Sandbox, cmd, ctx.DefaultTimeout, ct);
            return r.ExitCode == 0
                ? ToolResult.Ok(new { path, branch })
                : ToolResult.Error(
                    $"worktree_add failed (exit {r.ExitCode}): {r.StdErr.Trim()}");
        });

    /// <summary>Diff de uma worktree contra o ref base.</summary>
    public static AgentTool WorktreeDiff() => new(
        new ToolSchema("worktree_diff",
            "Show the diff between a worktree and its base ref "
            + "(default: worktree HEAD vs repo HEAD merge-base).",
            Schema("""
                {"type":"object","properties":{
                  "name":{"type":"string","description":"Worktree dir name (wt-{name})"},
                  "base_ref":{"type":"string","description":"Compare ref (default merge-base com HEAD do repo)"},
                  "stat_only":{"type":"boolean","description":"true = só --stat"}},
                 "required":["name"]}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            var name = args.GetProperty("name").GetString()!;
            if (!SafeName(name))
            {
                return ToolResult.Error("name inválido");
            }

            var path = $"/workspace/wt-{name}";
            var statOnly = args.TryGetProperty("stat_only", out var s)
                && s.ValueKind == JsonValueKind.True;
            var baseArg = Str(args, "base_ref");
            var range = baseArg is not null
                ? $"{Sh(baseArg)}...HEAD"
                : "$(git merge-base HEAD $(cd " + path + " && git rev-parse HEAD 2>/dev/null || echo HEAD))...HEAD";
            var cmd = $"cd {path} && "
                + (baseArg is null
                    ? $"base=$(git merge-base HEAD $(cd {Repo} && git rev-parse HEAD)) && git diff {(statOnly ? "--stat " : "")}$base...HEAD"
                    : $"git diff {(statOnly ? "--stat " : "")}{range}")
                + $" | head -c {CapBytes}";
            var r = await ctx.Executor.ExecAsync(
                ctx.Sandbox, cmd, ctx.DefaultTimeout, ct);
            return r.ExitCode == 0
                ? ToolResult.Ok(new { worktree = name, diff = r.StdOut })
                : ToolResult.Error(
                    $"worktree_diff failed (exit {r.ExitCode}): {r.StdErr.Trim()}");
        });

    /// <summary>Mescla a branch da worktree no checkout principal.</summary>
    public static AgentTool WorktreeMerge() => new(
        new ToolSchema("worktree_merge",
            "Merge a worktree's branch into the main repo checkout, then "
            + "optionally remove the worktree.",
            Schema("""
                {"type":"object","properties":{
                  "name":{"type":"string","description":"Worktree dir name (wt-{name})"},
                  "remove":{"type":"boolean","description":"Remove worktree+branch after merge (default true)"}},
                 "required":["name"]}
                """)),
        Risky: true,
        Handler: async (ctx, args, ct) =>
        {
            var name = args.GetProperty("name").GetString()!;
            if (!SafeName(name))
            {
                return ToolResult.Error("name inválido");
            }

            var path = $"/workspace/wt-{name}";
            var remove = !args.TryGetProperty("remove", out var rm)
                || rm.ValueKind != JsonValueKind.False;
            var cmd = $"cd {path} && branch=$(git rev-parse --abbrev-ref HEAD) && "
                + $"cd {Repo} && git merge --no-ff --no-edit \"$branch\" && "
                + (remove
                    ? $"git worktree remove {Sh(path)} --force && git branch -d \"$branch\" && echo __REMOVED__"
                    : "")
                + " && echo __MERGED__ branch=$branch";
            var r = await ctx.Executor.ExecAsync(
                ctx.Sandbox, cmd, ctx.DefaultTimeout, ct);
            return r.StdOut.Contains("__MERGED__")
                ? ToolResult.Ok(new
                {
                    merged = true,
                    worktree_removed = r.StdOut.Contains("__REMOVED__"),
                    output = r.StdOut[^Math.Min(2000, r.StdOut.Length)..],
                })
                : ToolResult.Error(
                    $"worktree_merge failed (exit {r.ExitCode}): "
                    + $"{(r.StdOut + r.StdErr).Trim()[..Math.Min(2000, (r.StdOut + r.StdErr).Trim().Length)]}");
        });

    private static bool SafeName(string v) =>
        v.Length is > 0 and <= 64 && v.All(c =>
            char.IsLetterOrDigit(c) || c is '.' or '_' or '-');

    private static string? Str(JsonElement a, string n) =>
        a.TryGetProperty(n, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string Sh(string v) =>
        "'" + v.Replace("'", "'\\''") + "'";

    private static JsonElement Schema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}