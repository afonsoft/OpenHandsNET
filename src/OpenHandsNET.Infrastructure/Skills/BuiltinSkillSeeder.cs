using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Skills;

/// <summary>
/// Seeds builtin skills embedded as manifest resources under
/// <c>Skills/Embedded/*.md</c> (SPEC-11 RF-001). Idempotent — a builtin row
/// is upserted per name, never duplicated, and its content stays in sync
/// with the shipped resource.
/// </summary>
public static class BuiltinSkillSeeder
{
    private const string ResourcePrefix = "OpenHandsNET.Infrastructure.Skills.Embedded.";

    /// <summary>Embedded skill definitions (name → resource → trigger).</summary>
    private static readonly (string Name, string File, string Description,
        string TriggersJson)[] Definitions =
        [
            ("dotnet-conventions", "dotnet-conventions.md",
                ".NET coding conventions: async suffix, records, nullable, warnings-as-errors.",
                """[{"type":"always"}]"""),
            ("git-commit-style", "git-commit-style.md",
                "Commit message style: Conventional Commits, imperative, scoped.",
                """[{"type":"keyword","value":"commit"},{"type":"keyword","value":"git"}]"""),
        ];

    /// <summary>Upserts builtin skills; safe to call on every boot.</summary>
    public static async Task SeedAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        var existing = await db.Skills
            .Where(s => s.Source == "builtin")
            .ToListAsync(ct);
        var byName = existing.ToDictionary(s => s.Name, StringComparer.Ordinal);

        foreach (var def in Definitions)
        {
            var content = await ReadResourceAsync(def.File, ct);
            if (content is null)
            {
                logger.LogWarning("Embedded skill {File} missing", def.File);
                continue;
            }

            if (byName.TryGetValue(def.Name, out var row))
            {
                if (row.Content != content || row.Description != def.Description ||
                    row.TriggersJson != def.TriggersJson)
                {
                    row.Content = content;
                    row.Description = def.Description;
                    row.TriggersJson = def.TriggersJson;
                    row.Enabled = true;
                    row.UpdatedAt = DateTimeOffset.UtcNow;
                }

                continue;
            }

            db.Skills.Add(new SkillRecord
            {
                Id = Guid.NewGuid(),
                OwnerId = null,
                OrgId = null,
                Name = def.Name,
                Description = def.Description,
                Content = content,
                TriggersJson = def.TriggersJson,
                Source = "builtin",
                Enabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task<string?> ReadResourceAsync(
        string file, CancellationToken ct)
    {
        var asm = typeof(BuiltinSkillSeeder).Assembly;
        var name = ResourcePrefix + file;
        await using var stream = asm.GetManifestResourceStream(name);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }
}
