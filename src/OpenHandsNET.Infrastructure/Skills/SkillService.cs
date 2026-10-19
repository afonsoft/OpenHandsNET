using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using OpenHandsNET.Application.Skills;
using OpenHandsNET.Domain.Skills;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Skills;

/// <summary>
/// EF-backed skill store + trigger resolution (SPEC-11 RF-001/002/005).
/// Skills are markdown data — never executed.
/// </summary>
public sealed partial class SkillService(AppDbContext db) : ISkillService
{
    private const int MaxNameLength = 128;
    private const int MaxDescriptionLength = 1024;
    private const int MaxContentBytes = 32 * 1024;

    private static readonly JsonSerializerOptions TriggerJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(
            JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <inheritdoc />
    public async Task<Skill> CreateAsync(Skill skill, CancellationToken ct)
    {
        Validate(skill);
        var now = DateTimeOffset.UtcNow;
        var record = new SkillRecord
        {
            Id = skill.Id == Guid.Empty ? Guid.NewGuid() : skill.Id,
            OwnerId = skill.OwnerId,
            OrgId = skill.OrgId,
            Name = skill.Name,
            Description = skill.Description,
            Content = skill.ContentMd,
            TriggersJson = JsonSerializer.Serialize(skill.Triggers, TriggerJson),
            Source = skill.Source.ToString().ToLowerInvariant(),
            Enabled = skill.Enabled,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Skills.Add(record);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new InvalidOperationException(
                $"skill name '{skill.Name}' already exists for this owner");
        }

        return ToDomain(record);
    }

    /// <inheritdoc />
    public async Task<Skill?> GetAsync(Guid id, string? ownerId, CancellationToken ct)
    {
        var r = await db.Skills.FindAsync([id], ct);
        return r is null || !Visible(r, ownerId, null) ? null : ToDomain(r);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Skill>> ListAsync(
        string? ownerId, string? orgId, CancellationToken ct) =>
        (await db.Skills.AsNoTracking().ToListAsync(ct))
            .Where(r => Visible(r, ownerId, orgId))
            .Select(ToDomain)
            .OrderBy(s => s.Source).ThenBy(s => s.Name)
            .ToList();

    /// <inheritdoc />
    public async Task<Skill?> UpdateAsync(
        Guid id, string? ownerId, UpdateSkillRequest update, CancellationToken ct)
    {
        var r = await db.Skills.FindAsync([id], ct);
        if (r is null || !Visible(r, ownerId, null) ||
            r.Source == "builtin")
        {
            return null;
        }

        if (update.Description is not null)
        {
            r.Description = Truncate(update.Description, MaxDescriptionLength);
        }

        if (update.ContentMd is not null)
        {
            if (System.Text.Encoding.UTF8.GetByteCount(update.ContentMd) > MaxContentBytes)
            {
                throw new InvalidOperationException("content_md exceeds 32KB");
            }

            r.Content = update.ContentMd;
        }

        if (update.Triggers is not null)
        {
            r.TriggersJson = JsonSerializer.Serialize(update.Triggers, TriggerJson);
        }

        if (update.Enabled is { } enabled)
        {
            r.Enabled = enabled;
        }

        r.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToDomain(r);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid id, string? ownerId, CancellationToken ct)
    {
        var r = await db.Skills.FindAsync([id], ct);
        if (r is null || r.Source == "builtin" ||
            (r.OwnerId is not null && r.OwnerId != ownerId))
        {
            return false;
        }

        db.Skills.Remove(r);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<Skill?> FindByNameAsync(
        string name, string? ownerId, string? orgId, CancellationToken ct)
    {
        var r = await db.Skills.AsNoTracking()
            .Where(s => s.Name == name && s.Enabled)
            .ToListAsync(ct);
        return r.Where(s => Visible(s, ownerId, orgId))
            .OrderBy(s => SourceOrder(s.Source))
            .Select(ToDomain)
            .FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Skill>> ResolveForConversationAsync(
        Guid conversationId, string? message, string? taskTag,
        string? ownerId, string? orgId, CancellationToken ct)
    {
        var pinnedIds = await db.ConversationSkills.AsNoTracking()
            .Where(c => c.ConversationId == conversationId)
            .Select(c => c.SkillId)
            .ToListAsync(ct);

        var all = await db.Skills.AsNoTracking()
            .Where(s => s.Enabled)
            .ToListAsync(ct);

        var resolved = all
            .Where(r => Visible(r, ownerId, orgId))
            .Where(r => pinnedIds.Contains(r.Id) ||
                        Matches(r, message, taskTag))
            .OrderBy(r => SourceOrder(r.Source))
            .ThenBy(r => r.Name)
            .GroupBy(r => r.Name)
            .Select(g => ToDomain(g.First()))
            .ToList();
        return resolved;
    }

    /// <summary>Persists resolved skills into <c>conversation_skills</c>.</summary>
    public async Task PinAsync(
        Guid conversationId, IEnumerable<Guid> skillIds, CancellationToken ct)
    {
        var existing = await db.ConversationSkills
            .Where(c => c.ConversationId == conversationId)
            .Select(c => c.SkillId)
            .ToListAsync(ct);
        foreach (var id in skillIds.Except(existing))
        {
            db.ConversationSkills.Add(new ConversationSkillRecord
            {
                ConversationId = conversationId,
                SkillId = id,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private static bool Visible(SkillRecord r, string? ownerId, string? orgId) =>
        r.Source == "builtin" ||
        (r.OwnerId is null && r.OrgId is null) ||
        (r.OwnerId is not null && r.OwnerId == ownerId) ||
        (r.OrgId is not null && r.OrgId == orgId);

    private static int SourceOrder(string? source) => source switch
    {
        "builtin" => 0,
        "org" => 1,
        _ => 2,
    };

    private static bool Matches(SkillRecord r, string? message, string? taskTag)
    {
        var triggers = ParseTriggers(r.TriggersJson);
        foreach (var t in triggers)
        {
            switch (t.Type)
            {
                case SkillTriggerType.Always:
                    return true;
                case SkillTriggerType.Keyword
                    when message is not null && t.Value is not null &&
                         ContainsWord(message, t.Value):
                    return true;
                case SkillTriggerType.Task
                    when taskTag is not null && t.Value is not null &&
                         string.Equals(t.Value, taskTag,
                             StringComparison.OrdinalIgnoreCase):
                    return true;
            }
        }

        return false;
    }

    private static bool ContainsWord(string message, string keyword) =>
        Regex.IsMatch(message,
            $@"(?<![\w]){Regex.Escape(keyword)}(?![\w])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(500));

    private static IReadOnlyList<SkillTrigger> ParseTriggers(string? json) =>
        json is null ? [] :
        JsonSerializer.Deserialize<List<SkillTrigger>>(json, TriggerJson) ?? [];

    private static Skill ToDomain(SkillRecord r) => new()
    {
        Id = r.Id,
        OwnerId = r.OwnerId,
        OrgId = r.OrgId,
        Name = r.Name,
        Description = r.Description,
        ContentMd = r.Content,
        Triggers = ParseTriggers(r.TriggersJson),
        Source = r.Source switch
        {
            "builtin" => SkillSource.Builtin,
            "org" => SkillSource.Org,
            _ => SkillSource.User,
        },
        Enabled = r.Enabled,
        CreatedAt = r.CreatedAt,
    };

    private static void Validate(Skill skill)
    {
        if (string.IsNullOrWhiteSpace(skill.Name) ||
            skill.Name.Length > MaxNameLength ||
            !SlugRegex().IsMatch(skill.Name))
        {
            throw new InvalidOperationException(
                "skill name must be a slug (lowercase letters, digits, '-')");
        }

        if (System.Text.Encoding.UTF8.GetByteCount(skill.ContentMd) > MaxContentBytes)
        {
            throw new InvalidOperationException("content_md exceeds 32KB");
        }
    }

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugRegex();

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}