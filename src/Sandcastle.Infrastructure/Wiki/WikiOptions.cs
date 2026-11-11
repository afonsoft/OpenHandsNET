namespace Sandcastle.Infrastructure.Wiki;

/// <summary>
/// SPEC-145 — knobs da wiki por repo (seção <c>Wiki</c>).
/// </summary>
public sealed class WikiOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Wiki";

    /// <summary>Master switch — off desliga generate/manual (default on).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Ask mode usa páginas da wiki como contexto adicional quando
    /// existem (SPEC-145 RF-003, default on).
    /// </summary>
    public bool AskEnrichment { get; set; } = true;

    /// <summary>
    /// Alvo de páginas por esforço (<c>low|medium|high</c> → 5/15/40).
    /// </summary>
    public Dictionary<string, int> EffortPages { get; set; } = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ["low"] = 5,
        ["medium"] = 15,
        ["high"] = 40,
    };

    /// <summary>Teto de arquivos-fonte considerados por página.</summary>
    public int MaxSourceFilesPerPage { get; set; } = 60;

    /// <summary>Teto agregado de conteúdo-fonte por página (chars).</summary>
    public int MaxSourceCharsPerPage { get; set; } = 60_000;

    /// <summary>Teto da listagem da árvore no prompt (chars).</summary>
    public int MaxTreeChars { get; set; } = 12_000;

    /// <summary>Completion cap por página.</summary>
    public int MaxCompletionTokens { get; set; } = 4096;

    /// <summary>
    /// Modelo preferido para o role writer — null delega ao
    /// <c>AgentLlm:RoleModels["summarizer"]</c> e depois ao default.
    /// </summary>
    public string? Model { get; set; }
}