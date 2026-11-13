using System.Text.Json;

using Sandcastle.Domain.Conversations;

namespace Sandcastle.Application.Conversations;

/// <summary>
/// SPEC-150 — <c>Handoff:*</c> options. <see cref="MaxDiffKb"/> é o teto
/// duro de qualquer patch (ida e volta); <see cref="PackageTtlMin"/> é o
/// TTL do cache on-demand do handoff-package.
/// </summary>
public sealed class HandoffOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Handoff";

    /// <summary>Teto do diff (KB) — default 512.</summary>
    public int MaxDiffKb { get; set; } = 512;

    /// <summary>TTL do snapshot on-demand do package (min) — default 60.</summary>
    public int PackageTtlMin { get; set; } = 60;

    /// <summary><see cref="MaxDiffKb"/> em bytes.</summary>
    public int MaxDiffBytes => Math.Max(1, MaxDiffKb) * 1024;
}

/// <summary>
/// SPEC-150 RF-001 — body do <c>POST /api/v1/conversations/handoff</c>:
/// estado do working tree local empacotado pelo CLI.
/// </summary>
public sealed record HandoffRequest
{
    /// <summary>Repositório (<c>owner/repo</c> ou URL) associado a uma SCM connection.</summary>
    public required string Repository { get; init; }

    /// <summary>SHA base do working tree local (commit de partida).</summary>
    public required string BaseSha { get; init; }

    /// <summary>Branch local de origem (informativo).</summary>
    public string? BaseBranch { get; init; }

    /// <summary>Unified diff do working tree vs <see cref="BaseSha"/>.</summary>
    public required string DiffPatch { get; init; }

    /// <summary>Mensagem/prompt inicial do usuário (envelope SPEC-91).</summary>
    public string? Message { get; init; }

    /// <summary>Metadata arbitrária do CLI (cli_version, stat, …).</summary>
    public JsonElement? Metadata { get; init; }
}

/// <summary>
/// SPEC-150 §4 — metadata persistida em <c>conversations.handoff_meta_json</c>:
/// origem do handoff, base e stats do apply (preenchido pelo worker).
/// <see cref="DiffPatch"/> viaja aqui para o worker aplicar no sandbox —
/// sanitizado por SecretRedactor antes de persistir.
/// </summary>
public sealed record HandoffMeta
{
    /// <summary><c>local</c> (CLI → cloud) | <c>cloud</c> (reservado).</summary>
    public required string Origin { get; init; }

    /// <summary>SHA base do patch.</summary>
    public required string BaseSha { get; init; }

    /// <summary>Branch de origem local.</summary>
    public string? BaseBranch { get; init; }

    /// <summary>Versão do CLI que originou o handoff.</summary>
    public string? CliVersion { get; init; }

    /// <summary>Unified diff recebido (sanitizado).</summary>
    public string? DiffPatch { get; init; }

    /// <summary>Metadata arbitrária do request.</summary>
    public JsonElement? Metadata { get; init; }

    /// <summary>Stats do apply no sandbox (worker, após PREPARING_REPOSITORY).</summary>
    public HandoffApplyStats? AppliedStats { get; init; }
}

/// <summary>Resultado do <c>git apply</c> do handoff no sandbox.</summary>
public sealed record HandoffApplyStats
{
    /// <summary>Patch aplicado por completo (--3way ou --reject sem falhas).</summary>
    public bool Applied { get; set; }

    /// <summary>Arquivos tocados pelo patch.</summary>
    public int Files { get; set; }

    /// <summary>Linhas adicionadas (numstat do patch).</summary>
    public int Additions { get; set; }

    /// <summary>Linhas removidas.</summary>
    public int Deletions { get; set; }

    /// <summary>Paths com hunks rejeitados (<c>*.rej</c> deixados no tree).</summary>
    public IReadOnlyList<string> RejectedFiles { get; set; } = [];

    /// <summary>Conteúdo dos hunks rejeitados (cap por arquivo).</summary>
    public IReadOnlyList<HandoffRejectHunk> RejectedHunks { get; set; } = [];

    /// <summary>Erro fatal do apply (ex.: base_sha ausente) — null quando ok.</summary>
    public string? Error { get; set; }

    /// <summary>Timestamp do apply.</summary>
    public DateTimeOffset AppliedAt { get; set; }
}

/// <summary>Um <c>*.rej</c> do <c>git apply --reject</c> — path + corpo.</summary>
public sealed record HandoffRejectHunk(string Path, string Content);

/// <summary>
/// SPEC-150 RF-002 — package retornado por
/// <c>GET /api/v1/conversations/{id}/handoff-package</c> e cacheado em
/// <c>conversations.handoff_package_json</c> quando o sandbox morre.
/// </summary>
public sealed record HandoffPackage
{
    /// <summary>Repositório da conversa.</summary>
    public required string Repo { get; init; }

