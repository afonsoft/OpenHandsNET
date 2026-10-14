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

            // SPEC-24 RF-004: stream por chunk → command_output debounced.
            var debouncer = new OutputDebouncer(async (channel, text) =>
                await ctx.EmitAsync(new Domain.Events.CommandOutputEvent
                {
                    ToolCallId = ctx.ToolCallId ?? "",
                    ActionId = ctx.ActionId,
                    Channel = channel,
                    Text = SecretRedactor.Redact(text),
                }, CancellationToken.None));
            var result = new Application.Sandbox.CommandResult(0, "", "", false);
            var truncated = false;
            await foreach (var chunk in ctx.Executor.ExecStreamingAsync(
                ctx.Sandbox, command, t, ct))
            {
                switch (chunk)
                {
                    case Application.Sandbox.OutputChunk o:
                        await debouncer.AddAsync(o.Channel, o.Text);
                        break;
                    case Application.Sandbox.ExecCompleted done:
                        result = done.Result;
                        truncated = done.Truncated;
                        break;
                }
            }

            await debouncer.FlushAsync();
            if (truncated)
            {
                await ctx.EmitAsync(new Domain.Events.AgentDiagnosticEvent
                {
                    Severity = "warn", Code = "output_truncated",
                    Message = "command output exceeded the 2MB channel cap",
                }, CancellationToken.None);
            }

            // files_changed via git status — só quando o cwd é um repo.
            await EmitFilesChangedAsync(ctx, ct);
            return ToolResult.Ok(new
            {
                exit_code = result.ExitCode,
                output = result.StdOut,
                error = result.StdErr,
                timed_out = result.TimedOut,
            });
        });

    /// <summary>Reads a workspace file (UTF-8).</summary>
    /// <summary>
    /// SPEC-25 RF-003: <c>offset</c>/<c>limit</c> em linhas (1-based);
    /// resposta traz <c>total_lines</c>/<c>truncated</c>.
    /// </summary>
    public static AgentTool FileRead() => new(
        new ToolSchema("file_read",
            "Read a UTF-8 file from the workspace. Without offset/limit "
            + "returns up to 2000 lines; use offset+limit to page large files.",
            Schema("""
                {"type":"object","properties":{
                  "path":{"type":"string","description":"Absolute path inside the container"},
                  "offset":{"type":"integer","description":"1-based first line to return (default 1)"},
                  "limit":{"type":"integer","description":"Max lines to return (default 2000)"}},
                 "required":["path"]}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            try
            {
                var content = await ctx.Executor.ReadFileAsync(
                    ctx.Sandbox, args.GetProperty("path").GetString()!, ct);
                var lines = content.Split('\n');
                var offset = args.TryGetProperty("offset", out var o)
                    ? Math.Max(1, o.GetInt32()) : 1;
                var limit = args.TryGetProperty("limit", out var l)
                    ? Math.Clamp(l.GetInt32(), 1, 2000) : 2000;
                var slice = lines
                    .Skip(Math.Min(offset - 1, lines.Length))
                    .Take(limit)
                    .ToList();
                return ToolResult.Ok(new
                {
                    content = string.Join('\n', slice),
                    total_lines = lines.Length,
                    offset,
                    truncated = offset - 1 + slice.Count < lines.Length,
                });
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
                var path = args.GetProperty("path").GetString()!;
                var existed = true;
                try
                {
                    await ctx.Executor.ReadFileAsync(ctx.Sandbox, path, ct);
                }
                catch
                {
                    existed = false;
                }

                await ctx.Executor.WriteFileAsync(ctx.Sandbox, path,
                    args.GetProperty("content").GetString()!, ct);
                await EmitPathsAsync(ctx, "file_write",
                    [(path, existed ? "modify" : "create")], ct);
                return ToolResult.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                return ToolResult.Error(ex.Message);
            }
        });

    /// <summary>
    /// String-replace edit (SPEC-25 RF-003 — modes): <c>exact</c> (default,
    /// single occurrence of old_string), <c>anchor</c> (replace the line
    /// carrying a unique <c>anchor: &lt;text&gt;</c> marker), <c>hashline</c>
    /// (replace the line addressed by <c>lineno:sha1-prefix</c> — tolerates
    /// drift in surrounding content). Failures return a nearby-content hint.
    /// </summary>
    public static AgentTool FileEdit() => new(
        new ToolSchema("file_edit",
            "Edit a workspace file. mode=exact (default): replace the single "
            + "occurrence of old_string with new_string. mode=anchor: replace "
            + "the whole line containing the unique marker 'anchor: <anchor>'. "
            + "mode=hashline: replace the line addressed by 'lineno:hash8' "
            + "(hash8 = first 8 hex chars of sha1(line) — verifies the "
            + "expected content; omit ':hash8' to target the bare line number).",
            Schema("""
                {"type":"object","properties":{
                  "path":{"type":"string"},
                  "mode":{"type":"string","enum":["exact","anchor","hashline"],
                    "description":"Edit mode (default exact)"},
                  "old_string":{"type":"string"},
                  "new_string":{"type":"string"},
                  "anchor":{"type":"string",
                    "description":"anchor mode: marker text after 'anchor:'"},
                  "line":{"type":"string",
                    "description":"hashline mode: 'lineno' or 'lineno:hash8'"}},
                 "required":["path"]}
                """)),
        Risky: true,
        Handler: async (ctx, args, ct) =>
        {
            try
            {
                var path = args.GetProperty("path").GetString()!;
                var mode = args.TryGetProperty("mode", out var m)
                    ? m.GetString() ?? "exact" : "exact";
                var content = await ctx.Executor.ReadFileAsync(
                    ctx.Sandbox, path, ct);
                var edited = mode switch
                {
                    "exact" => EditExact(args, content),
                    "anchor" => EditAnchor(args, content),
                    "hashline" => EditHashline(args, content),
                    _ => $"__ERR__unknown mode '{mode}'",
                };
                if (edited.StartsWith("__ERR__", StringComparison.Ordinal))
                {
                    return ToolResult.Error(edited["__ERR__".Length..]
                        + Hint(content));
                }

                await ctx.Executor.WriteFileAsync(
                    ctx.Sandbox, path, edited, ct);
                // SPEC-24 RF-004 + SPEC-25: files_changed também nos modes.
                await EmitPathsAsync(ctx, "file_edit",
                    [(path, "modify")], ct);
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

    /// <summary>
    /// Headless Chromium via <see cref="IBrowserService"/> (SPEC-18 — worker-side
    /// per SPEC-03 RF-005). Every successful action returns a
    /// <c>screenshot_base64</c> that feeds the UI browser pane.
    /// </summary>
    public static AgentTool Browser() => new(
        new ToolSchema("browser",
            "Control a headless web browser: navigate pages, click elements, " +
            "fill inputs, scroll, go back/forward, take screenshots and " +
            "extract page text.",
            Schema("""
                {"type":"object","properties":{
                  "action":{"type":"string",
                    "enum":["goto","click","type","scroll","back","forward","screenshot","content"],
                    "description":"Browser action to perform"},
                  "url":{"type":"string","description":"URL for goto"},
                  "selector":{"type":"string",
                    "description":"CSS selector for click/type"},
                  "text":{"type":"string","description":"Text for type"},
                  "direction":{"type":"string","enum":["up","down"],
                    "description":"Scroll direction"}},
                 "required":["action"]}
                """)),
        Risky: true,
        Handler: async (ctx, args, ct) =>
        {
            var browser = ctx.Services.GetService<IBrowserService>();
            if (browser is null)
            {
                return ToolResult.Error("browser service unavailable");
            }

            var actionName = args.GetProperty("action").GetString()!;
            if (!Enum.TryParse<BrowserActionKind>(
                    actionName, ignoreCase: true, out var kind))
            {
                return ToolResult.Error($"unknown browser action '{actionName}'");
            }

            string? Opt(string name) => args.TryGetProperty(name, out var v)
                && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            // SPEC-24 RF-003: tool_progress nas fases da ação.
            if (ctx.ToolCallId is not null)
            {
                await ctx.EmitAsync(new Domain.Events.ToolProgressEvent
                {
                    ToolCallId = ctx.ToolCallId, ActionId = ctx.ActionId,
                    Phase = "start",
                    Message = $"browser {actionName}",
                }, CancellationToken.None);
            }

            var result = await browser.ActAsync(ctx.ConversationId,
                new BrowserActRequest(
                    kind, Opt("url"), Opt("selector"), Opt("text"),
                    Opt("direction")), ct);
            if (ctx.ToolCallId is not null)
            {
                await ctx.EmitAsync(new Domain.Events.ToolProgressEvent
                {
                    ToolCallId = ctx.ToolCallId, ActionId = ctx.ActionId,
                    Phase = result.Ok ? "done" : "error",
                    Message = result.Ok
                        ? result.Title ?? result.Url ?? "ok"
                        : result.Error ?? "failed",
                }, CancellationToken.None);
            }

            return result.Ok
                ? ToolResult.Ok(new
                {
                    ok = true, url = result.Url, title = result.Title,
                    screenshot_base64 = result.ScreenshotBase64,
                    content = result.Content,
                })
                : ToolResult.Error(result.Error ?? "browser action failed");
        });

    /// <summary>Skill invocation — injects skill content as context (SPEC-11 RF-003).</summary>
    public static AgentTool InvokeSkill() => new(
        new ToolSchema("invoke_skill",
            "Load a named skill's instructions into context.",
            Schema("""
                {"type":"object","properties":{
                  "name":{"type":"string"}},
                 "required":["name"]}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            var skills = ctx.Services
                .GetService<Application.Skills.ISkillService>();
            if (skills is null)
            {
                return ToolResult.Error("skill service unavailable");
            }

            var name = args.GetProperty("name").GetString()!;
            var skill = await skills.FindByNameAsync(name, null, null, ct);
            // SPEC-25 RF-002: db miss → tenta skill versionada no repo.
            if (skill is null
                && ctx.Services.GetService<Skills.RepoSkillProvider>()
                    is { } repo)
            {
                var rs = (await repo.LoadAsync(ctx.Sandbox, ctx.Executor, ct))
                    .FirstOrDefault(s => s.Name.Equals(name,
                        StringComparison.OrdinalIgnoreCase));
                if (rs is not null)
                {
                    var dir = System.IO.Path.GetDirectoryName(
                        rs.RepoPath!)!.Replace('\\', '/');
                    var listing = await ctx.Executor.ListFilesAsync(
                        ctx.Sandbox, dir, ct);
                    return ToolResult.Ok(new
                    {
                        name = rs.Name,
                        content = rs.ContentMd,
                        source = "repo",
                        files = listing.Select(f => f.Path).ToList(),
                    });
                }
            }

            return skill is null
                ? ToolResult.Error($"skill '{name}' not found")
                : ToolResult.Ok(new
                {
                    name = skill.Name,
                    content = skill.ContentMd,
                });
        });

    /// <summary>
    /// Workspace code search (SPEC-25 RF-003): ripgrep no sandbox
    /// (<c>rg</c> na imagem runtime), fallback <c>grep -R</c>/<c>find</c>.
    /// Saída capped (head+tail 100KB) pelo OutputPolicy do runner.
    /// </summary>
    public static AgentTool Search(TimeSpan timeout) => new(
        new ToolSchema("search",
            "Search the workspace with ripgrep. mode=content (default) shows "
            + "matching lines with numbers; mode=files lists matching paths. "
            + "Confined to the workspace — never follows symlinks outside.",
            Schema("""
                {"type":"object","properties":{
                  "query":{"type":"string",
                    "description":"ripgrep pattern (regex) or glob for mode=files"},
                  "mode":{"type":"string","enum":["content","files"],
                    "description":"content = matching lines; files = paths only"},
                  "glob":{"type":"string",
                    "description":"Optional glob filter, e.g. '*.cs'"},
                  "path":{"type":"string",
                    "description":"Workspace-relative subdir (default repo root)"},
                  "max_results":{"type":"integer","description":"Cap hits (default 50)"},
                  "context_lines":{"type":"integer",
                    "description":"Context lines around each hit (mode=content)"}},
                 "required":["query"]}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            var query = args.GetProperty("query").GetString()!;
            var mode = args.TryGetProperty("mode", out var m)
                ? m.GetString() ?? "content" : "content";
            var glob = args.TryGetProperty("glob", out var g)
                ? g.GetString() : null;
            var path = args.TryGetProperty("path", out var p)
                ? p.GetString() : null;
            var max = args.TryGetProperty("max_results", out var mr)
                ? Math.Clamp(mr.GetInt32(), 1, 500) : 50;
            var contextLines = args.TryGetProperty("context_lines", out var cl)
                ? Math.Clamp(cl.GetInt32(), 0, 10) : 0;
            var dir = string.IsNullOrEmpty(path)
                ? "/workspace/repo" : $"/workspace/repo/{path}";

            // rg confinado ao dir; fallback grep/find quando a imagem
            // não tem ripgrep (blueprints mínimas).
            var shell = ShellQuote(query);
            var globArg = glob is null ? "" : $" -g {ShellQuote(glob)}";
            var ctxArg = mode == "content" && contextLines > 0
                ? $" -C{contextLines}" : "";
            var command = mode == "files"
                ? $"cd {ShellQuote(dir)} 2>/dev/null && "
                    + $"(rg --files{globArg} 2>/dev/null | head -{max}"
                    + $" || find . -type f{GlobToFind(glob)} | head -{max})"
                : $"cd {ShellQuote(dir)} 2>/dev/null && "
                    + $"(rg --line-number --max-count {max}{ctxArg}{globArg}"
                    + $" -- {shell} 2>/dev/null || grep -Rn"
                    + $"{(glob is null ? "" : $" --include={ShellQuote(glob)}")}"
                    + $" -- {shell} . 2>/dev/null | head -{max * 8})";
            var res = await ctx.Executor.ExecAsync(
                ctx.Sandbox, command, timeout, ct);
            return res.ExitCode is 0 or 1 // rg: 1 = no matches
                ? ToolResult.Ok(new
                {
                    mode, query,
                    output = res.StdOut.Trim(),
                    matches = res.StdOut.Split('\n',
                        StringSplitOptions.RemoveEmptyEntries).Length,
                })
                : ToolResult.Error($"search failed: {res.StdErr.Trim()}");
        });

    /// <summary>
    /// Backend-side fetch (SPEC-25 RF-003): http(s) only, SSRF guard,
    /// egress allowlist aware, markdown-lite strip, redacted.
    /// </summary>
    public static AgentTool WebFetch() => new(
        new ToolSchema("web_fetch",
            "Fetch a web page through the backend (NOT the sandbox egress) "
            + "and return a markdown-lite text extract.",
            Schema("""
                {"type":"object","properties":{
                  "url":{"type":"string","description":"http(s) URL"},
                  "max_chars":{"type":"integer",
                    "description":"Response cap (default 16000)"}},
                 "required":["url"]}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            var url = args.GetProperty("url").GetString()!;
            var maxChars = args.TryGetProperty("max_chars", out var mc)
                ? Math.Clamp(mc.GetInt32(), 256, 64 * 1024) : 16_000;
            var fetch = ctx.Services.GetService<Application.Web.IWebFetchService>();
            if (fetch is null)
            {
                return ToolResult.Error("web_fetch service unavailable");
            }

            var res = await fetch.FetchAsync(url, maxChars, ct);
            return res.Ok
                ? ToolResult.Ok(new
                {
                    url = res.FinalUrl, status = res.StatusCode,
                    content = res.Text,
                })
                : ToolResult.Error(res.Error ?? "fetch failed");
        });

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

    /// <summary>
    /// SPEC-24 RF-004: files_changed determinístico para file_write/file_edit.
    /// </summary>
    private static async Task EmitPathsAsync(ToolCallContext ctx,
        string sourceTool, IReadOnlyList<(string Path, string Op)> paths,
        CancellationToken ct)
    {
        if (paths.Count == 0 || ctx.ToolCallId is null)
        {
            return;
        }

        await ctx.EmitAsync(new Domain.Events.FilesChangedEvent
        {
            ToolCallId = ctx.ToolCallId,
            ActionId = ctx.ActionId,
            SourceTool = sourceTool,
            Paths = paths.Select(p =>
                new Domain.Events.FileChange(p.Path, p.Op)).ToList(),
        }, CancellationToken.None);
    }

    /// <summary>
    /// SPEC-24 RF-004: após bash, emite files_changed do estado dirty do
    /// repo (<c>git status --porcelain</c>, best-effort — ignora quando o
    /// workspace não é git ou a chamada falha).
    /// </summary>
    private static async Task EmitFilesChangedAsync(ToolCallContext ctx,
        CancellationToken ct)
    {
        if (ctx.ToolCallId is null)
        {
            return;
        }

        try
        {
            var probe = await ctx.Executor.ExecAsync(ctx.Sandbox,
                "cd /workspace/repo 2>/dev/null && git status --porcelain=v1"
                    + " --untracked-files=normal 2>/dev/null | head -200",
                TimeSpan.FromSeconds(15), ct);
            if (probe.ExitCode != 0 || string.IsNullOrWhiteSpace(probe.StdOut))
            {
                return;
            }

            var paths = new List<(string Path, string Op)>();
            foreach (var line in probe.StdOut.Split('\n',
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length < 4)
                {
                    continue;
                }

                var xy = line[..2];
                var path = line[3..].Trim();
                var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow >= 0)
                {
                    path = path[(arrow + 4)..];
                }

                var op = xy.Contains('D') ? "delete"
                    : xy == "??" || xy.Contains('A') ? "create"
                    : "modify";
                paths.Add((path, op));
            }

            await EmitPathsAsync(ctx, "bash", paths, ct);
        }
        catch
        {
            // best-effort — nunca falha o tool call por telemetria.
        }
    }

    // ---- SPEC-25 RF-003: file_edit modes --------------------------------

    private static string EditExact(JsonElement args, string content)
    {
        var oldStr = args.GetProperty("old_string").GetString()!;
        var newStr = args.GetProperty("new_string").GetString()!;
        var idx = content.IndexOf(oldStr, StringComparison.Ordinal);
        if (idx < 0)
        {
            return "__ERR__old_string not found in file";
        }

        return content[..idx] + newStr + content[(idx + oldStr.Length)..];
    }

    private static string EditAnchor(JsonElement args, string content)
    {
        var anchor = args.GetProperty("anchor").GetString()!;
        var newStr = args.GetProperty("new_string").GetString()!;
        var lines = content.Split('\n').ToList();
        var hits = lines.Select((l, i) => (l, i))
            .Where(t => t.l.Contains($"anchor: {anchor}",
                StringComparison.Ordinal)
                || t.l.Contains($"anchor:{anchor}", StringComparison.Ordinal))
            .ToList();
        return hits.Count switch
        {
            0 => $"__ERR__anchor '{anchor}' not found",
            > 1 => $"__ERR__anchor '{anchor}' matches {hits.Count} lines "
                + "(must be unique)",
            _ => string.Join('\n', lines.Take(hits[0].i)
                .Append(newStr).Concat(lines.Skip(hits[0].i + 1))),
        };
    }

    private static string EditHashline(JsonElement args, string content)
    {
        var spec = args.GetProperty("line").GetString()!;
        var newStr = args.GetProperty("new_string").GetString()!;
        var parts = spec.Split(':', 2);
        if (!int.TryParse(parts[0], out var lineno))
        {
            return $"__ERR__invalid line spec '{spec}' (want lineno[:hash8])";
        }

        var lines = content.Split('\n').ToList();
        if (lineno < 1 || lineno > lines.Count)
        {
            return $"__ERR__line {lineno} out of range (file has "
                + $"{lines.Count} lines)";
        }

        if (parts.Length == 2)
        {
            var expect = parts[1].Trim();
            var actual = LineHash(lines[lineno - 1]);
            if (!actual.StartsWith(expect, StringComparison.OrdinalIgnoreCase))
            {
                return $"__ERR__line {lineno} hash mismatch "
                    + $"(want {expect}, got {actual[..8]}): "
                    + $"'{lines[lineno - 1].Trim()}'";
            }
        }

        lines[lineno - 1] = newStr;
        return string.Join('\n', lines);
    }

    /// <summary>sha1(line)[..8] hex — endereçável e tolerante a drift.</summary>
    internal static string LineHash(string line)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(line);
        return Convert.ToHexString(
            System.Security.Cryptography.SHA1.HashData(bytes))
            .ToLowerInvariant();
    }

    /// <summary>Single-quote escape for shell args.</summary>
    private static string ShellQuote(string s) =>
        "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>Maps a glob like <c>*.cs</c> to <c>-name '*.cs'</c>.</summary>
    private static string GlobToFind(string? glob) =>
        glob is null ? "" : $" -name {ShellQuote(glob)}";

    /// <summary>Dica de correção: primeiras linhas do arquivo (≤5).</summary>
    private static string Hint(string content)
    {
        var head = string.Join('\n',
            content.Split('\n').Take(5).Select((l, i) => $"{i + 1}: {l}"));
        return $"\nfile starts with:\n{head}";
    }

    private static JsonElement Schema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
