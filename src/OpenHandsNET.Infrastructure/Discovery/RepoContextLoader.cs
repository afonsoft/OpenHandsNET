using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using OpenHandsNET.Application.Discovery;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Domain.Skills;
using OpenHandsNET.Infrastructure.Conversations;
using OpenHandsNET.Infrastructure.Persistence;
using OpenHandsNET.Infrastructure.Skills;

using RuntimeSandbox = OpenHandsNET.Domain.Sandbox.Sandbox;
using SandboxStatus = OpenHandsNET.Domain.Sandbox.SandboxStatus;

namespace OpenHandsNET.Infrastructure.Discovery;

/// <summary>
/// SPEC-25 RF-001/002: at prompt-build time, resolves the discovered repo
/// context — instruction file contents (≤64KB each) + repo skills —
/// and renders the <c>## Repository context</c> section. The manifest
/// comes from the latest <c>repo_context</c> state event; legacy
/// conversations (no event) get a live discovery.
/// </summary>
public sealed class RepoContextLoader(
    IRepoDiscoveryService discovery,
    RepoSkillProvider repoSkills,
    Application.Sandbox.ISandboxService sandboxes,
    ISandboxExecutor executor,
    ILogger<RepoContextLoader> logger,
    Application.Memory.IMemoryService? memory = null)
{
    private const int InstructionCapBytes = 64 * 1024;
    private const int ReadmeCapBytes = 8 * 1024;

    /// <summary>Rendered section + merged repo skills for one build.</summary>
    public sealed record LoadedContext(
        string? RenderedMarkdown, IReadOnlyList<Skill> RepoSkills);

    /// <summary>
    /// Loads repo context for the prompt. <paramref name="events"/> is the
    /// event stream already materialized by the caller (manifest lookup is
    /// free); file contents are read live so edits stay fresh.
    /// </summary>
    public async Task<LoadedContext> LoadAsync(
        ConversationRecord conv, IReadOnlyList<EventBase> events,
        CancellationToken ct)
    {
        if (conv.SandboxId is not { } sandboxId)
        {
            return new LoadedContext(null, []);
        }

        var sandbox = await sandboxes.GetAsync(sandboxId, ct);
        if (sandbox is null || sandbox.Status != SandboxStatus.Running)
        {
            return new LoadedContext(null, []);
        }

        var manifest = LatestManifest(events)
            ?? await discovery.DiscoverAsync(executor, sandbox, ct);
        var skills = await repoSkills.LoadAsync(sandbox, executor, ct);

        var sb = new StringBuilder();
        if (manifest.InstructionFiles.Count > 0)
        {
            sb.AppendLine("<repo_instructions>");
            foreach (var file in manifest.InstructionFiles)
            {
                var content = await ReadCappedAsync(
                    sandbox, $"/workspace/repo/{file}",
                    InstructionCapBytes, ct);
                if (content is null)
                {
                    continue;
                }

                sb.AppendLine($"<file path=\"{file}\">")
                    .AppendLine(content)
                    .AppendLine("</file>");
            }

            sb.AppendLine("</repo_instructions>");
        }

        if (manifest.ReadmePath is { } readme)
        {
            var content = await ReadCappedAsync(
                sandbox, $"/workspace/repo/{readme}", ReadmeCapBytes, ct);
            if (content is not null)
            {
                sb.AppendLine("<repo_readme>").AppendLine(content)
                    .AppendLine("</repo_readme>");
            }
        }

        if (skills.Count > 0)
        {
            sb.AppendLine("Repository skills (invoke via `invoke_skill`):");
            foreach (var s in skills)
            {
                sb.AppendLine($"- {s.Name} ({s.RepoPath})"
                    + (s.Description is { } d ? $" — {d}" : ""));
            }
        }

        if (manifest.HasRepoConfig)
        {
            sb.AppendLine(
                "The repository declares an `openhandsnet.yaml` — its setup"
                + " already ran at sandbox provisioning (blueprint).");
        }

        // SPEC-30 RF-003: digest de memória (project INDEX + org digest) —
        // untrusted, citado; estável entre turns (não é podado pela SPEC-22).
        await AppendMemoryAsync(sb, conv, sandbox, ct);

        var rendered = sb.Length > 0 ? sb.ToString() : null;
        return new LoadedContext(rendered, skills);
    }

    private RepoContextManifest? LatestManifest(IReadOnlyList<EventBase> events)
    {
        var e = events.OfType<ConversationStateUpdateEvent>()
            .LastOrDefault(s => s.Key == "repo_context");
        if (e?.Value is not { } v || v.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            var ins = v.TryGetProperty("instructions_files", out var i)
                && i.ValueKind == JsonValueKind.Array
                ? i.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!).ToList()
                : [];
            var skills = v.TryGetProperty("skills", out var s)
                && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray().Select(x => new RepoSkillRef(
                    x.TryGetProperty("name", out var n)
                        ? n.GetString() ?? "" : "",
                    x.TryGetProperty("path", out var p)
                        ? p.GetString() ?? "" : "",
                    x.TryGetProperty("description", out var d)
                        ? d.GetString() : null)).ToList()
                : [];
            var config = v.TryGetProperty("config", out var c)
                && c.ValueKind == JsonValueKind.True;
            var readme = v.TryGetProperty("readme", out var r)
                ? r.GetString() : null;
            return new RepoContextManifest(ins, skills, config, readme);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "repo_context manifest inválido");
            return null;
        }
    }

    /// <summary>
    /// SPEC-30 RF-003: renders the <c>## Memory</c> section — project
    /// <c>.openhands/memory/INDEX.md</c> + newest org entries (cap 4KB total).
    /// </summary>
    private async Task AppendMemoryAsync(
        StringBuilder sb, ConversationRecord conv,
        RuntimeSandbox sandbox, CancellationToken ct)
    {
        var block = new StringBuilder();
        try
        {
            var index = await ReadCappedAsync(sandbox,
                "/workspace/repo/.openhands/memory/INDEX.md", 2048, ct);
            if (index is not null)
            {
                block.AppendLine("<memory scope=\"project\">")
                    .AppendLine(index).AppendLine("</memory>");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        if (memory is not null && conv.OrgId is { } orgId)
        {
            try
            {
                var entries = await memory.DigestAsync(orgId, 10, ct);
                if (entries.Count > 0)
                {
                    block.AppendLine("<memory scope=\"org\">");
                    foreach (var e in entries)
                    {
                        var first = e.Text.Split('\n')
                            .FirstOrDefault()?.Trim() ?? "";
                        block.AppendLine($"- {e.Key} — {first}");
                    }

                    block.AppendLine("</memory>");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "memory digest indisponível");
            }
        }

        if (block.Length > 0)
        {
            var s = block.ToString();
            sb.AppendLine("## Memory")
                .AppendLine(s.Length <= 4096
                    ? s : s[..4096] + "\n[…truncated…]");
        }
    }

    private async Task<string?> ReadCappedAsync(
        RuntimeSandbox sandbox, string path, int capBytes,
        CancellationToken ct)
    {
        try
        {
            var content = await executor.ReadFileAsync(sandbox, path, ct);
            return content.Length <= capBytes
                ? content
                : content[..capBytes] + "\n[…truncated…]";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}