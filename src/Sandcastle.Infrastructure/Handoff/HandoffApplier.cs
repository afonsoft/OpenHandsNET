using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Conversations;
using Sandcastle.Application.Sandbox;
using Sandcastle.Infrastructure.Git;

using DomainSandbox = Sandcastle.Domain.Sandbox.Sandbox;

namespace Sandcastle.Infrastructure.Handoff;

/// <summary>
/// SPEC-150 RF-001 — aplica o patch do handoff dentro do sandbox após o
/// clone: reseta a work branch para <c>base_sha</c>, tenta
/// <c>git apply --3way</c>; em conflito, <c>--reject</c> e os hunks
/// falhados (<c>*.rej</c>) são reportados nos stats do evento
/// <c>handoff_applied</c>. Exec roda via <see cref="ISandboxExecutor"/> —
/// mesma boundary dos demais comandos git (SPEC-12).
/// </summary>
public sealed class HandoffApplier(
    ISandboxExecutor executor,
    IOptions<GitOptions> gitOptions,
    ILogger<HandoffApplier> logger) : IHandoffApplier
{
    private const string PatchPath = "/tmp/sc-handoff.patch";
    private const int MaxRejectFiles = 50;

    /// <inheritdoc />
    public async Task<HandoffApplyStats> ApplyAsync(
        DomainSandbox sandbox, HandoffMeta meta,
        string workBranch, CancellationToken ct)
    {
        var repo = gitOptions.Value.RepoPath;
        var (files, adds, dels) = meta.DiffPatch is { } p
            ? HandoffDiffUtil.Numstat(p) : (0, 0, 0);
        var stats = new HandoffApplyStats
        {
            Files = files,
            Additions = adds,
            Deletions = dels,
            AppliedAt = DateTimeOffset.UtcNow,
        };

        if (meta.DiffPatch is not { Length: > 0 } patch)
        {
            stats.Error = "empty_patch";
            return stats;
        }

        var has = await executor.ExecAsync(sandbox,
            $"git -C '{repo}' rev-parse --is-inside-work-tree",
            TimeSpan.FromSeconds(15), ct);
        if (has.StdOut.Trim() != "true")
        {
            stats.Error = "repository_not_cloned";
            return stats;
        }

        // Work branch no base_sha do handoff — o patch é relativo a ele.
        var co = await executor.ExecAsync(sandbox,
            $"git -C '{repo}' checkout -B '{Escape(workBranch)}' " +
            $"'{Escape(meta.BaseSha)}'",
            TimeSpan.FromSeconds(60), ct);
        if (co.ExitCode != 0)
        {
            stats.Error = "base_checkout_failed: "
                + HandoffDiffUtil.Cap(co.StdErr.Trim(), 400);
            return stats;
        }

        await executor.WriteFileAsync(sandbox, PatchPath, patch, ct);
        var applied = await executor.ExecAsync(sandbox,
            $"git -C '{repo}' apply --3way '{PatchPath}'",
            TimeSpan.FromMinutes(2), ct);
        if (applied.ExitCode == 0)
        {
            stats.Applied = true;
            await CleanupAsync(sandbox, ct);
            return stats;
        }

        // Conflito (ou 3-way indisponível): volta o tree ao base e reaplica
        // com --reject — hunks falhados viram *.rej reportados no evento.
        await executor.ExecAsync(sandbox,
            $"git -C '{repo}' reset --hard '{Escape(meta.BaseSha)}'",
            TimeSpan.FromSeconds(60), ct);
        var rejected = await executor.ExecAsync(sandbox,
            $"git -C '{repo}' apply --reject '{PatchPath}'",
            TimeSpan.FromMinutes(2), ct);
        await CollectRejectsAsync(sandbox, repo, stats, ct);

        if (rejected.ExitCode == 0)
        {
            stats.Applied = true;
        }
        else if (stats.RejectedFiles.Count == 0)
        {
            // Nenhum hunk virou .rej → falha não-conflito (patch corrompido).
            stats.Error = "apply_failed: "
                + HandoffDiffUtil.Cap(rejected.StdErr.Trim(), 400);
        }

        await CleanupAsync(sandbox, ct);
        logger.LogInformation(
            "handoff apply conv sandbox={SandboxId}: applied={Applied} "
            + "rejects={Rejects}", sandbox.Id, stats.Applied,
            stats.RejectedFiles.Count);
        return stats;
    }

    /// <summary>Lê os <c>*.rej</c> deixados pelo --reject (cap de 50).</summary>
    private async Task CollectRejectsAsync(
        DomainSandbox sandbox, string repo,
        HandoffApplyStats stats, CancellationToken ct)
    {
        var find = await executor.ExecAsync(sandbox,
            $"find '{repo}' -name '*.rej' -type f",
            TimeSpan.FromSeconds(15), ct);
        var files = find.StdOut.Split('\n',
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Take(MaxRejectFiles).ToList();
        var rejectedFiles = new List<string>(files.Count);
        var hunks = new List<HandoffRejectHunk>(files.Count);
        foreach (var abs in files)
        {
            var rel = abs.StartsWith(repo + "/", StringComparison.Ordinal)
                ? abs[(repo.Length + 1)..] : abs;
            rejectedFiles.Add(rel);
            string body;
            try
            {
                body = await executor.ReadFileAsync(sandbox, abs, ct);
            }
            catch (Exception ex)
            {
                body = $"failed to read reject: {ex.Message}";
            }

            hunks.Add(new HandoffRejectHunk(
                rel, HandoffDiffUtil.Cap(body, HandoffDiffUtil.MaxRejectChars)));
        }

        stats.RejectedFiles = rejectedFiles;
        stats.RejectedHunks = hunks;
    }

    private async Task CleanupAsync(DomainSandbox sandbox, CancellationToken ct) =>
        await executor.ExecAsync(sandbox, $"rm -f '{PatchPath}'",
            TimeSpan.FromSeconds(10), ct);

    private static string Escape(string s) => s.Replace("'", "'\\''");
}