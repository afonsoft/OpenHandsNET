namespace OpenHandsNET.Application.Artifacts;

/// <summary>
/// SPEC-34 RF-002: armazenamento pluggável de artefatos (observations
/// grandes do OutputPolicy, previews). Default: workspace local
/// (<c>/tmp/ohn-artifacts/</c>); opt-in S3 via <c>Artifacts:Provider=s3</c>.
/// Refs opacos: <c>ws:&lt;path&gt;</c> (workspace) ou <c>art:&lt;id&gt;</c> (S3).
/// </summary>
public interface IArtifactStore
{
    /// <summary>
    /// Stores the payload and returns an opaque artifact ref
    /// (<c>ws:</c>/<c>art:</c> prefixed) safe to embed in events.
    /// </summary>
    Task<string> PutAsync(Guid conversationId, string name,
        ReadOnlyMemory<byte> data, CancellationToken ct);

    /// <summary>
    /// Opens the artifact identified by <paramref name="artifactRef"/>
    /// (the full opaque ref returned by <see cref="PutAsync"/>),
    /// or <see langword="null"/> when it does not exist.
    /// </summary>
    Task<Stream?> OpenAsync(Guid conversationId, string artifactRef,
        CancellationToken ct);

    /// <summary>
    /// Removes every artifact of the conversation (delete path —
    /// SPEC-34 RF-003). Best-effort: storage failures are logged, not thrown.
    /// </summary>
    Task DeleteConversationAsync(Guid conversationId, CancellationToken ct);
}
