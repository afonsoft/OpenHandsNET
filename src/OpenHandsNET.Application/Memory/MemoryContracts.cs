namespace OpenHandsNET.Application.Memory;

/// <summary>
/// One memory entry (SPEC-30): <paramref name="Scope"/> is
/// <c>org</c> (DB, audited) or <c>project</c> (workspace file).
/// </summary>
public sealed record MemoryEntry(
    string Key, string Text, string Scope, DateTimeOffset UpdatedAt);

/// <summary>
/// Org-scoped memory service (SPEC-30 RF-001/002): project-scope memories
/// live as workspace files (<c>.openhands/memory/*.md</c>) and are handled
/// directly by the <c>memory</c> tool + digest loader.
/// </summary>
public interface IMemoryService
{
    /// <summary>Upserts an org memory by key (audit-logged).</summary>
    Task<MemoryEntry> RememberOrgAsync(Guid orgId, string key, string text,
        string? actorUserId, CancellationToken ct);

    /// <summary>Lists org memories; <paramref name="query"/> filters key/text.</summary>
    Task<IReadOnlyList<MemoryEntry>> ListOrgAsync(
        Guid orgId, string? query, CancellationToken ct);

    /// <summary>Soft-deletes an org memory by key (audit-logged).</summary>
    Task<bool> ForgetOrgAsync(
        Guid orgId, string key, string? actorUserId, CancellationToken ct);

    /// <summary>
    /// Newest <paramref name="maxEntries"/> memories for the prompt digest
    /// (SPEC-30 RF-003) — stable across turns, never pruned by compaction.
    /// </summary>
    Task<IReadOnlyList<MemoryEntry>> DigestAsync(
        Guid orgId, int maxEntries, CancellationToken ct);
}
