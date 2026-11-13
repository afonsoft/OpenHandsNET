using System.Text;

namespace Sandcastle.Cli.Services;

/// <summary>
/// SPEC-150 RF-001 — coleta do estado do working tree local para
/// <c>sandcastle handoff</c>: diff tracked (staged+unstaged) vs base
/// (merge-base com upstream, senão HEAD), untracked opt-in respeitando
/// <c>.gitignore</c> + blocklist dura de secrets, binários fora do
/// patch (metadata), teto <c>Handoff:MaxDiffKb</c>.
/// </summary>
public sealed class HandoffCollect(IGitRunner git)
{
    /// <summary>Resultado da coleta — patch + metadata do handoff.</summary>
    public sealed record Result
    {
        public required string Repository { get; init; }
        public required string BaseSha { get; init; }
        public string? BaseBranch { get; init; }
        public required string Patch { get; init; }
        public int Files { get; init; }
        public int Additions { get; init; }
        public int Deletions { get; init; }
        public IReadOnlyList<string> BinaryFiles { get; init; } = [];
        public IReadOnlyList<string> SecretsSkipped { get; init; } = [];
        public IReadOnlyList<string> UntrackedIncluded { get; init; } = [];
        public int PatchBytes { get; init; }
    }

    /// <summary>Falha de coleta com mensagem para o usuário.</summary>
    public sealed class CollectException(string message)
        : Exception(message);

    /// <summary>
    /// Coleta o diff. <paramref name="includeUntracked"/> injeta arquivos
    /// untracked (exceto .gitignore + secrets) como new-file patches.
    /// </summary>
    public async Task<Result> CollectAsync(
        bool includeUntracked, CancellationToken ct = default)
    {
        var inside = await git.RunAsync(
            "rev-parse --is-inside-work-tree", ct);
        if (inside.ExitCode != 0
            || inside.StdOut.Trim() != "true")
        {
            throw new CollectException(
                "not inside a git working tree — run inside a repository");
        }

        var remote = await git.RunAsync(
            "remote get-url origin", ct);
        if (remote.ExitCode != 0 || remote.StdOut.Trim().Length == 0)
        {
            throw new CollectException(
                "no git remote 'origin' — the handoff needs a repository "
                + "the server can clone (org SCM connection)");
        }

        var branch = (await git.RunAsync(
            "rev-parse --abbrev-ref HEAD", ct)).StdOut.Trim();

        // base_sha: merge-base com upstream quando existe (cobre commits
        // locais não-pushados + uncommitted); senão HEAD.
        var upstream = await git.RunAsync(
            "rev-parse --abbrev-ref @{u}", ct);
        var baseSha = upstream.ExitCode == 0
            ? (await git.RunAsync(
                $"merge-base HEAD {upstream.StdOut.Trim()}", ct))
                .StdOut.Trim()
            : (await git.RunAsync("rev-parse HEAD", ct)).StdOut.Trim();
        if (baseSha.Length == 0)
        {
            throw new CollectException(
                "could not resolve base_sha (no upstream, empty HEAD?)");
        }

        var tracked = await git.RunAsync(
            $"diff {baseSha}", ct);
        if (tracked.ExitCode != 0)
        {
            throw new CollectException(
                $"git diff failed: {tracked.StdErr.Trim()}");
        }

        var secretsSkipped = new List<string>();
        var untrackedIncluded = new List<string>();
        var patch = new StringBuilder(tracked.StdOut);

        if (includeUntracked)
        {
            var others = await git.RunAsync(
                "ls-files --others --exclude-standard", ct);
            foreach (var path in others.StdOut.Split('\n',
                    StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries))
            {
                if (HandoffDiffTool.IsSecretPath(path))
                {
                    secretsSkipped.Add(path);
                    continue;
                }

                var full = Path.Combine(git.WorkDir,
                    path.Replace('/', Path.DirectorySeparatorChar));
                string content;
                try
                {
                    content = await File.ReadAllTextAsync(full, ct);
                }
                catch (Exception)
                {
                    continue; // binário/ilegível → fora do patch
                }

                if (Encoding.UTF8.GetByteCount(content)
                    > HandoffDiffTool.MaxUntrackedBytes)
                {
                    content = "\0"; // grande demais → placeholder
                }

                patch.Append(HandoffDiffTool.NewFilePatch(path, content));
                untrackedIncluded.Add(path);
            }
        }

        var (stripped, binaries) =
            HandoffDiffTool.StripBinary(patch.ToString());
        var bytes = Encoding.UTF8.GetByteCount(stripped);
        if (bytes > HandoffDiffTool.MaxDiffKb * 1024)
        {
            throw new CollectException(
                $"diff exceeds Handoff:MaxDiffKb "
                + $"({HandoffDiffTool.MaxDiffKb} KB, got {bytes / 1024} KB)"
                + " — commit or split the changes first");
        }

        var (files, adds, dels) = HandoffDiffTool.Stat(stripped);
        return new Result
        {
            Repository = remote.StdOut.Trim(),
            BaseSha = baseSha,
            BaseBranch = branch is "HEAD" ? null : branch,
            Patch = stripped,
            Files = files,
            Additions = adds,
            Deletions = dels,
            BinaryFiles = binaries,
            SecretsSkipped = secretsSkipped,
            UntrackedIncluded = untrackedIncluded,
            PatchBytes = bytes,
        };
    }

}