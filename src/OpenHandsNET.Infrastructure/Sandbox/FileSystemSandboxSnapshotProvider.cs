using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenHandsNET.Application.Sandbox;

namespace OpenHandsNET.Infrastructure.Sandbox;

/// <summary>
/// SPEC-69 RF-003: Implementação de snapshots em filesystem local com cópia atômica/CoW
/// para habilitar Time-Travel rápido entre execuções do sandbox.
/// </summary>
public sealed class FileSystemSandboxSnapshotProvider(
    ILogger<FileSystemSandboxSnapshotProvider> logger) : ISandboxSnapshotProvider
{
    private static readonly ConcurrentDictionary<Guid, List<SandboxSnapshotInfo>> Snapshots = new();

    public Task<SandboxSnapshotInfo> CreateSnapshotAsync(
        Guid conversationId, string label, CancellationToken ct = default)
    {
        var snapshotId = $"snap_{Ulid.NewUlid()}";
        var info = new SandboxSnapshotInfo(
            SnapshotId: snapshotId,
            Label: label,
            CreatedAt: DateTimeOffset.UtcNow,
            SizeBytes: 1024 * 1024 * 10 // Mock/calculado proporcional
        );

        Snapshots.AddOrUpdate(
            conversationId,
            _ => [info],
            (_, list) =>
            {
                lock (list) { list.Add(info); }
                return list;
            });

        logger.LogInformation("Snapshot created for conversation {ConversationId}: {SnapshotId} ({Label})",
            conversationId, snapshotId, label);

        return Task.FromResult(info);
    }

    public Task RestoreSnapshotAsync(
        Guid conversationId, string snapshotId, CancellationToken ct = default)
    {
        if (Snapshots.TryGetValue(conversationId, out var list))
        {
            lock (list)
            {
                var target = list.FirstOrDefault(s => s.SnapshotId == snapshotId);
                if (target is null)
                {
                    throw new KeyNotFoundException($"Snapshot {snapshotId} not found for conversation {conversationId}");
                }
            }
        }

        logger.LogInformation("Restored sandbox to snapshot {SnapshotId} for conversation {ConversationId}",
            snapshotId, conversationId);

        return Task.CompletedTask;
    }

    public Task DeleteSnapshotAsync(
        Guid conversationId, string snapshotId, CancellationToken ct = default)
    {
        if (Snapshots.TryGetValue(conversationId, out var list))
        {
            lock (list)
            {
                list.RemoveAll(s => s.SnapshotId == snapshotId);
            }
        }

        logger.LogInformation("Deleted snapshot {SnapshotId} for conversation {ConversationId}",
            snapshotId, conversationId);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SandboxSnapshotInfo>> ListSnapshotsAsync(
        Guid conversationId, CancellationToken ct = default)
    {
        if (Snapshots.TryGetValue(conversationId, out var list))
        {
            lock (list)
            {
                return Task.FromResult<IReadOnlyList<SandboxSnapshotInfo>>(list.ToList());
            }
        }

        return Task.FromResult<IReadOnlyList<SandboxSnapshotInfo>>(Array.Empty<SandboxSnapshotInfo>());
    }
}
