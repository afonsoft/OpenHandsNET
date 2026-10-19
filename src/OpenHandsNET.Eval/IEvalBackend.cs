using System.Text.Json;

namespace OpenHandsNET.Eval;

/// <summary>
/// Porta entre o runner e o ambiente (API local/remota — RF-002 provider).
/// Implementada no CLI sobre <c>CloudApiClient</c>; fakeable em testes.
/// </summary>
public interface IEvalBackend
{
    /// <summary>Cria a conversa com a instrução da task; devolve o start-task id.</summary>
    Task<string> CreateConversationAsync(
        string task, string? repo, string? model, CancellationToken ct);

    /// <summary>Espera o start-task chegar a READY; devolve conversation id.</summary>
    Task<string> WaitReadyAsync(string taskId, CancellationToken ct);

    /// <summary>Executa comandos <c>setup</c> no workspace do sandbox.</summary>
    Task RunSetupAsync(
        string conversationId, IReadOnlyList<string> commands, CancellationToken ct);

    /// <summary>
    /// Aguarda estado terminal do run e devolve o transcript de eventos
    /// (elementos JSON crus, wire format V1).
    /// </summary>
    Task<IReadOnlyList<JsonElement>> WatchAsync(
        string conversationId, TimeSpan timeout, CancellationToken ct);

    /// <summary>Exec pós-run: devolve <c>(exit_code, stdout)</c>.</summary>
    Task<(int ExitCode, string StdOut)> ExecAsync(
        string conversationId, string command, CancellationToken ct);

    /// <summary>Lê um arquivo do workspace; <see langword="null"/> se ausente.</summary>
    Task<string?> ReadFileAsync(
        string conversationId, string path, CancellationToken ct);

    /// <summary>
    /// Juiz LLM (role Summarizer): devolve score 0..1 + racional, ou
    /// <see langword="null"/> quando não configurado (assert vira skipped).
    /// </summary>
    Task<(double Score, string Rationale)?> JudgeAsync(
        string rubric, string transcript, CancellationToken ct);
}