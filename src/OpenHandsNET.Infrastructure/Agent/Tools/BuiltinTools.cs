using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.Contracts;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Infrastructure.Agent.Subagents;

namespace OpenHandsNET.Infrastructure.Agent.Tools;

/// <summary>
/// Built-in tool handlers over <see cref="ToolCallContext.Executor"/>
/// (SPEC-09 RF-003). <c>finish</c>/<c>think</c>/<c>task_tracker</c> are handled
/// here; <c>git_commit</c> runs inside the workspace via bash so it works before
/// SPEC-12 lands the dedicated service.
/// </summary>
public static class BuiltinTools
{
    private const string Project = "project";
    private const string Content = "content";
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
                    Severity = "warn",
                    Code = "output_truncated",
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
                    args.GetProperty(Content).GetString()!, ct);
                await EmitPathsAsync(ctx, "file_write",
                    [(path, existed ? "modify" : "create")]);
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
                    [(path, "modify")]);
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
                    ok = true,
                    hash = result.Hash,
                    output = result.Output,
                })
                : ToolResult.Error(result.Error ?? result.Output);
        });

    /// <summary>
    /// SPEC-44 RF-001: <c>ask_user</c> — faz uma pergunta ao usuário e
    /// aguarda a resposta (interceptada pelo runner: status
    /// <c>waiting_for_question</c> + attention <c>question</c>; a próxima
    /// mensagem do usuário no chat é a resposta). O handler aqui é só um
    /// fallback defensivo — a execução real acontece no AgentRunner.
    /// </summary>
    public static AgentTool AskUser() => new(
        new ToolSchema("ask_user",
            "Ask the user a question and wait for their reply in chat. " +
            "Use when blocked on a decision only the user can make.",
            Schema("""
                {"type":"object","properties":{
                  "question":{"type":"string",
                    "description":"The question to ask the user"}},
                 "required":["question"]}
                """)),
        Risky: false,
        Handler: (ctx, args, ct) => Task.FromResult(
            ToolResult.Error(
                "ask_user is handled by the agent runner")));

    /// <summary>
    /// SPEC-52 RF-003: <c>create_pull_request</c> — push do work branch +
    /// PR no provider SCM da org (GitHub App). Falha limpa sem conexão.
    /// </summary>
    public static AgentTool CreatePullRequest() => new(
        new ToolSchema("create_pull_request",
            "Push the workspace work branch and open a pull request in the " +
            "repository's SCM provider (requires an org scm connection).",
            Schema("""
                {"type":"object","properties":{
                  "title":{"type":"string","description":"PR title"},
                  "body":{"type":"string","description":"PR body (markdown)"},
                  "base":{"type":"string",
                    "description":"Base branch (default: repo default)"},
                  "draft":{"type":"boolean",
                    "description":"Open as draft (default false)"}},
                 "required":["title"]}
                """)),
        Risky: true,
        Handler: async (ctx, args, ct) =>
        {
            var svc = ctx.Services.GetService<
                OpenHandsNET.Application.Scm.IScmPullRequestService>();
            if (svc is null)
            {
                return ToolResult.Error("scm pull request service unavailable");
            }

            var result = await svc.CreateAsync(
                ctx.ConversationId,
                new OpenHandsNET.Application.Scm.ScmPrCreateRequest(
                    args.GetProperty("title").GetString()!,
                    args.TryGetProperty("body", out var b)
                        ? b.GetString() ?? "" : "",
                    args.TryGetProperty("base", out var bs)
                        ? bs.GetString() : null,
                    Head: "",
                    args.TryGetProperty("draft", out var d)
                        && d.GetBoolean(),
                    Reviewers: null),
                actorUserId: null, ct);
            return result.PullRequest is { } pr
                ? ToolResult.Ok(new
                {
                    ok = true,
                    url = pr.Url,
                    number = pr.Number,
                    state = pr.State,
                })
                : ToolResult.Error($"create_pull_request failed: {result.Error}");
        });

    /// <summary>
    /// SPEC-43 RF-002: <c>checkpoint</c> — snapshot sob demanda do workdir
    /// (git-dir separado); o agente usa antes de refactors arriscados.
    /// </summary>
    public static AgentTool Checkpoint() => new(
        new ToolSchema("checkpoint",
            "Snapshot the current workspace state (/workspace/repo) as a " +
            "revertable checkpoint. Use before risky or large edits.",
            Schema("""
                {"type":"object","properties":{
                  "label":{"type":"string",
                    "description":"Short label for the snapshot (default: agent checkpoint)"}}}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            var ckpts = ctx.Services.GetService<
                OpenHandsNET.Application.Checkpoints.ICheckpointService>();
            if (ckpts is null
                || !await ckpts.IsAvailableAsync(ctx.Sandbox, ct))
            {
                return ToolResult.Error(
                    "checkpoints unavailable (no git repository)");
            }

            var label = args.TryGetProperty("label", out var l)
                && l.GetString() is { Length: > 0 } s ? s
                : "agent checkpoint";
            var item = await ckpts.SnapshotAsync(
                ctx.ConversationId, ctx.Sandbox, label, ct);
            return ToolResult.Ok(new
            {
                hash = item.Hash,
                files_changed = item.FilesChanged,
                insertions = item.Insertions,
                deletions = item.Deletions,
            });
        });

    /// <summary>
    /// SPEC-47 RF-005: <c>routine</c> — lista/cria/habilita/desabilita/
    /// deleta automations do owner da conversa (thin adapter sobre
    /// <see cref="Application.Automations.IAutomationService"/>). Mutações
    /// são risky — confirmation-mode faz o gate de <c>ask</c>.
    /// </summary>
    public static AgentTool Routine() => new(
        new ToolSchema("routine",
            "Manage this user's scheduled automations (routines). " +
            "action=list returns routines; create needs name+prompt+schedule " +
            "(interval_minutes>=5, or daily/weekly with schedule_time_utc " +
            "HH:mm, or once); enable/disable/delete need id.",
            Schema("""
                {"type":"object","properties":{
                  "action":{"type":"string",
                    "enum":["list","create","enable","disable","delete"]},
                  "id":{"type":"string","description":"Routine id (enable/disable/delete)"},
                  "name":{"type":"string"},
                  "prompt":{"type":"string","description":"Initial message for each run"},
                  "schedule_kind":{"type":"string",
                    "enum":["interval","daily","weekly","once"]},
                  "schedule_time_utc":{"type":"string","description":"HH:mm UTC (daily/weekly)"},
                  "interval_minutes":{"type":"integer"},
                  "weekday":{"type":"integer","description":"0=Sunday..6 (weekly)"},
                  "selected_repository":{"type":"string"},
                  "llm_model":{"type":"string"}},
                 "required":["action"]}
                """)),
        Risky: true,
        Handler: RoutineHandlerAsync);

    private static async Task<ToolResult> RoutineHandlerAsync(
        ToolCallContext ctx, JsonElement args, CancellationToken ct)
    {
        var db = ctx.Services.GetService<
            OpenHandsNET.Infrastructure.Persistence.AppDbContext>();
        var svc = ctx.Services.GetService<
            OpenHandsNET.Application.Automations.IAutomationService>();
        if (db is null || svc is null)
        {
            return ToolResult.Error("automations unavailable");
        }

        var conv = await db.Conversations.AsNoTracking()
            .Where(c => c.Id == ctx.ConversationId)
            .Select(c => new { c.CreatedByUserId, c.OrgId })
            .FirstOrDefaultAsync(ct);
        if (conv?.CreatedByUserId is not { } owner
            || !Guid.TryParse(owner, out var ownerId))
        {
            return ToolResult.Error("conversation owner unresolved");
        }

        return args.GetProperty("action").GetString() switch
        {
            "list" => await ListRoutinesAsync(svc, ownerId, ct),
            "create" => await CreateRoutineAsync(
                svc, args, ownerId, conv.OrgId, ct),
            "enable" or "disable" or "delete" =>
                await MutateRoutineAsync(svc, args, ownerId, ct),
            var action => ToolResult.Error($"unknown action {action}"),
        };
    }

    private static async Task<ToolResult> ListRoutinesAsync(
        OpenHandsNET.Application.Automations.IAutomationService svc,
        Guid ownerId, CancellationToken ct)
    {
        var items = await svc.ListAsync(ownerId, ct);
        return ToolResult.Ok(new
        {
            routines = items.Select(a => new
            {
                a.Id,
                a.Name,
                a.ScheduleKind,
                a.Enabled,
                a.NextRunAt,
                a.LastRunAt,
            }),
        });
    }

    private static async Task<ToolResult> CreateRoutineAsync(
        OpenHandsNET.Application.Automations.IAutomationService svc,
        JsonElement args, Guid ownerId, Guid? orgId,
        CancellationToken ct)
    {
        var req = new OpenHandsNET.Application.Automations
            .AutomationUpsert(
                Str(args, "name"), Str(args, "prompt"),
                Str(args, "llm_model"),
                Str(args, "selected_repository"),
                Str(args, "schedule_kind"),
                Str(args, "schedule_time_utc"),
                Int(args, "interval_minutes"),
                Int(args, "weekday"),
                Enabled: true, NextRunAt: null);
        var (info, error) = await svc.CreateAsync(
            req, ownerId, orgId, ct);
        return info is null
            ? ToolResult.Error(error ?? "create failed")
            : ToolResult.Ok(new
            {
                info.Id,
                info.Name,
                info.NextRunAt,
            });
    }

    private static async Task<ToolResult> MutateRoutineAsync(
        OpenHandsNET.Application.Automations.IAutomationService svc,
        JsonElement args, Guid ownerId, CancellationToken ct)
    {
        if (!Guid.TryParse(Str(args, "id"), out var id))
        {
            return ToolResult.Error("id required");
        }

        if (Str(args, "action") == "delete")
        {
            return await svc.DeleteAsync(id, ownerId, ct)
                ? ToolResult.Ok(new { deleted = id })
                : ToolResult.Error("routine not found");
        }

        var (info, error) = await svc.UpdateAsync(id,
            new OpenHandsNET.Application.Automations
                .AutomationUpsert(null, null, null, null,
                    null, null, null, null,
                    Enabled: Str(args, "action") == "enable",
                    NextRunAt: null),
            ownerId, ct);
        return info is null
            ? ToolResult.Error(error ?? "routine not found")
            : ToolResult.Ok(new
            {
                info.Id,
                info.Enabled,
                info.NextRunAt,
            });
    }

    private static string? Str(JsonElement a, string n) =>
        a.TryGetProperty(n, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static int? Int(JsonElement a, string n) =>
        a.TryGetProperty(n, out var v)
        && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : null;

    /// <summary>
    /// SPEC-30 RF-002: persistent memory — <c>project</c> scope writes
    /// <c>.openhands/memory/{slug}.md</c> + INDEX.md inside the workspace;
    /// <c>org</c> scope uses <see cref="Application.Memory.IMemoryService"/>
    /// (DB + audit). Ops: <c>remember|recall|forget|promote</c>.
    /// </summary>
    public static AgentTool Memory() => new(
        new ToolSchema("memory",
            "Persist or recall memories. scope=project writes " +
            ".openhands/memory/ in the repo (versionable); scope=org writes " +
            "the organization store (audited). promote copies project→org.",
            Schema("""
                {"type":"object","properties":{
                  "op":{"type":"string","enum":["remember","recall","forget","promote"]},
                  "scope":{"type":"string","enum":["project","org"]},
                  "key":{"type":"string","description":"Stable slug (e.g. coding-style)"},
                  "text":{"type":"string","description":"remember/promote payload"}},
                 "required":["op","key"]}
                """)),
        Risky: true,
        Handler: MemoryHandlerAsync);

    private static async Task<ToolResult> MemoryHandlerAsync(
        ToolCallContext ctx, JsonElement args, CancellationToken ct)
    {
        var op = args.GetProperty("op").GetString()!;
        var scope = args.TryGetProperty("scope", out var sc)
            ? sc.GetString() ?? Project : Project;
        var key = Slugify(args.GetProperty("key").GetString()!);
        var text = args.TryGetProperty("text", out var t)
            ? t.GetString() : null;

        // scope=org resolve a OrgId da conversa (SPEC-30 RF-001).
        var orgId = scope == "org" || op == "promote"
            ? await ResolveConvOrgIdAsync(ctx, ct)
            : null;
        var memory = ctx.Services.GetService<
            Application.Memory.IMemoryService>();
        return op switch
        {
            "remember" when scope == "org" =>
                await RememberOrgAsync(memory, orgId, key, text, ct),
            "remember" => await RememberProjectAsync(ctx, key, text, ct),
            "recall" when scope == "org" =>
                await RecallOrgAsync(memory, orgId, key, ct),
            "recall" => await RecallProjectAsync(ctx, key, ct),
            "forget" when scope == "org" =>
                await ForgetOrgAsync(memory, orgId, key, ct),
            "forget" => await ForgetProjectAsync(ctx, key, ct),
            "promote" => await PromoteAsync(ctx, memory, orgId, key, ct),
            _ => ToolResult.Error($"unknown memory op '{op}'"),
        };
    }

    private static async Task<Guid?> ResolveConvOrgIdAsync(
        ToolCallContext ctx, CancellationToken ct)
    {
        var db = ctx.Services.GetService<
            Infrastructure.Persistence.AppDbContext>();
        return db is null
            ? null
            : (await db.Conversations
                .FindAsync([ctx.ConversationId], ct))?.OrgId;
    }

    private static async Task<ToolResult> ForgetOrgAsync(
        Application.Memory.IMemoryService? memory, Guid? orgId,
        string key, CancellationToken ct) =>
        orgId is null || memory is null
            ? ToolResult.Error("org memory unavailable")
            : ToolResult.Ok(new
            {
                ok = await memory.ForgetOrgAsync(
                    orgId.Value, key, "agent", ct),
            });

    private static async Task<ToolResult> RememberOrgAsync(
        Application.Memory.IMemoryService? memory, Guid? orgId,
        string key, string? text, CancellationToken ct)
    {
        if (orgId is null || memory is null)
        {
            return ToolResult.Error("org memory unavailable (no org)");
        }

        var entry = await memory.RememberOrgAsync(
            orgId.Value, key, text ?? "", "agent", ct);
        return ToolResult.Ok(new { ok = true, key = entry.Key, scope = "org" });
    }

    private static async Task<ToolResult> RememberProjectAsync(
        ToolCallContext ctx, string key, string? text, CancellationToken ct)
    {
        var dir = "/workspace/repo/.openhands/memory";
        await ctx.Executor.ExecAsync(ctx.Sandbox,
            $"mkdir -p {dir}", TimeSpan.FromSeconds(10), ct);
        var path = $"{dir}/{key}.md";
        await ctx.Executor.WriteFileAsync(ctx.Sandbox, path,
            $"# {key}\n\n{text ?? ""}\n", ct);

        // INDEX.md: uma linha por key (ordenável, digest-friendly).
        var index = $"{dir}/INDEX.md";
        string existing = "";
        try
        {
            existing = await ctx.Executor.ReadFileAsync(
                ctx.Sandbox, index, ct);
        }
        catch (Exception) { /* primeiro write */ }

        var firstLine = (text ?? "").Split('\n')
            .FirstOrDefault()?.Trim() ?? "";
        var line = $"- {key} — {firstLine}";
        var lines = existing.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith($"- {key} ", StringComparison.Ordinal)
                && !l.Equals($"- {key}", StringComparison.Ordinal))
            .ToList();
        lines.Add(line);
        await ctx.Executor.WriteFileAsync(ctx.Sandbox, index,
            string.Join('\n', lines) + "\n", ct);
        return ToolResult.Ok(new { ok = true, key, scope = Project, path });
    }

    private static async Task<ToolResult> RecallOrgAsync(
        Application.Memory.IMemoryService? memory, Guid? orgId,
        string key, CancellationToken ct)
    {
        if (orgId is null || memory is null)
        {
            return ToolResult.Error("org memory unavailable (no org)");
        }

        var entries = await memory.ListOrgAsync(orgId.Value, key, ct);
        return ToolResult.Ok(new
        {
            entries = entries.Select(e => new
            {
                key = e.Key,
                text = e.Text,
                scope = e.Scope,
            }).ToList(),
        });
    }

    private static async Task<ToolResult> RecallProjectAsync(
        ToolCallContext ctx, string key, CancellationToken ct)
    {
        try
        {
            var path = $"/workspace/repo/.openhands/memory/{key}.md";
            var content = await ctx.Executor.ReadFileAsync(
                ctx.Sandbox, path, ct);
            return ToolResult.Ok(new
            {
                entries = new[]
                {
                    new { key, text = content, scope = Project },
                },
            });
        }
        catch (Exception)
        {
            return ToolResult.Error($"project memory '{key}' not found");
        }
    }

    private static async Task<ToolResult> ForgetProjectAsync(
        ToolCallContext ctx, string key, CancellationToken ct)
    {
        var dir = "/workspace/repo/.openhands/memory";
        await ctx.Executor.ExecAsync(ctx.Sandbox,
            $"rm -f {dir}/{key}.md", TimeSpan.FromSeconds(10), ct);
        try
        {
            var index = $"{dir}/INDEX.md";
            var existing = await ctx.Executor.ReadFileAsync(
                ctx.Sandbox, index, ct);
            var lines = existing.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(l => !l.StartsWith($"- {key} ", StringComparison.Ordinal)
                    && !l.Equals($"- {key}", StringComparison.Ordinal))
                .ToList();
            await ctx.Executor.WriteFileAsync(ctx.Sandbox, index,
                string.Join('\n', lines) + "\n", ct);
        }
        catch (Exception) { /* sem INDEX */ }

        return ToolResult.Ok(new { ok = true, key, scope = Project });
    }

    private static async Task<ToolResult> PromoteAsync(
        ToolCallContext ctx, Application.Memory.IMemoryService? memory,
        Guid? orgId, string key, CancellationToken ct)
    {
        var recalled = await RecallProjectAsync(ctx, key, ct);
        if (recalled.IsError)
        {
            return recalled;
        }

        var text = recalled.Observation
            .GetProperty("entries")[0].GetProperty("text").GetString() ?? "";
        return await RememberOrgAsync(memory, orgId, key, text, ct);
    }

    private static string Slugify(string key)
    {
        var s = string.Concat(key.Trim().ToLowerInvariant().Select(
            c => char.IsLetterOrDigit(c) ? c : '-'));
        while (s.Contains("--", StringComparison.Ordinal))
        {
            s = s.Replace("--", "-", StringComparison.Ordinal);
        }

        return s.Trim('-')[..Math.Min(s.Trim('-').Length, 80)];
    }

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
                    "enum":["goto","click","type","scroll","back","forward","screenshot","content","totp"],
                    "description":"Browser action to perform"},
                  "name":{"type":"string",
                    "description":"TOTP secret name for totp action (resolves secret 'totp:<name>')"},
                  "url":{"type":"string","description":"URL for goto"},
                  "selector":{"type":"string",
                    "description":"CSS selector for click/type"},
                  "text":{"type":"string","description":"Text for type"},
                  "direction":{"type":"string","enum":["up","down"],
                    "description":"Scroll direction"}},
                 "required":["action"]}
                """)),
        Risky: true,
        Handler: BrowserHandlerAsync);

    private static async Task<ToolResult> BrowserHandlerAsync(
        ToolCallContext ctx, JsonElement args, CancellationToken ct)
    {
        var browser = ctx.Services.GetService<IBrowserService>();
        if (browser is null)
        {
            return ToolResult.Error("browser service unavailable");
        }

        var actionName = args.GetProperty("action").GetString()!;
        // SPEC-64 RF-004: TOTP — o código é computado aqui a
        // partir do segredo 'totp:<name>'; o segredo nunca sai
        // do vault e só o código efêmero vai ao input.
        if (actionName.Equals("totp", StringComparison.OrdinalIgnoreCase))
        {
            return await BrowserTotpAsync(ctx, args, ct);
        }
        if (!Enum.TryParse<BrowserActionKind>(
                actionName, ignoreCase: true, out var kind))
        {
            return ToolResult.Error($"unknown browser action '{actionName}'");
        }

        // SPEC-24 RF-003: tool_progress nas fases da ação.
        await EmitProgressAsync(ctx, "start", $"browser {actionName}");

        var result = await browser.ActAsync(ctx.ConversationId,
            new BrowserActRequest(
                kind, Opt(args, "url"), Opt(args, "selector"),
                Opt(args, "text"), Opt(args, "direction")), ct);
        await EmitProgressAsync(ctx, result.Ok ? "done" : "error",
            result.Ok
                ? result.Title ?? result.Url ?? "ok"
                : result.Error ?? "failed");

        return result.Ok
            ? ToolResult.Ok(new
            {
                ok = true,
                url = result.Url,
                title = result.Title,
                screenshot_base64 = result.ScreenshotBase64,
                content = result.Content,
            })
            : ToolResult.Error(result.Error ?? "browser action failed");
    }

    private static string? Opt(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static async Task EmitProgressAsync(
        ToolCallContext ctx, string phase, string message)
    {
        if (ctx.ToolCallId is null)
        {
            return;
        }

        await ctx.EmitAsync(new Domain.Events.ToolProgressEvent
        {
            ToolCallId = ctx.ToolCallId,
            ActionId = ctx.ActionId,
            Phase = phase,
            Message = message,
        }, CancellationToken.None);
    }

    /// <summary>
    /// SPEC-64 RF-004: resolve <c>totp:{name}</c> e devolve o código
    /// corrente. O segredo base32 nunca sai do vault; o retorno ao
    /// LLM é só o código efêmero.
    /// </summary>
    private static async Task<ToolResult> BrowserTotpAsync(
        ToolCallContext ctx, JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("name", out var n)
            || n.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(n.GetString()))
        {
            return ToolResult.Error("totp requires name");
        }
        var secrets = ctx.Services.GetService<ISecretStore>();
        if (secrets is null)
        {
            return ToolResult.Error("secret store unavailable");
        }
        var current = ctx.Services.GetService<ICurrentUserAccessor>();
        var ownerId = current?.User?.UserId.ToString() ?? "user";
        var b32 = await secrets.ResolveAsync(
            "user", ownerId, $"totp:{n.GetString()}", ct);
        if (b32 is null)
        {
            return ToolResult.Error(
                $"totp secret 'totp:{n.GetString()}' not found");
        }
        string code;
        try
        {
            code = BrowserProfiles.TotpGenerator.Generate(b32);
        }
        catch (ArgumentException)
        {
            return ToolResult.Error("stored totp secret is not valid base32");
        }
        var audit = ctx.Services.GetService<IAuditService>();
        if (audit is not null)
        {
            await audit.RecordAsync("browser.totp_used",
                actorUserId: ownerId, metadata: new
                {
                    secret = $"totp:{n.GetString()}",
                }, cancellationToken: ct);
        }
        return ToolResult.Ok(new { code });
    }

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
                ? m.GetString() ?? Content : Content;
            var max = args.TryGetProperty("max_results", out var mr)
                ? Math.Clamp(mr.GetInt32(), 1, 500) : 50;
            var res = await ctx.Executor.ExecAsync(
                ctx.Sandbox, BuildSearchCommand(args, mode, max), timeout, ct);
            return res.ExitCode is 0 or 1 // rg: 1 = no matches
                ? ToolResult.Ok(new
                {
                    mode,
                    query,
                    output = res.StdOut.Trim(),
                    matches = res.StdOut.Split('\n',
                        StringSplitOptions.RemoveEmptyEntries).Length,
                })
                : ToolResult.Error($"search failed: {res.StdErr.Trim()}");
        });

    /// <summary>
    /// rg confinado ao dir; fallback grep/find quando a imagem
    /// não tem ripgrep (blueprints mínimas).
    /// </summary>
    private static string BuildSearchCommand(
        JsonElement args, string mode, int max)
    {
        var query = args.GetProperty("query").GetString()!;
        var glob = args.TryGetProperty("glob", out var g)
            ? g.GetString() : null;
        var path = args.TryGetProperty("path", out var p)
            ? p.GetString() : null;
        var contextLines = args.TryGetProperty("context_lines", out var cl)
            ? Math.Clamp(cl.GetInt32(), 0, 10) : 0;
        var dir = string.IsNullOrEmpty(path)
            ? "/workspace/repo" : $"/workspace/repo/{path}";

        var shell = ShellQuote(query);
        var globArg = glob is null ? "" : $" -g {ShellQuote(glob)}";
        var ctxArg = mode == Content && contextLines > 0
            ? $" -C{contextLines}" : "";
        return mode == "files"
            ? $"cd {ShellQuote(dir)} 2>/dev/null && "
                + $"(rg --files{globArg} 2>/dev/null | head -{max}"
                + $" || find . -type f{GlobToFind(glob)} | head -{max})"
            : $"cd {ShellQuote(dir)} 2>/dev/null && "
                + $"(rg --line-number --max-count {max}{ctxArg}{globArg}"
                + $" -- {shell} 2>/dev/null || grep -Rn"
                + $"{(glob is null ? "" : $" --include={ShellQuote(glob)}")}"
                + $" -- {shell} . 2>/dev/null | head -{max * 8})";
    }

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
                    url = res.FinalUrl,
                    status = res.StatusCode,
                    content = res.Text,
                })
                : ToolResult.Error(res.Error ?? "fetch failed");
        });

    /// <summary>
    /// SPEC-28 RF-001/RF-003: <c>task</c> — spawns a subagent in the same
    /// sandbox. Sync by default; <c>background:true</c> queues and returns
    /// <c>task_id</c> for <c>task_status</c>/<c>task_result</c>.
    /// </summary>
    public static AgentTool SpawnTask() => new(
        new ToolSchema("task",
            "Spawn a subagent to explore, research or work on a scoped task "
            + "in this same sandbox. Use subagent_type to restrict its "
            + "tools: explore (search/file_read/bash), scout (web_fetch/"
            + "search/file_read), plan (read-only), general (all except "
            + "task). The parent only receives the final summary.",
            Schema("""
                {"type":"object","properties":{
                  "description":{"type":"string",
                    "description":"short label for the run"},
                  "prompt":{"type":"string",
                    "description":"complete instructions for the subagent"},
                  "subagent_type":{"type":"string",
                    "enum":["explore","scout","plan","general"],
                    "default":"explore"},
                  "background":{"type":"boolean","default":false,
                    "description":"true = run async, poll task_status"}},
                 "required":["prompt"]}
                """)),
        Risky: false,
        Handler: SpawnTaskHandlerAsync);

    private static async Task<ToolResult> SpawnTaskHandlerAsync(
        ToolCallContext ctx, JsonElement args, CancellationToken ct)
    {
        var host = ctx.Services.GetService<ISubagentHost>();
        var llm = ctx.Services.GetService<IAgentLlmProvider>();
        var opt = ctx.Services.GetService<
            Microsoft.Extensions.Options.IOptions<AgentLlmOptions>>()?.Value
            ?? new AgentLlmOptions();
        if (host is null || llm is null)
        {
            return ToolResult.Error("subagents unavailable");
        }

        var prompt = args.TryGetProperty("prompt", out var p)
            ? p.GetString() : null;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return ToolResult.Error("task requires a prompt");
        }

        var type = args.TryGetProperty("subagent_type", out var st)
            ? st.GetString() ?? "explore" : "explore";
        var (agentDef, typeError) = await ResolveAgentDefAsync(
            ctx, type, ct);
        if (typeError is not null)
        {
            return typeError;
        }

        var description = args.TryGetProperty("description", out var d)
            ? d.GetString() ?? type : type;
        var background = args.TryGetProperty("background", out var bg)
            && bg.ValueKind == JsonValueKind.True;
        var taskId = Ulid.NewUlid().ToString();
        var run = new SubagentRun
        {
            Info = new SubagentRunInfo(
                taskId, type, description, SubagentStatus.Running,
                DateTimeOffset.UtcNow),
        };
        if (!host.TryStart(ctx.ConversationId, run,
                Math.Max(1, opt.Subagents.MaxConcurrent)))
        {
            return ToolResult.Error(
                "subagent concurrency limit reached");
        }

        var runner = new Subagents.SubagentRunner(llm,
            Microsoft.Extensions.Options.Options.Create(opt));
        Task<string> ExecuteAsync(CancellationToken runCt) =>
            RunSubagentAsync(ctx, host, runner, taskId, type,
                description, prompt, agentDef, runCt);

        if (background)
        {
            run.Cts = new CancellationTokenSource();
            run.Completion = Task.Run(
                () => ExecuteAsync(run.Cts.Token));
            return ToolResult.Ok(new
            {
                queued = true,
                task_id = taskId,
                subagent_type = type,
            });
        }

        try
        {
            var result = await ExecuteAsync(ct);
            return ToolResult.Ok(new
            {
                task_id = taskId,
                status = "completed",
                result,
            });
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error("subagent aborted");
        }
        catch (Exception ex)
        {
            return ToolResult.Error(
                $"subagent failed: {SecretRedactor.Redact(ex.Message)}");
        }
    }

    /// <summary>
    /// SPEC-95 RF-004: presets built-in primeiro; senão, catálogo
    /// declarativo (mesma fonte que alimenta o prompt).
    /// </summary>
    private static async Task<(AgentDefinition? Def, ToolResult? Error)>
        ResolveAgentDefAsync(ToolCallContext ctx, string type,
            CancellationToken ct)
    {
        if (SubagentPresets.IsKnown(type))
        {
            return (null, null);
        }

        var catalog = ctx.Services.GetService<IAgentCatalog>();
        var agentDef = catalog is null
            ? null
            : await catalog.FindAsync(
                type, ctx.Sandbox, ctx.Executor, ct);
        if (agentDef is not null)
        {
            return (agentDef, null);
        }

        var valid = catalog is null
            ? string.Join(", ", SubagentPresets.Names)
            : string.Join(", ", await catalog.ListNamesAsync(
                ctx.Sandbox, ctx.Executor, ct));
        return (null, ToolResult.Error(
            $"unknown subagent_type '{type}' "
            + $"— válidos: {valid}"));
    }

    private static async Task<string> RunSubagentAsync(
        ToolCallContext ctx, ISubagentHost host,
        Subagents.SubagentRunner runner, string taskId, string type,
        string description, string prompt, AgentDefinition? agentDef,
        CancellationToken runCt)
    {
        try
        {
            var result = await runner.RunAsync(
                ctx.ConversationId, ctx.Sandbox, ctx.Executor,
                ctx.Events, ctx.Services,
                ctx.ActionId ?? ctx.ToolCallId ?? taskId,
                type, description, prompt, taskId, runCt,
                agent: agentDef);
            host.Finish(ctx.ConversationId, taskId,
                SubagentStatus.Completed, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            host.Finish(ctx.ConversationId, taskId,
                SubagentStatus.Aborted, error: "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            host.Finish(ctx.ConversationId, taskId,
                SubagentStatus.Failed,
                error: SecretRedactor.Redact(ex.Message));
            throw;
        }
    }

    /// <summary>
    /// SPEC-95 RF-003: <c>agent_catalog</c> — lista o catálogo efetivo
    /// (builtin &lt; user &lt; workspace) para o modelo escolher um
    /// <c>subagent_type</c> antes de delegar.
    /// </summary>
    public static AgentTool AgentCatalogTool() => new(
        new ToolSchema("agent_catalog",
            "List the subagents available for delegation as "
            + "'name — description (source)'. Query this before choosing "
            + "a subagent_type for the task tool.",
            Schema("""
                {"type":"object","properties":{}}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            var catalog = ctx.Services.GetService<IAgentCatalog>();
            if (catalog is null)
            {
                return ToolResult.Error("agent catalog unavailable");
            }
            var agents = await catalog.ListAsync(
                ctx.Sandbox, ctx.Executor, ct);
            return ToolResult.Ok(new
            {
                catalog = string.Join("\n", agents.Select(
                    a => $"{a.Name} — {a.Description} ({a.Source})")),
            });
        });

    /// <summary>SPEC-28 RF-003: <c>task_status</c> — poll a background run.</summary>
    public static AgentTool TaskStatusTool() => new(
        new ToolSchema("task_status",
            "Check the status of a background subagent run by task_id.",
            Schema("""
                {"type":"object","properties":{
                  "task_id":{"type":"string"}},
                 "required":["task_id"]}
                """)),
        Risky: false,
        Handler: (ctx, args, ct) =>
        {
            var host = ctx.Services.GetService<ISubagentHost>();
            var id = args.TryGetProperty("task_id", out var t)
                ? t.GetString() : null;
            var run = host is not null && id is not null
                ? host.Get(ctx.ConversationId, id) : null;
            return Task.FromResult(run is null
                ? ToolResult.Error($"unknown task_id '{id}'")
                : ToolResult.Ok(new
                {
                    task_id = id,
                    status = run.Info.Status.ToString().ToLowerInvariant(),
                    subagent_type = run.Info.SubagentType,
                    description = run.Info.Description,
                    started_at = run.Info.StartedAt,
                    ended_at = run.Info.EndedAt,
                    error = run.Info.Error,
                }));
        });

    /// <summary>SPEC-28 RF-003: <c>task_result</c> — collect a finished run.</summary>
    public static AgentTool TaskResultTool() => new(
        new ToolSchema("task_result",
            "Get the result summary of a completed subagent run by task_id.",
            Schema("""
                {"type":"object","properties":{
                  "task_id":{"type":"string"}},
                 "required":["task_id"]}
                """)),
        Risky: false,
        Handler: (ctx, args, ct) =>
        {
            var host = ctx.Services.GetService<ISubagentHost>();
            var id = args.TryGetProperty("task_id", out var t)
                ? t.GetString() : null;
            var run = host is not null && id is not null
                ? host.Get(ctx.ConversationId, id) : null;
            if (run is null)
            {
                return Task.FromResult(
                    ToolResult.Error($"unknown task_id '{id}'"));
            }

            return Task.FromResult(run.Info.Status switch
            {
                SubagentStatus.Completed => ToolResult.Ok(new
                {
                    task_id = id,
                    status = "completed",
                    result = run.Info.Result,
                }),
                SubagentStatus.Running => ToolResult.Ok(new
                {
                    task_id = id,
                    status = "running",
                }),
                _ => ToolResult.Error(
                    $"subagent {run.Info.Status.ToString().ToLowerInvariant()}"
                    + (run.Info.Error is not null
                        ? $": {run.Info.Error}" : "")),
            });
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
        string sourceTool, IReadOnlyList<(string Path, string Op)> paths)
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

            await EmitPathsAsync(ctx, "bash", paths);
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

    /// <summary>sha256(line) hex — endereçável e tolerante a drift.</summary>
    internal static string LineHash(string line)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(line);
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(bytes))
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