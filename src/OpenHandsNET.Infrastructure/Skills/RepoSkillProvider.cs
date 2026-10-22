using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Skills;

using RuntimeSandbox = OpenHandsNET.Domain.Sandbox.Sandbox;

namespace OpenHandsNET.Infrastructure.Skills;

/// <summary>
/// SPEC-25 RF-002: loads repo-versioned skills (<c>SKILL.md</c> under
/// conventional dirs) from the workspace via the sandbox executor.
/// Read-only, conversation-scoped, cached per sandbox for the run —
/// <see cref="Invalidate"/> drops the cache (rescan).
/// </summary>
public sealed class RepoSkillProvider(ILogger<RepoSkillProvider> logger)
{
    /// <summary>Conventional repo skill dirs (repo-root relative).</summary>
    public static readonly string[] SkillDirs =
    [
        ".openhands/skills", ".devin/skills",
        ".agents/skills", ".claude/skills",
    ];

    private const string RepoRoot = "/workspace/repo";
    private const int MaxSkillBytes = 64 * 1024;

    private readonly ConcurrentDictionary<Guid, IReadOnlyList<Skill>> _cache =
        new();

    /// <summary>
    /// All repo skills of the sandbox (cached). <paramref name="executor"/>
    /// é parâmetro (scoped) — o provider é singleton pelo cache por sandbox.
    /// </summary>
    public async Task<IReadOnlyList<Skill>> LoadAsync(
        RuntimeSandbox sandbox, ISandboxExecutor executor,
        CancellationToken ct)
    {
        if (_cache.TryGetValue(sandbox.Id, out var cached))
        {
            return cached;
        }

        cached = _cache[sandbox.Id] = await ScanAsync(sandbox, executor, ct);
        return cached;
    }

    /// <summary>Drops the cached manifest (context rescan).</summary>
    public void Invalidate(Guid sandboxId) => _cache.TryRemove(sandboxId, out _);

    private async Task<IReadOnlyList<Skill>> ScanAsync(
        RuntimeSandbox sandbox, ISandboxExecutor executor,
        CancellationToken ct)
    {
        var skills = new List<Skill>();
        foreach (var dir in SkillDirs)
        {
            IReadOnlyList<FileEntry> entries;
            try
            {
                entries = await executor.ListFilesAsync(
                    sandbox, $"{RepoRoot}/{dir}", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue; // dir ausente — normal.
            }

            foreach (var skillPath in entries
                .Where(e => e.IsDirectory)
                .Select(e => $"{RepoRoot}/{dir}/{e.Path.TrimEnd('/')}/SKILL.md"))
            {
                try
                {
                    var md = await executor.ReadFileAsync(
                        sandbox, skillPath, ct);
                    if (md.Length > MaxSkillBytes
                        || string.IsNullOrWhiteSpace(md))
                    {
                        continue;
                    }

                    var (name, desc, body) = ParseSkillMd(md);
                    skills.Add(new Skill
                    {
                        Id = Guid.NewGuid(),
                        Name = name ?? Path.GetFileName(
                            skillPath.TrimEnd('/')),
                        Description = desc,
                        ContentMd = body,
                        Source = SkillSource.Repo,
                        RepoPath = skillPath,
                        CreatedAt = DateTimeOffset.UtcNow,
                    });
                }
                catch (Exception ex2)
                    when (ex2 is not OperationCanceledException)
                {
                    logger.LogDebug(ex2, "repo skill {Path} ilegível",
                        skillPath);
                }
            }
        }

        logger.LogInformation(
            "repo skills no sandbox {Id}: {Count}", sandbox.Id, skills.Count);
        return skills;
    }

    /// <summary>
    /// Minimal YAML frontmatter parse — <c>name</c>/<c>description</c>
    /// entre fences <c>---</c>; corpo vira ContentMd.
    /// </summary>
    internal static (string? Name, string? Description, string Body)
        ParseSkillMd(string md)
    {
        var span = md.AsSpan().TrimStart('\uFEFF');
        if (!span.StartsWith("---"))
        {
            return (null, null, md.Trim());
        }

        var nl = span.IndexOf('\n');
        if (nl < 0)
        {
            return (null, null, md.Trim());
        }

        var rest = span[(nl + 1)..];
        var end = rest.ToString().IndexOf("\n---", StringComparison.Ordinal);
        if (end < 0)
        {
            return (null, null, md.Trim());
        }

        var fm = rest[..end].ToString();
        var body = rest[(end + 4)..].ToString().Trim();
        string? name = null, desc = null;
        foreach (var line in fm.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var val = line[(colon + 1)..].Trim().Trim('"', '\'');
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                name = val;
            }
            else if (key.Equals("description",
                StringComparison.OrdinalIgnoreCase))
            {
                desc = val;
            }
        }

        return (name, desc, body);
    }
}