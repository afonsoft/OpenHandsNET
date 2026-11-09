namespace Sandcastle.Application.Agent;

/// <summary>
/// Deferred tool loading (SPEC-123, port OWUIC <c>DeferredToolState</c>):
/// tools pesadas/raras saem do spec anunciado ao modelo e voltam via
/// <c>tool_search</c> — reduz tokens do system prompt sem remover
/// capacidade. Estado indexado por <c>conversationId</c>, limpo no fim
/// do run.
/// </summary>
public interface IDeferredToolState
{
    /// <summary>Registra o conjunto deferido da conversa (idempotente).</summary>
    void Set(Guid conversationId, IEnumerable<string> deferredNames);

    /// <summary>A tool ainda está deferida — chamada direta falha
    /// com "use tool_search".</summary>
    bool IsDeferredOnly(Guid conversationId, string toolName);

    /// <summary>Tools já promovidas (voltam ao anúncio no round
    /// seguinte).</summary>
    IReadOnlyCollection<string> Loaded(Guid conversationId);

    /// <summary>
    /// Promove a tool deferida a carregada. Devolve false quando o nome
    /// não estava deferido — nunca cria acesso a tool fora do permitido.
    /// </summary>
    bool Load(Guid conversationId, string toolName);

    /// <summary>Libera o estado da conversa (fim do run).</summary>
    void Clear(Guid conversationId);
}