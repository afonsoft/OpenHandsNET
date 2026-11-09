namespace Sandcastle.Application.Agent;

/// <summary>
/// SPEC-125 (port OWUIC advisor-system.md): segundo modelo opinando em
/// decisões arriscadas — "advisor" no sentido de consultor, nunca
/// executor. <see cref="ConsultAsync"/> devolve o parecer ou
/// <see langword="null"/> quando o recurso está inerte
/// (<c>Agent:AdvisorModel</c> ausente) ou a consulta falhou — fail-open,
/// nunca interrompe o run.
/// </summary>
public interface IAdvisorService
{
    /// <summary>Se a feature está ativa (model configurado).</summary>
    bool Enabled { get; }

    /// <summary>
    /// Consulta o advisor sobre uma decisão: devolve o parecer (texto
    /// curto + modelo + latência) ou null em falha/inerte.
    /// </summary>
    Task<AdvisorVerdict?> ConsultAsync(
        string context, string question, CancellationToken ct);
}

/// <summary>Parecer do advisor + metadados da consulta.</summary>
public sealed record AdvisorVerdict(
    string Answer, string Model, long ElapsedMs);