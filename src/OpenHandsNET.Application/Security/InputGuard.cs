namespace OpenHandsNET.Application.Security;

/// <summary>Veredito do screening de input (SPEC-59 RF-002).</summary>
public enum InputGuardVerdict
{
    /// <summary>Passa direto.</summary>
    Allow,

    /// <summary>Suspeito — passa, mas marca o evento.</summary>
    Warn,

    /// <summary>Prompt-injection/exfiltração provável — bloqueado.</summary>
    Block,
}

/// <summary>
/// Resultado do screening. <see cref="Reason"/> nunca contém o texto
/// original (só categoria/hash) — input não vaza para logs/audit.
/// </summary>
public sealed record InputGuardResult(
    InputGuardVerdict Verdict,
    IReadOnlyList<string> Categories,
    string Reason);

/// <summary>
/// SPEC-59 RF-002: screening de mensagens de usuário e gatilhos
/// externos (webhooks chat/ticket/scm) contra prompt-injection e
/// exfiltração. Aplicado na borda — antes de virar prompt de run.
/// </summary>
public interface IInputGuard
{
    /// <summary><paramref name="channel"/> — origem:
    /// <c>ui</c> | <c>webhook:{kind}</c>; <paramref name="orgId"/>
    /// seleciona a política da org (<c>input_guard</c>).</summary>
    Task<InputGuardResult> ScoreAsync(
        string text, string channel, Guid? orgId, CancellationToken ct);
}
