using System.Text.Json;

using OpenHandsNET.Application.Agent;

namespace OpenHandsNET.Infrastructure.Agent.Tools;

/// <summary>
/// SPEC-121: file-operation tools ported from the OWUIC builtin set —
/// <c>file_list</c>, <c>file_grep</c>, <c>file_glob</c> e
/// <c>apply_patch</c> — todos executam dentro do sandbox via
/// <see cref="Application.Sandbox.ISandboxExecutor.ExecAsync"/> (nada no
/// host). Saídas limitadas (cap 200 linhas / 32KB) p/ não explodir o
/// contexto do modelo.
/// </summary>
public static class BuiltinFileOps
{
    private const int MaxLines = 200;
    private const int MaxBytes = 32 * 1024;

    /// <summary>Lists files/dirs under a workspace path (find -maxdepth).</summary>
    public static AgentTool FileList() => new(
        new ToolSchema("file_list",
            "List files and directories under a workspace path. Returns "
            + "one relative path per line (directories end with '/'), "
            + "directories first. Caps at 200 entries.",
            Schema("""
                {"type":"object","properties":{
                  "path":{"type":"string","description":"Directory inside the container (default /workspace/repo)"},
                  "max_depth":{"type":"integer","description":"Recursion depth (default 2, max 6)"}},
                 "required":[]}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            var path = Str(args, "path") ?? "/workspace/repo";
            var depth = args.TryGetProperty("max_depth", out var d)
                ? Math.Clamp(d.GetInt32(), 1, 6) : 2;
            // dirs primeiro (-type d), depois arquivos — ordenado por path.
            var cmd = $"find {Sh(path)} -maxdepth {depth} "
                + @"\( -type d \) -o -type f | sort | head -n 300";
            var r = await ExecCapAsync(ctx, cmd, ct);
            if (r.ExitCode != 0)
            {
                return ToolResult.Error(
                    $"file_list failed (exit {r.ExitCode}): {r.StdErr.Trim()}");
            }

            var lines = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var rel = lines
                .Select(l => l.Trim())
                .Select(l => l.StartsWith(path, StringComparison.Ordinal)
                    ? l[path.Length..].TrimStart('/') : l)
                .Where(l => l.Length > 0)
                .Take(MaxLines)
                .ToList();
            return ToolResult.Ok(new
            {
                path,
                entries = rel,
                truncated = lines.Length > MaxLines,
            });
        });

    /// <summary>Recursive grep with line numbers (grep -rn).</summary>
    public static AgentTool FileGrep() => new(
        new ToolSchema("file_grep",
            "Search file contents recursively (grep -rn). Returns "
            + "'path:line:match' entries, capped at 200.",
            Schema("""
                {"type":"object","properties":{
                  "pattern":{"type":"string","description":"Extended regex (grep -E)"},
                  "path":{"type":"string","description":"Directory/file inside the container (default /workspace/repo)"},
                  "include":{"type":"string","description":"Glob filter for file names (e.g. *.cs)"},
                  "case_insensitive":{"type":"boolean","description":"grep -i"}},
                 "required":["pattern"]}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            var pattern = args.GetProperty("pattern").GetString()!;
            var path = Str(args, "path") ?? "/workspace/repo";
            var include = Str(args, "include");
            var icase = args.TryGetProperty("case_insensitive", out var i)
                && i.ValueKind == JsonValueKind.True;
            var cmd = $"grep -rnE{(icase ? "i" : "")} "
                + (include is not null ? $"--include={Sh(include)} " : "")
                + $"{Sh(pattern)} {Sh(path)} 2>/dev/null | head -n {MaxLines + 1}";
            var r = await ExecCapAsync(ctx, cmd, ct);
            // grep exit 1 = zero matches — não é erro.
            if (r.ExitCode is not (0 or 1))
            {
                return ToolResult.Error(
                    $"file_grep failed (exit {r.ExitCode}): {r.StdErr.Trim()}");
            }

            var lines = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return ToolResult.Ok(new
            {
                pattern,
                path,
                matches = lines.Take(MaxLines).ToList(),
                count = Math.Min(lines.Length, MaxLines),
                truncated = lines.Length > MaxLines,
            });
        });

    /// <summary>Glob file names (find -name/-iname).</summary>
    public static AgentTool FileGlob() => new(
        new ToolSchema("file_glob",
            "Match file names by glob pattern under a directory "
            + "(find -name). Returns relative paths, capped at 200.",
            Schema("""
                {"type":"object","properties":{
                  "pattern":{"type":"string","description":"Glob pattern (e.g. *.cs, **/test_*.py)"},
                  "path":{"type":"string","description":"Base directory (default /workspace/repo)"},
                  "case_insensitive":{"type":"boolean","description":"find -iname"}},
                 "required":["pattern"]}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            var pattern = args.GetProperty("pattern").GetString()!;
            var path = Str(args, "path") ?? "/workspace/repo";
            var icase = args.TryGetProperty("case_insensitive", out var i)
                && i.ValueKind == JsonValueKind.True;
            var cmd = $"find {Sh(path)} -type f "
                + (icase ? "-iname" : "-name") + $" {Sh(pattern)} "
                + $"| sort | head -n {MaxLines + 1}";
            var r = await ExecCapAsync(ctx, cmd, ct);
            if (r.ExitCode != 0)
            {
                return ToolResult.Error(
                    $"file_glob failed (exit {r.ExitCode}): {r.StdErr.Trim()}");
            }

            var lines = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return ToolResult.Ok(new
            {
                pattern,
                path,
                files = lines.Take(MaxLines).ToList(),
                count = Math.Min(lines.Length, MaxLines),
                truncated = lines.Length > MaxLines,
            });
        });

    /// <summary>Applies a unified diff via `git apply` (fallback `patch`).</summary>
    public static AgentTool ApplyPatch() => new(
        new ToolSchema("apply_patch",
            "Apply a unified diff patch to the workspace. Runs 'git apply' "
            + "inside the container (falls back to 'patch -p1'). Returns "
            + "the list of modified files.",
            Schema("""
                {"type":"object","properties":{
                  "patch":{"type":"string","description":"Unified diff content (---/+++/@@ format)"},
                  "strip":{"type":"integer","description":"-p strip level (default 1)"},
                  "reverse":{"type":"boolean","description":"Apply in reverse (-R)"}},
                 "required":["patch"]}
                """)),
        Risky: true,
        Handler: async (ctx, args, ct) =>
        {
            var patch = args.GetProperty("patch").GetString()!;
            var strip = args.TryGetProperty("strip", out var s)
                ? Math.Clamp(s.GetInt32(), 0, 8) : 1;
            var reverse = args.TryGetProperty("reverse", out var rv)
                && rv.ValueKind == JsonValueKind.True;
            var tmp = $"/tmp/.ohn-patch-{Guid.NewGuid():N}.diff";
            await ctx.Executor.WriteFileAsync(ctx.Sandbox, tmp, patch, ct);
            var flags = $"-p{strip}{(reverse ? " -R" : "")} --verbose";
            var cmd = $"cd /workspace/repo && "
                + $"(git apply {flags} {Sh(tmp)} 2>&1 "
                + $"|| patch {flags} < {Sh(tmp)} 2>&1); "
                + $"rc=$?; rm -f {Sh(tmp)}; "
                + "git diff --name-only 2>/dev/null; exit $rc";
            var r = await ExecCapAsync(ctx, cmd, ct);
            if (r.ExitCode != 0)
            {
                return ToolResult.Error(
                    $"apply_patch failed (exit {r.ExitCode}): "
                    + $"{(r.StdOut + r.StdErr).Trim()[..Math.Min(2000, (r.StdOut + r.StdErr).Trim().Length)]}");
            }

            var files = r.StdOut.Split('\n',
                StringSplitOptions.RemoveEmptyEntries)
                .Where(l => !l.Contains("patch") || l.StartsWith('/'))
                .Take(50).ToList();
            return ToolResult.Ok(new { applied = true, files });
        });

    /// <summary>Exec com cap de saída (32KB stdout+stderr).</summary>
    private static async Task<Application.Sandbox.CommandResult> ExecCapAsync(
        ToolCallContext ctx, string cmd, CancellationToken ct)
    {
        var r = await ctx.Executor.ExecAsync(
            ctx.Sandbox, cmd, ctx.DefaultTimeout, ct);
        return r with
        {
            StdOut = Cap(r.StdOut),
            StdErr = Cap(r.StdErr),
        };
    }

    private static string Cap(string s) =>
        s.Length <= MaxBytes ? s : s[..MaxBytes] + "\n…[truncated]";

    /// <summary>Shell-quote simples ('…' com escape interno).</summary>
    private static string Sh(string v) =>
        "'" + v.Replace("'", "'\\''") + "'";

    private static string? Str(JsonElement a, string n) =>
        a.TryGetProperty(n, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static JsonElement Schema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}