    /// <summary>Branch de trabalho no sandbox.</summary>
    public string? HeadBranch { get; init; }

    /// <summary>HEAD do sandbox na geração (invalida cache a cada commit).</summary>
    public string? HeadSha { get; init; }

    /// <summary>Base do diff (handoff base_sha ou merge-base).</summary>
    public string? BaseSha { get; init; }

    /// <summary>Unified diff worktree vs base (sanitizado).</summary>
    public required string DiffPatch { get; init; }

    /// <summary>Resumo do agente (último work_summary / título).</summary>
    public string? Summary { get; init; }

    /// <summary>Arquivos binários alterados (placeholder — blob nunca trafega).</summary>
    public IReadOnlyList<string> BinaryFiles { get; init; } = [];

    /// <summary>Geração do package (UTC).</summary>
    public DateTimeOffset GeneratedAt { get; init; }
}

/// <summary>Resultado do GetPackage — package ou erro HTTP-mappable.</summary>
public sealed record HandoffPackageResult(
    HandoffPackage? Package, HandoffPackageError? Error, bool Cached);

/// <summary>Erros do handoff-package (SPEC-150 §3).</summary>
public enum HandoffPackageError
{
    /// <summary>404 — conversa inexistente.</summary>
    NotFound,

    /// <summary>403 — caller não é dono nem tem papel na org da conversa.</summary>
    Forbidden,

    /// <summary>409 — sandbox morto e sem snapshot cacheado.</summary>
    NoLiveSandbox,

    /// <summary>422 — diff do sandbox excede <c>Handoff:MaxDiffKb</c>.</summary>
    PackageTooLarge,
}

/// <summary>Erros de validação do create handoff (SPEC-150 §3).</summary>
public enum HandoffCreateError
{
    /// <summary>400 — repository ausente/inválido.</summary>
    RepositoryRequired,

    /// <summary>400 — base_sha ausente/malformado.</summary>
    InvalidBaseSha,

    /// <summary>400 — diff_patch vazio ou sem cara de unified diff.</summary>
    InvalidPatch,

    /// <summary>400 — diff_patch excede <c>Handoff:MaxDiffKb</c>.</summary>
    PatchTooLarge,
}

/// <summary>Resultado do create handoff — task ou erro de validação.</summary>
public sealed record HandoffCreateResult(
    AppConversationStartTask? Task,
    HandoffCreateError? Error,
    string? Detail)
{
    /// <summary>Sucesso — a start-task criada.</summary>
    public static HandoffCreateResult Ok(AppConversationStartTask task) =>
        new(task, null, null);

    /// <summary>Falha de validação (400 no endpoint).</summary>
    public static HandoffCreateResult Fail(
        HandoffCreateError error, string detail) =>
        new(null, error, detail);
}

/// <summary>SPEC-150 — serviço de handoff (create + package).</summary>
public interface IHandoffService
{
    /// <summary>
    /// Cria a conversa handoff (start-task padrão): valida o request,
    /// sanitiza o patch e semeia <c>handoff_meta_json</c> + mensagem
    /// inicial envelopada. Todas as policies de start-task se aplicam
    /// (quota/plan → <see cref="Identity.PlanLimitExceededException"/>;
    /// seleção → <see cref="RepositorySelectionException"/>).
    /// </summary>
    Task<HandoffCreateResult> CreateAsync(
        HandoffRequest request, CancellationToken ct);

    /// <summary>
    /// Package on-demand: sandbox vivo → diff vs base (+cache);
    /// morto → snapshot persistido; sem os dois →
    /// <see cref="HandoffPackageError.NoLiveSandbox"/>.
    /// <paramref name="regenerate"/> ignora o TTL do cache.
    /// </summary>
    Task<HandoffPackageResult> GetPackageAsync(
        Guid conversationId, bool regenerate, CancellationToken ct);
}

/// <summary>
/// SPEC-150 — apply do patch do handoff dentro do sandbox
/// (<c>git apply --3way</c> → fallback <c>--reject</c> com hunks
/// reportados). Usado pelo <c>ConversationStartWorker</c> após o clone.
/// </summary>
public interface IHandoffApplier
{
    /// <summary>
    /// Reseta a work branch para <paramref name="meta"/>.BaseSha e aplica
    /// <paramref name="meta"/>.DiffPatch; retorna os stats para o evento
    /// <c>handoff_applied</c> e para <c>applied_stats</c> do registro.
    /// </summary>
    Task<HandoffApplyStats> ApplyAsync(
        Domain.Sandbox.Sandbox sandbox, HandoffMeta meta,
        string workBranch, CancellationToken ct);
}