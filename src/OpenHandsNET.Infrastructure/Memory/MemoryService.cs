using Microsoft.EntityFrameworkCore;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Application.Memory;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Memory;

/// <summary>
/// EF-backed org memory (SPEC-30 RF-001): upsert by (org,key), soft-delete,
/// every write audit-logged (<c>memory_remember</c>/<c>memory_forget</c>).
/// </summary>
public sealed class MemoryService(
    AppDbContext db, IAuditService? audit = null) : IMemoryService
{
    /// <inheritdoc />
    public async Task<MemoryEntry> RememberOrgAsync(Guid orgId, string key,
        string text, string? actorUserId, CancellationToken ct)
    {
        var normalized = NormalizeKey(key);
        var existing = await db.MemoryRecords
            .Where(m => m.OrgId == orgId && m.Key == normalized
                && m.DeletedAt == null)
            .FirstOrDefaultAsync(ct);
        var now = DateTimeOffset.UtcNow;
        if (existing is null)
        {
            existing = new MemoryRecord
            {
                Id = Guid.NewGuid(), OrgId = orgId, Key = normalized,
                Text = text, CreatedByUserId = actorUserId,
                CreatedAt = now, UpdatedAt = now,
            };
            db.MemoryRecords.Add(existing);
        }
        else
        {
            existing.Text = text;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        if (audit is not null)
        {
            await audit.RecordAsync("memory_remember", actorUserId, orgId,
                "memory", normalized, metadata: new { key = normalized },
                cancellationToken: ct);
        }

        return new MemoryEntry(normalized, text, "org", existing.UpdatedAt);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryEntry>> ListOrgAsync(
        Guid orgId, string? query, CancellationToken ct)
    {
        var q = db.MemoryRecords.AsNoTracking()
            .Where(m => m.OrgId == orgId && m.DeletedAt == null);
        var rows = await q.ToListAsync(ct);
        // Filtro de texto em memória (SQLite/PG traduzem LIKE diferente).
        return rows
            .Where(m => string.IsNullOrWhiteSpace(query)
                || m.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                || m.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.UpdatedAt)
            .Select(ToEntry).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> ForgetOrgAsync(
        Guid orgId, string key, string? actorUserId, CancellationToken ct)
    {
        var normalized = NormalizeKey(key);
        var existing = await db.MemoryRecords
            .Where(m => m.OrgId == orgId && m.Key == normalized
                && m.DeletedAt == null)
            .ToListAsync(ct);
        if (existing.Count == 0)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var m in existing)
        {
            m.DeletedAt = now;
        }

        await db.SaveChangesAsync(ct);
        if (audit is not null)
        {
            await audit.RecordAsync("memory_forget", actorUserId, orgId,
                "memory", normalized, metadata: new { key = normalized },
                cancellationToken: ct);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryEntry>> DigestAsync(
        Guid orgId, int maxEntries, CancellationToken ct)
    {
        var rows = await db.MemoryRecords.AsNoTracking()
            .Where(m => m.OrgId == orgId && m.DeletedAt == null)
            .ToListAsync(ct);
        // Ordenação em memória — SQLite não ordena DateTimeOffset.
        return rows.OrderByDescending(m => m.UpdatedAt)
            .Take(Math.Clamp(maxEntries, 1, 50)).Select(ToEntry).ToList();
    }

    private static MemoryEntry ToEntry(MemoryRecord m) =>
        new(m.Key, m.Text, "org", m.UpdatedAt);

    private static string NormalizeKey(string key) =>
        string.IsNullOrWhiteSpace(key)
            ? throw new ArgumentException("memory key must be non-empty")
            : key.Trim().ToLowerInvariant()[..Math.Min(key.Trim().Length, 200)];
}
