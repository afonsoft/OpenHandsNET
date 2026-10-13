using OpenHandsNET.Domain.Skills;

namespace OpenHandsNET.Application.Skills;

/// <summary>Skill CRUD + trigger resolution (SPEC-11 RF-001/002).</summary>
public interface ISkillService
{
    /// <summary>Creates a user skill (slug unique per owner).</summary>
    Task<Skill> CreateAsync(Skill skill, CancellationToken ct);

    /// <summary>Returns a skill visible to the owner, or null.</summary>
    Task<Skill?> GetAsync(Guid id, string? ownerId, CancellationToken ct);

    /// <summary>Lists enabled skills visible to the owner (own + org + builtin).</summary>
    Task<IReadOnlyList<Skill>> ListAsync(
        string? ownerId, string? orgId, CancellationToken ct);

    /// <summary>Updates mutable fields of a non-builtin skill.</summary>
    Task<Skill?> UpdateAsync(
        Guid id, string? ownerId, UpdateSkillRequest update, CancellationToken ct);

    /// <summary>Deletes a user/org skill. Builtin skills are read-only.</summary>
    Task<bool> DeleteAsync(Guid id, string? ownerId, CancellationToken ct);

    /// <summary>Finds a visible skill by slug name (for invoke_skill).</summary>
    Task<Skill?> FindByNameAsync(
        string name, string? ownerId, string? orgId, CancellationToken ct);

    /// <summary>
    /// Resolves skills active for a conversation: persisted
    /// <c>conversation_skills</c> + always/org/builtin + keyword/task matches
    /// against <paramref name="message"/> (ordered builtin → org → user,
    /// deduped by name).
    /// </summary>
    Task<IReadOnlyList<Skill>> ResolveForConversationAsync(
        Guid conversationId, string? message, string? taskTag,
        string? ownerId, string? orgId, CancellationToken ct);
}

/// <summary>Mutable fields of a skill.</summary>
public sealed record UpdateSkillRequest(
    string? Description,
    string? ContentMd,
    IReadOnlyList<SkillTrigger>? Triggers,
    bool? Enabled);
