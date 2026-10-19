using System.Runtime.CompilerServices;

namespace OpenHandsNET.Application.Conversations;

/// <summary>
/// SPEC-44 RF-003: barramento in-process de invalidação de attention.
/// Publica um <em>tick</em> sem payload — os assinantes (hub → client)
/// refazem o fetch no endpoint com o scoping normal. Nenhum dado de
/// conversa trafega aqui (menor superfície de vazamento possível).
/// </summary>
public interface IAttentionBus
{
    /// <summary>Sinaliza que a projeção de attention mudou.</summary>
    void PublishChanged();

    /// <summary>Ticks (UTC) de mudança até <paramref name="ct"/> cancelar.</summary>
    IAsyncEnumerable<DateTimeOffset> SubscribeAsync(CancellationToken ct);
}