namespace OpenHandsNET.Domain.Skills;

/// <summary>Skill provenance (SPEC-11 RF-001) — maps to injection order.</summary>
public enum SkillSource
{
    /// <summary>Seeded from embedded resources; read-only.</summary>
    Builtin = 0,

    /// <summary>Managed by org admins (SPEC-08).</summary>
    Org = 1,

    /// <summary>Owned by a single user.</summary>
    User = 2,
}

/// <summary>Trigger kinds (SPEC-11 RF-002).</summary>
public enum SkillTriggerType
{
    /// <summary>Injected into every conversation.</summary>
    Always,

    /// <summary>Case-insensitive word match on the last user message.</summary>
    Keyword,

    /// <summary>Matches a task tag on the start-task.</summary>
    Task,
}

/// <summary>One trigger clause on a skill.</summary>
public sealed record SkillTrigger(
    SkillTriggerType Type,
    string? Value = null);

/// <summary>
/// Reusable markdown skill (upstream microagent parity — data, never code).
/// </summary>
public sealed record Skill
{
    public required Guid Id { get; init; }
    public string? OwnerId { get; init; }
    public string? OrgId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string ContentMd { get; init; }
    public IReadOnlyList<SkillTrigger> Triggers { get; init; } = [];
    public SkillSource Source { get; init; } = SkillSource.User;
    public bool Enabled { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; }
}
