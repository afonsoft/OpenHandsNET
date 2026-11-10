namespace Sandcastle.Infrastructure.Scm.Review;

/// <summary>
/// SPEC-140 — knobs do auto-review de PR (seção <c>Review</c>).
/// Todos os defaults são advisory: review nunca é required por si só —
/// branch rules do repo decidem se <c>sandcastle/review</c> bloqueia.
/// </summary>
public sealed class ReviewOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Review";

    /// <summary>Master switch — off desliga webhook trigger e manual (default on).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Teto do diff efetivamente enviado ao LLM (KB) — SPEC 512KB.</summary>
    public int MaxDiffKb { get; set; } = 512;

    /// <summary>Teto de arquivos revisados por run (SPEC 200).</summary>
    public int MaxFilesPerRun { get; set; } = 200;

    /// <summary>Teto de comentários inline postados por run (SPEC 30) — excedente vira linha no summary.</summary>
    public int MaxInlineComments { get; set; } = 30;

    /// <summary>Teto agregado das repo rules concatenadas no prompt (chars).</summary>
    public int MaxRulesChars { get; set; } = 8000;

    /// <summary>Quota diária de runs por repo (SPEC 50).</summary>
    public int MaxDailyRunsPerRepo { get; set; } = 50;

    /// <summary>
    /// Modelo preferido p/ o role reviewer — null delega ao
    /// <c>AgentLlm:RoleModels["reviewer"]</c> e depois ao modelo default.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>Completion cap da chamada de review.</summary>
    public int MaxCompletionTokens { get; set; } = 8192;
}