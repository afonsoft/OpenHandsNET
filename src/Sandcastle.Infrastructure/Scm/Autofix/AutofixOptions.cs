namespace Sandcastle.Infrastructure.Scm.Autofix;

/// <summary>
/// SPEC-141 — knobs do CI autofix (seção <c>Autofix</c>). O commit
/// status <c>sandcastle/autofix</c> é sempre advisory — branch rules do
/// repo decidem se bloqueia.
/// </summary>
public sealed class AutofixOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Autofix";

    /// <summary>Master switch — off desliga webhook trigger e manual (default on).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Cap duro de tentativas de fix por run (SPEC 3) — conexão pode reduzir, nunca além de 10.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Teto do log de CI enviado ao agente/persistido (KB) — SPEC 256KB.</summary>
    public int MaxLogKb { get; set; } = 256;

    /// <summary>Intervalo do poll de checks pós-push (segundos, SPEC 60).</summary>
    public int PollSeconds { get; set; } = 60;

    /// <summary>Timeout do loop por tentativa — push + checks terminais (minutos, SPEC 45).</summary>
    public int RunTimeoutMinutes { get; set; } = 45;

    /// <summary>Quota diária de runs por repo.</summary>
    public int MaxDailyRunsPerRepo { get; set; } = 20;

    /// <summary>
    /// Logins de bot cujos commits/atores nunca disparam autofix
    /// (anti-loop — SPEC-141 RF-002). Casados contra
    /// <c>workflow_run.actor.login</c> e afins; a detecção principal de
    /// push próprio é por <c>autofix_attempts.head_sha_out</c>.
    /// </summary>
    public string[] BotAuthors { get; set; } =
        ["sandcastle-agent", "sandcastle-agent[bot]", "github-actions[bot]"];

    /// <summary>
    /// Globs de check-name elegíveis (default <c>*</c>) — org restringe
    /// ex.: <c>*lint*</c>, <c>*test*</c>. Aplicado quando a conexão não
    /// define <c>autofix_check_patterns</c> próprios.
    /// </summary>
    public string[] CheckNamePatterns { get; set; } = ["*"];

    /// <summary>
    /// Assinaturas de falha flaky/transiente (substring, case-insensitive)
    /// — hits viram <see cref="Sandcastle.Application.Scm.CiFailureClass.Flaky"/>
    /// e NÃO consomem tentativa nem recebem fix.
    /// </summary>
    public string[] FlakyPatterns { get; set; } =
    [
        "flaky", "econnreset", "etimedout", "socket hang up",
        "net::err", "tls handshake timeout", "unexpected eof",
        "econnrefused", "broken pipe",
    ];

    /// <summary>
    /// SPEC-141 RF-004: quando ON, findings <c>bug|security</c> ainda
    /// <c>open</c> do review (SPEC-140) entram no prompt do fix.
    /// </summary>
    public bool FixReviewFindings { get; set; }

    /// <summary>Modelo preferido da conversa de fix — null = default do routing.</summary>
    public string? Model { get; set; }
}