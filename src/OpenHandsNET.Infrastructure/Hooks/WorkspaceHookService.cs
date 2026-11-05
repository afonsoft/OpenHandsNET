using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using OpenHandsNET.Application.Events;
using OpenHandsNET.Application.Hooks;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Domain.Hooks;
using OpenHandsNET.Infrastructure.Agent;

namespace OpenHandsNET.Infrastructure.Hooks;

/// <summary>
/// SPEC-127: <c>.openhands/hooks/*.hook.json</c> file hooks. Discovery is
/// layered — repo (<c>.openhands/hooks/</c> inside the workspace) first,
/// then user (<c>~/.openhands/hooks/</c> in the sandbox home). Command
/// hooks run through the sandbox executor; prompt hooks inject a
/// hook-sourced message into history. Every firing appends a durable
/// <see cref="HookFiredEvent"/>; a failing <c>blocking</c> command hook
/// vetoes the step.
/// </summary>
public sealed class WorkspaceHookService(
    IEventService events,
    ILogger<WorkspaceHookService> logger) : IWorkspaceHookService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private const string HookTypeCommand = "command";

    private readonly ConcurrentDictionary<Guid, CacheEntry> _cache = new();

    private sealed record CacheEntry(
        IReadOnlyList<WorkspaceHookDefinition> Hooks, DateTimeOffset LoadedAt);

    /// <inheritdoc />
    public async Task<bool> RunAsync(
        HookEvent point, HookContext context, CancellationToken ct)
    {
        var hooks = await LoadAsync(context, ct);
        if (hooks.Count == 0)
        {
            return false;
        }

        var eventName = EventName(point);
        var blocked = false;
        foreach (var hook in hooks)
        {
            if (!string.Equals(hook.Event, eventName,
                    StringComparison.OrdinalIgnoreCase)
                || !GlobMatches(hook.Matcher, context.ToolName))
            {
                continue;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var success = true;
            int? exitCode = null;
            string? error = null;
            try
            {
                if (hook.Type == "prompt")
                {
                    await InjectPromptAsync(context, hook);
                }
                else
                {
                    (success, exitCode, error) =
                        await RunCommandAsync(context, hook, ct);
                }
            }
            catch (Exception ex)
            {
                success = false;
                error = SecretRedactor.Redact(ex.Message);
                logger.LogWarning(ex,
                    "Workspace hook {Hook} failed at {Point}",
                    hook.Name, point);
            }

            sw.Stop();
            await events.AppendAsync(context.ConversationId,
                new HookFiredEvent
                {
                    HookEventType = eventName,
                    HookName = hook.Name,
                    Layer = hook.Layer,
                    Matcher = hook.Matcher,
                    HookType = hook.Type,
                    ToolName = context.ToolName,
                    Success = success,
                    ExitCode = exitCode,
                    Blocking = hook.Blocking,
                    Error = error,
                    DurationMs = sw.ElapsedMilliseconds,
                    ParentId = context.ActionId,
                }, CancellationToken.None);

            if (hook.Blocking && !success)
            {
                blocked = true;
            }
        }

        return blocked;
    }

    /// <summary>
    /// Loads hook definitions for the conversation workspace, cached with a
    /// short TTL so edits land on the next run, not mid-tool-loop.
    /// </summary>
    private async Task<IReadOnlyList<WorkspaceHookDefinition>> LoadAsync(
        HookContext context, CancellationToken ct)
    {
        if (_cache.TryGetValue(context.ConversationId, out var hit)
            && hit.LoadedAt > DateTimeOffset.UtcNow - CacheTtl)
        {
            return hit.Hooks;
        }

        var hooks = await DiscoverAsync(context, ct);
        _cache[context.ConversationId] =
            new CacheEntry(hooks, DateTimeOffset.UtcNow);
        return hooks;
    }

    /// <summary>
    /// One sandbox exec dumps every hook file (repo layer first, then user
    /// layer) as <c>===layer:name</c>-delimited bodies parsed locally.
    /// </summary>
    private async Task<IReadOnlyList<WorkspaceHookDefinition>> DiscoverAsync(
        HookContext context, CancellationToken ct)
    {
        var exec = context.Services?
            .GetService(typeof(ISandboxExecutor)) as ISandboxExecutor;
        if (context.Sandbox is null || exec is null)
        {
            return [];
        }

        const string dump =
            "shopt -s nullglob 2>/dev/null; " +
            "for f in .openhands/hooks/*.hook.json " +
            "\"$HOME/.openhands/hooks/\"*.hook.json; do " +
            "[ -f \"$f\" ] || continue; " +
            "case \"$f\" in .openhands/*) l=repo;; *) l=user;; esac; " +
            "printf '===%s:%s\\n' \"$l\" \"${f##*/}\"; cat \"$f\"; " +
            "printf '\\n'; done";

        CommandResult result;
        try
        {
            result = await exec.ExecAsync(context.Sandbox, dump,
                TimeSpan.FromSeconds(15), ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Workspace hook discovery failed");
            return [];
        }

        if (result.ExitCode != 0 || string.IsNullOrEmpty(result.StdOut))
        {
            return [];
        }

        return ParseDump(result.StdOut);
    }

    internal List<WorkspaceHookDefinition> ParseDump(string stdout)
    {
        var hooks = new List<WorkspaceHookDefinition>();
        string? layer = null;
        string? name = null;
        var body = new System.Text.StringBuilder();

        void Flush()
        {
            if (layer is null || name is null)
            {
                return;
            }

            var text = body.ToString();
            body.Clear();
            try
            {
                using var doc = JsonDocument.Parse(text);
                var h = ParseHook(doc.RootElement, layer, name);
                if (h is not null)
                {
                    hooks.Add(h);
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex,
                    "Workspace hook {Name}: invalid JSON — skipped", name);
            }
        }

        foreach (var line in stdout.Split('\n'))
        {
            if (line.StartsWith("===", StringComparison.Ordinal)
                && line.IndexOf(':', 3) is > 3 and var sep)
            {
                Flush();
                layer = line[3..sep];
                name = line[(sep + 1)..].TrimEnd('\r');
            }
            else
            {
                body.AppendLine(line);
            }
        }

        Flush();
        return hooks;
    }

    private WorkspaceHookDefinition? ParseHook(
        JsonElement el, string layer, string fileName)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var eventName = NormalizeEvent(Str(el, "event") ?? Str(el, "on"));
        if (eventName is null)
        {
            logger.LogWarning(
                "Workspace hook {Name}: unknown event '{Event}' — skipped",
                fileName, Str(el, "event"));
            return null;
        }

        var type = Str(el, "type") ?? HookTypeCommand;
        var command = Str(el, HookTypeCommand) ?? Str(el, "prompt");
        if (type is not (HookTypeCommand or "prompt")
            || string.IsNullOrWhiteSpace(command))
        {
            logger.LogWarning(
                "Workspace hook {Name}: bad type/missing command — skipped",
                fileName);
            return null;
        }

        if (type == HookTypeCommand)
        {
            command = command.Trim();
        }

        return new WorkspaceHookDefinition
        {
            Event = eventName,
            Matcher = Str(el, "matcher"),
            Type = type,
            Command = command,
            TimeoutSeconds = el.TryGetProperty("timeout", out var t)
                && t.ValueKind == JsonValueKind.Number
                && t.TryGetInt32(out var n)
                    ? Math.Clamp(n, 1, 300)
                    : 60,
            Blocking = el.TryGetProperty("blocking", out var b)
                && b.ValueKind == JsonValueKind.True,
            Name = fileName,
            Layer = layer,
        };
    }

    /// <summary>
    /// Canonical OHN event names plus OWUIC/Claude-Code aliases so hook
    /// files port over without renames.
    /// </summary>
    internal static string? NormalizeEvent(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "on_conversation_start" or "sessionstart" => "on_conversation_start",
            "on_message" or "userpromptsubmit" => "on_message",
            "on_tool_pre" or "pretooluse" => "on_tool_pre",
            "on_tool_post" or "posttooluse" => "on_tool_post",
            "on_finish" or "stop" or "sessionend" => "on_finish",
            _ => null,
        };

    private static string EventName(HookEvent e) => e switch
    {
        HookEvent.OnConversationStart => "on_conversation_start",
        HookEvent.OnMessage => "on_message",
        HookEvent.OnToolPre => "on_tool_pre",
        HookEvent.OnToolPost => "on_tool_post",
        _ => "on_finish",
    };

    /// <summary>Glob match: <c>*</c> = any run, <c>?</c> = one char;
    /// null matcher or null tool ⇒ all/none semantics per event.</summary>
    internal static bool GlobMatches(string? matcher, string? toolName)
    {
        if (matcher is null)
        {
            return true;
        }

        if (toolName is null)
        {
            return false;
        }

        var pattern = "^" + Regex.Escape(matcher)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(toolName, pattern,
            RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
    }

    private static async Task<(bool Success, int? Exit, string? Error)>
        RunCommandAsync(
            HookContext context, WorkspaceHookDefinition hook,
            CancellationToken ct)
    {
        var exec = context.Services?
            .GetService(typeof(ISandboxExecutor)) as ISandboxExecutor;
        if (context.Sandbox is null || exec is null)
        {
            return (false, null, "command hook requires a running sandbox");
        }

        var result = await exec.ExecAsync(context.Sandbox, hook.Command,
            TimeSpan.FromSeconds(hook.TimeoutSeconds), ct);
        return result.ExitCode == 0
            ? (true, result.ExitCode, null)
            : (false, result.ExitCode,
                $"exit_code={result.ExitCode}: "
                + SecretRedactor.Redact(result.StdErr));
    }

    private async Task InjectPromptAsync(
        HookContext context, WorkspaceHookDefinition hook) =>
        await events.AppendAsync(context.ConversationId,
            new MessageEvent
            {
                Source = EventSource.Hook,
                LlmMessage = JsonSerializer.SerializeToElement(new
                {
                    role = "user",
                    content = new[]
                    {
                        new { type = "text", text = hook.Command },
                    },
                }, EventJson.Options),
            }, CancellationToken.None);

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}