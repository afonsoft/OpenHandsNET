namespace OpenHandsNET.Application.Sandbox;

/// <summary>
/// Informações descritivas de um snapshot de sandbox.
/// </summary>
public sealed record SandboxSnapshotInfo(
    string SnapshotId,
    string Label,
    DateTimeOffset CreatedAt,
    long SizeBytes = 0);

/// <summary>
/// SPEC-69 RF-003: Provedor de snapshots físicos de sandbox e time-travel rollback.
/// Permite reverter o estado de disco/sandbox atômico durante a execução de tarefas.
/// </summary>
public interface ISandboxSnapshotProvider
{
    /// <summary>Cria snapshot atômico do workspace/sandbox atual.</summary>
    Task<SandboxSnapshotInfo> CreateSnapshotAsync(
        Guid conversationId, string label, CancellationToken ct = default);

    /// <summary>Restaura o sandbox para um snapshot prévio.</summary>
    Task RestoreSnapshotAsync(
        Guid conversationId, string snapshotId, CancellationToken ct = default);

    /// <summary>Remove snapshot descartado.</summary>
    Task DeleteSnapshotAsync(
        Guid conversationId, string snapshotId, CancellationToken ct = default);

    /// <summary>Lista snapshots criados para a conversa informada.</summary>
    Task<IReadOnlyList<SandboxSnapshotInfo>> ListSnapshotsAsync(
        Guid conversationId, CancellationToken ct = default);
}
