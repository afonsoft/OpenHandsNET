namespace OpenHandsNET.Domain.Events;

/// <summary>
/// SPEC-69 RF-002: Observação injetada quando um operador humano assume o controle direto
/// do sandbox (via Terminal Web xterm.js ou VNC Canvas) e finaliza sua intervenção.
/// O agente utiliza esse sumário para recalcular o estado mental e o plano de ação.
/// </summary>
public sealed record HumanIntervenedObservation : EventBase
{
    public HumanIntervenedObservation()
    {
        Source = EventSource.User;
    }

    /// <summary>Interface de origem da intervenção: "terminal" ou "browser_vnc".</summary>
    public string Surface { get; init; } = "terminal";

    /// <summary>Sumário das ações realizadas ou intenção do humano.</summary>
    public required string Summary { get; init; }

    /// <summary>Comandos executados pelo humano no terminal durante o takeover, se aplicável.</summary>
    public IReadOnlyList<string>? CommandsExecuted { get; init; }
}
