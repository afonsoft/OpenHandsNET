using System.Text.Json.Serialization;

namespace OpenHandsNET.Application.Checkpoints;

/// <summary>
/// SPEC-43: um snapshot do workdir (<c>/workspace/repo</c>) registrado no
/// git-dir separado <c>/workspace/.ohn-checkpoints</c>.
/// </summary>
public sealed record CheckpointItem(
    [property: JsonPropertyName("hash")] string Hash,
    [property: JsonPropertyName("ts")] long Timestamp,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("files_changed")] int FilesChanged,
    [property: JsonPropertyName("ins")] int Insertions,
    [property: JsonPropertyName("del")] int Deletions);

/// <summary>Diffstat por arquivo de um checkpoint.</summary>
public sealed record CheckpointFileStat(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("ins")] int Ins,
    [property: JsonPropertyName("del")] int Del);

/// <summary>Detalhe de um checkpoint (expand na lista).</summary>
public sealed record CheckpointDetail(
    [property: JsonPropertyName("hash")] string Hash,
    [property: JsonPropertyName("ts")] long Timestamp,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("files")]
    IReadOnlyList<CheckpointFileStat> Files);

/// <summary>Resultado de <see cref="ICheckpointService.RevertAsync"/>.</summary>
public sealed record CheckpointRevertResult
{
    /// <summary>Revert aplicado.</summary>
    [JsonPropertyName("reverted")]
    public required bool Reverted { get; init; }

    /// <summary>Tracked modificados após o checkpoint (drift → 409).</summary>
    [JsonPropertyName("conflicts")]
    public required IReadOnlyList<string> Conflicts { get; init; }

    /// <summary>Untracked criados depois do snapshot (preservados).</summary>
    [JsonPropertyName("untracked")]
    public required IReadOnlyList<string> Untracked { get; init; }

    /// <summary>Hash do snapshot <c>pre-revert</c> criado antes do revert.</summary>
    [JsonPropertyName("pre_revert_hash")]
    public string? PreRevertHash { get; init; }
}

/// <summary>
/// SPEC-43: snapshots do workdir num git-dir separado dentro do sandbox —
/// git CLI via <c>ISandboxExecutor.ExecAsync</c> com
/// <c>GIT_DIR=/workspace/.ohn-checkpoints</c> +
/// <c>GIT_WORK_TREE=/workspace/repo</c> (objects/info/alternates aponta p/
/// o object store do repo, snapshots baratos).
/// </summary>
public interface ICheckpointService
{
    /// <summary>true quando o workspace tem repo git (feature habilitada).</summary>
    Task<bool> IsAvailableAsync(
        Domain.Sandbox.Sandbox sandbox, CancellationToken ct);

    /// <summary>
    /// <c>add -A + commit --allow-empty</c> no git-dir, retorna o item
    /// criado (stats via <c>diff-tree --shortstat</c>). Aplica retenção
    /// <see cref="CheckpointOptions.MaxCount"/>.
    /// </summary>
    Task<CheckpointItem> SnapshotAsync(Guid conversationId,
        Domain.Sandbox.Sandbox sandbox, string label, CancellationToken ct);

    /// <summary>Log do git-dir, mais novo primeiro (cap 50).</summary>
    Task<IReadOnlyList<CheckpointItem>> ListAsync(
        Domain.Sandbox.Sandbox sandbox, CancellationToken ct);

    /// <summary>Diffstat + arquivos do commit <paramref name="hash"/>.</summary>
    Task<CheckpointDetail?> GetAsync(Domain.Sandbox.Sandbox sandbox,
        string hash, CancellationToken ct);

    /// <summary>
    /// Drift: tracked files modificados no workdir desde
    /// <paramref name="hash"/> (→ 409 se não-empty e !force) + untracked.
    /// </summary>
    Task<(IReadOnlyList<string> Conflicts, IReadOnlyList<string> Untracked)>
        DriftAsync(Domain.Sandbox.Sandbox sandbox, string hash,
            CancellationToken ct);

    /// <summary>
    /// <c>reset --hard {hash}</c> no workdir (untracked preservados).
    /// Caller já validou drift/run-ativa.
    /// </summary>
    Task RevertAsync(Guid conversationId, Domain.Sandbox.Sandbox sandbox,
        string hash, CancellationToken ct);
}

/// <summary>Config <c>Checkpoints:*</c> — auto pós-turno e retenção.</summary>
public sealed class CheckpointOptions
{
    /// <summary>Snapshot automático ao fim de cada turno (default true).</summary>
    public bool Auto { get; set; } = true;

    /// <summary>Máximo de checkpoints retidos por conversa (default 50).</summary>
    public int MaxCount { get; set; } = 50;
}