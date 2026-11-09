using System.Text.Json.Serialization;

namespace Sandcastle.Application.Conversations;

/// <summary>SPEC-135 RF-001: body do fork — corte opcional por evento.</summary>
public sealed record ForkConversationRequest(
    [property: JsonPropertyName("at_event_id")] string? AtEventId = null,
    [property: JsonPropertyName("title")] string? Title = null);

/// <summary>Resultado do fork: nova conversa + start-task + origem.</summary>
public sealed record ForkResult(
    Guid Id, Guid StartTaskId, Guid ParentConversationId);

/// <summary>Falhas mapeadas p/ HTTP pelo endpoint.</summary>
public enum ForkError
{
    /// <summary>404 — conversa inexistente/arquivada.</summary>
    NotFound,

    /// <summary>409 — run ativa na origem.</summary>
    RunActive,

    /// <summary>404 — <c>at_event_id</c> não pertence à conversa.</summary>
    EventNotFound,
}

/// <summary>
/// SPEC-135 RF-003: resultado do undo — eventos truncados do último turno
/// (soft-delete <c>truncated_at_ticks</c>) + checkpoint restaurado (quando
/// havia repo/checkpoint anterior ao turno).
/// </summary>
public sealed record UndoResult(
    int TruncatedEvents,
    string? CheckpointHash,
    string TurnEventId,
    IReadOnlyList<string> FilesReverted);

/// <summary>Falhas do undo mapeadas p/ HTTP.</summary>
public enum UndoError
{
    /// <summary>404 — conversa inexistente/arquivada.</summary>
    NotFound,

    /// <summary>409 — run ativa (mesmo guard do checkpoint revert).</summary>
    RunActive,

    /// <summary>404 — nenhum turno de usuário restante.</summary>
    NoTurn,
}

/// <summary>SPEC-135 RF-005: body do revert-hunks.</summary>
public sealed record RevertHunksRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("hunk_indexes")] IReadOnlyList<int> HunkIndexes);

/// <summary>Resultado: hunks aplicados + hunks que falharam (409).</summary>
public sealed record RevertHunksResult(
    IReadOnlyList<int> Applied,
    IReadOnlyList<int> Conflicts);

/// <summary>Falhas do revert-hunks mapeadas p/ HTTP.</summary>
public enum RevertHunksError
{
    /// <summary>404 — sandbox ou conversa dona inexistente.</summary>
    NotFound,

    /// <summary>409 — sandbox não está Running.</summary>
    SandboxNotRunning,

    /// <summary>400 — path inválido (absoluto/..) ou fora do workdir.</summary>
    InvalidPath,

    /// <summary>400 — arquivo não é tracked pelo git do workdir.</summary>
    NotTracked,

    /// <summary>502 — exec falhou no sandbox.</summary>
    ExecFailed,
}

/// <summary>
/// SPEC-135: operações de manipulação da conversa — fork, turn undo e
/// diff hunk revert. Guards executados dentro do serviço; endpoints só
/// mapeiam os erros p/ HTTP.
/// </summary>
public interface IConversationUxService
{
    /// <summary>
    /// Clona a conversa até <paramref name="req"/> (<c>AtEventId</c> ou
    /// todos os eventos) numa nova: eventos copiados com ULIDs novos e
    /// <c>parent_id</c> encadeado, metadados herdados,
    /// <c>ParentConversationId</c> aponta a origem e um start-task boot
    /// o sandbox da filha.
    /// </summary>
    Task<(ForkResult? Result, ForkError? Error)> ForkAsync(
        Guid id, Guid? orgId, string? userId,
        ForkConversationRequest req, CancellationToken ct);

    /// <summary>
    /// Desfaz o último turno do usuário: soft-delete
    /// (<c>truncated_at_ticks</c>) dos eventos do turno + revert do
    /// workdir ao checkpoint do início do turno (SPEC-43 auto) quando
    /// disponível; emite <c>files_changed</c> + <c>conversation_state</c>.
    /// </summary>
    Task<(UndoResult? Result, UndoError? Error)> UndoAsync(
        Guid id, CancellationToken ct);

    /// <summary>
    /// Reverse-apply de hunks selecionados do <c>git diff</c> de
    /// <paramref name="path"/> no workdir do sandbox — hunks que não
    /// aplicam voltam em <see cref="RevertHunksResult.Conflicts"/>.
    /// </summary>
    Task<(RevertHunksResult? Result, RevertHunksError? Error)> RevertHunksAsync(
        Guid sandboxId, string path, IReadOnlyList<int> hunkIndexes,
        CancellationToken ct);
}