namespace Sandcastle.Infrastructure.Indexing;

/// <summary>
/// SPEC-145 RF-001 — knobs da indexação de repos (seção <c>Indexing</c>).
/// </summary>
public sealed class IndexingOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Indexing";

    /// <summary>
    /// Re-index automático quando um <c>push</c> webhook muda o head da
    /// branch indexada (default on — desligue para triggers só manuais).
    /// </summary>
    public bool AutoRefreshOnPush { get; set; } = true;

    /// <summary>
    /// Path do repo local servido pelo
    /// <see cref="LocalRepoContentProvider"/> (fallback quando o repo não
    /// tem conexão SCM configurada). Null = working directory do processo.
    /// </summary>
    public string? LocalRepoPath { get; set; }

    /// <summary>Raiz dos workdirs temporários de clone (default = temp do SO).</summary>
    public string? TempRoot { get; set; }

    /// <summary>Profundidade do clone shallow (default 1).</summary>
    public int CloneDepth { get; set; } = 1;

    /// <summary>Teto de arquivos indexados por run (0 = sem teto).</summary>
    public int MaxFiles { get; set; }

    /// <summary>Timeout do clone em minutos.</summary>
    public int CloneTimeoutMinutes { get; set; } = 5;

    /// <summary>Binário git usado pelos clones locais.</summary>
    public string GitPath { get; set; } = "git";
}