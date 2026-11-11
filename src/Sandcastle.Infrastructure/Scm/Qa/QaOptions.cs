namespace Sandcastle.Infrastructure.Scm.Qa;

/// <summary>
/// SPEC-142 — knobs do QA agent (seção <c>Qa</c>). O commit status
/// <c>sandcastle/qa</c> é sempre advisory — branch rules do repo
/// decidem se bloqueia.
/// </summary>
public sealed class QaOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Qa";

    /// <summary>Master switch — off desliga webhook trigger e manual (default on).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Bail-out: cap duro de passos executados por run (SPEC 30).</summary>
    public int MaxSteps { get; set; } = 30;

    /// <summary>Bail-out: cap duro de duração do run em minutos (SPEC 30).</summary>
    public int MaxMinutes { get; set; } = 30;

    /// <summary>Quota diária de runs por repo (SPEC 30).</summary>
    public int MaxDailyRunsPerRepo { get; set; } = 30;

    /// <summary>Teto do diff enviado ao LLM na fase Understand (KB).</summary>
    public int MaxDiffKb { get; set; } = 256;

    /// <summary>Teto de arquivos do diff considerados no plano.</summary>
    public int MaxFilesPerRun { get; set; } = 100;

    /// <summary>Timeout de um passo exec/browser individual (segundos).</summary>
    public int StepTimeoutSeconds { get; set; } = 60;

    /// <summary>Timeout do install/setup dentro do sandbox (segundos).</summary>
    public int SetupTimeoutSeconds { get; set; } = 300;

    /// <summary>Readiness budget do app start (segundos).</summary>
    public int AppReadySeconds { get; set; } = 60;

    /// <summary>
    /// Spec do sandbox descartável do run — vazio = spec default
    /// (<c>Sandbox:DefaultImage</c>, que deve carregar Playwright +
    /// Chromium para passos browser — SPEC-131 runtime image).
    /// </summary>
    public Guid? SandboxSpecId { get; set; }

    /// <summary>Passos browser habilitados (driver Playwright no sandbox).</summary>
    public bool BrowserEnabled { get; set; } = true;

    /// <summary>Gravação de vídeo da sessão browser (webm via page.video()).</summary>
    public bool VideoEnabled { get; set; } = true;

    /// <summary>Teto do vídeo persistido como artifact (MB).</summary>
    public int MaxVideoMb { get; set; } = 16;

    /// <summary>Screenshots linkados inline no comentário do PR (SPEC ≤4).</summary>
    public int InlineScreenshots { get; set; } = 4;

    /// <summary>
    /// Base pública do Sandcastle para links de evidência no comentário
    /// do PR (ex.: <c>https://sandcastle.example.com</c>). Null = links
    /// relativos <c>/api/v1/qa/runs/{id}/artifacts/{ref}</c> — ainda
    /// auth'd, mas exigem que o leitor resolva o host.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Bugfix: tenta reproduzir no base SHA antes de verificar no PR
    /// (quando base + reprodução são viáveis — exec-only).
    /// </summary>
    public bool ReproductionEnabled { get; set; } = true;

    /// <summary>Modelo preferido p/ a fase Understand — null = routing.</summary>
    public string? Model { get; set; }

    /// <summary>Completion cap da chamada de plano.</summary>
    public int MaxCompletionTokens { get; set; } = 4096;
}