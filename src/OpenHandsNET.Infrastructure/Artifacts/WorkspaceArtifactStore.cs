using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Artifacts;

namespace OpenHandsNET.Infrastructure.Artifacts;

/// <summary>
/// SPEC-34 RF-002: store default — grava em
/// <c>{WorkspaceRoot}/{conversationId}/{ulid}-{name}</c> e devolve refs
/// <c>ws:{conversationId}/{file}</c>. Path-traversal safe: o ref é sempre
/// reancorado sob a raiz da conversa.
/// </summary>
public sealed class WorkspaceArtifactStore(
    IOptions<ArtifactOptions> opt,
    ILogger<WorkspaceArtifactStore> log) : IArtifactStore
{
    /// <inheritdoc />
    public async Task<string> PutAsync(Guid conversationId, string name,
        ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var dir = DirOf(opt.Value.WorkspaceRoot, conversationId);
        Directory.CreateDirectory(dir);
        var file = $"{Ulid.NewUlid()}-{Sanitize(name)}";
        await File.WriteAllBytesAsync(
            Path.Combine(dir, file), data.ToArray(), ct);
        return $"ws:{conversationId}/{file}";
    }

    /// <inheritdoc />
    public async Task<Stream?> OpenAsync(Guid conversationId,
        string artifactRef, CancellationToken ct)
    {
        var path = Resolve(conversationId, artifactRef);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        return await Task.FromResult<Stream?>(
            new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read));
    }

    /// <inheritdoc />
    public Task DeleteConversationAsync(Guid conversationId,
        CancellationToken ct)
    {
        var dir = DirOf(opt.Value.WorkspaceRoot, conversationId);
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex,
                "artifact cleanup failed for conversation {Id}",
                conversationId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolve <c>ws:{conv}/{file}</c> para caminho absoluto, rejeitando
    /// refs de outra conversa ou com traversal (<c>..</c>, separadores no
    /// nome do arquivo).
    /// </summary>
    public string? Resolve(Guid conversationId, string artifactRef)
    {
        if (!artifactRef.StartsWith("ws:", StringComparison.Ordinal))
        {
            return null;
        }

        var rel = artifactRef[3..];
        var sep = rel.IndexOf('/');
        if (sep <= 0 || sep == rel.Length - 1)
        {
            return null;
        }

        if (!Guid.TryParse(rel[..sep], out var convId)
            || convId != conversationId)
        {
            return null;
        }

        var file = rel[(sep + 1)..];
        if (file.Contains('/') || file.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        return Path.Combine(
            DirOf(opt.Value.WorkspaceRoot, conversationId), file);
    }

    private static string DirOf(string root, Guid conversationId) =>
        Path.Combine(root, conversationId.ToString());

    private static string Sanitize(string name)
    {
        var safe = new string(name.Select(c =>
            char.IsLetterOrDigit(c) || c is '.' or '-' or '_'
                ? c : '-').ToArray());
        return safe.Length is 0 or > 120 ? "artifact.bin" : safe;
    }
}