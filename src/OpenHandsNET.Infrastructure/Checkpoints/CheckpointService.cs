using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Checkpoints;
using OpenHandsNET.Application.Git;
using OpenHandsNET.Application.Sandbox;

using DomainSandbox = OpenHandsNET.Domain.Sandbox.Sandbox;

namespace OpenHandsNET.Infrastructure.Checkpoints;

/// <summary>
/// SPEC-43 RF-001/RF-005: checkpoints do workdir via git-dir separado
/// (<c>/workspace/.ohn-checkpoints</c>, bare) com
/// <c>objects/info/alternates → /workspace/repo/.git/objects</c> —
/// snapshots baratos sem tocar o <c>.git</c> do usuário. Tudo por
/// <see cref="ISandboxExecutor.ExecAsync"/> com comandos fixos
/// (zero mudança no sandbox-agent). Lock por conversa via
/// <see cref="SemaphoreSlim"/> (conversa 1:1 sandbox serializa).
/// </summary>
public sealed partial class CheckpointService(
    ISandboxExecutor executor, IGitService git,
    IOptions<CheckpointOptions> options) : ICheckpointService
{
    /// <summary>Git-dir de checkpoints (bare, fora do .git do usuário).</summary>
    public const string Dir = "/workspace/.ohn-checkpoints";

    /// <summary>Work-tree snapshotado.</summary>
    public const string WorkTree = "/workspace/repo";

    private const string Env =
        $"GIT_DIR='{Dir}' GIT_WORK_TREE='{WorkTree}'";

    // Lock por conversa — snapshot+revert não podem intercalar execs.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks =
        new();

    /// <inheritdoc />
    public Task<bool> IsAvailableAsync(DomainSandbox sandbox, CancellationToken ct) =>
        git.HasRepositoryAsync(sandbox, ct);

    /// <inheritdoc />
    public async Task<CheckpointItem> SnapshotAsync(Guid conversationId,
        DomainSandbox sandbox, string label, CancellationToken ct)
    {
        var gate = Locks.GetOrAdd(conversationId, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(sandbox, ct);

            // Commit sempre cria objeto (allow-empty) — label sanitizada
            // p/ single-quote shell.
            var r = await executor.ExecAsync(sandbox,
                $"{Env} git add -A && " +
                $"{Env} git -c user.name=openhands " +
                $"-c user.email=ohn@local commit -qm '{Escape(label)}' " +
                "--allow-empty && " +
                $"{Env} git rev-parse HEAD",
                TimeSpan.FromSeconds(30), ct);
            var hash = r.ExitCode == 0
                ? r.StdOut.Trim().Split('\n').LastOrDefault() ?? "" : "";
            var item = new CheckpointItem(
                hash, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                label, 0, 0, 0);
            var withStats = await StatsOfAsync(sandbox, item, ct);

            await PruneAsync(sandbox, options.Value.MaxCount, ct);
            return withStats;
        }
        finally { gate.Release(); }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CheckpointItem>> ListAsync(
        DomainSandbox sandbox, CancellationToken ct)
    {
        var r = await executor.ExecAsync(sandbox,
            $"test -d '{Dir}/objects' && " +
            $"{Env} git log -n 50 --format='%H%x1f%ct%x1f%s' --shortstat",
            TimeSpan.FromSeconds(15), ct);
        if (r.ExitCode != 0)
        {
            return [];
        }

        // Blocos: "{hash}\x1f{ts}\x1f{label}" + linha shortstat.
        var items = new List<CheckpointItem>();
        CheckpointItem? pending = null;
        foreach (var line in r.StdOut.Split('\n'))
        {
            var parts = line.Split('\x1f');
            if (parts.Length == 3 && HashRe().IsMatch(parts[0]))
            {
                if (pending is not null) { items.Add(pending); }
                pending = new CheckpointItem(parts[0],
                    long.TryParse(parts[1], out var ts) ? ts : 0,
                    parts[2].Trim(), 0, 0, 0);
                continue;
            }

            if (pending is not null
                && line.Contains("changed", StringComparison.Ordinal))
            {
                pending = pending with
                {
                    FilesChanged = Grab(line, FilesRe()),
                    Insertions = Grab(line, InsRe()),
                    Deletions = Grab(line, DelRe()),
                };
            }
        }

        if (pending is not null) { items.Add(pending); }
        return items;
    }

    /// <inheritdoc />
    public async Task<CheckpointDetail?> GetAsync(DomainSandbox sandbox,
        string hash, CancellationToken ct)
    {
        if (!HashRe().IsMatch(hash))
        {
            return null;
        }

        var r = await executor.ExecAsync(sandbox,
            $"{Env} git show {hash} --format='%ct%x1f%s' --numstat",
            TimeSpan.FromSeconds(15), ct);
        if (r.ExitCode != 0)
        {
            return null;
        }

        var lines = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var head = lines.Length > 0 ? lines[0].Split('\x1f') : [];
        var files = new List<CheckpointFileStat>();
        foreach (var line in lines.Skip(1))
        {
            var cols = line.Split('\t');
            if (cols.Length < 3)
            {
                continue;
            }

            // "-" em ins/del = binário.
            files.Add(new CheckpointFileStat(
                cols[2],
                int.TryParse(cols[0], out var i) ? i : 0,
                int.TryParse(cols[1], out var d) ? d : 0));
        }

        return new CheckpointDetail(hash,
            head.Length > 0 && long.TryParse(head[0], out var ts) ? ts : 0,
            head.Length > 1 ? head[1].Trim() : "", files);
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<string>, IReadOnlyList<string>)>
        DriftAsync(DomainSandbox sandbox, string hash, CancellationToken ct)
    {
        if (!HashRe().IsMatch(hash))
        {
            return ([hash], []);
        }

        // Tracked que o revert sobrescreveria + untracked (preservados).
        var tracked = await executor.ExecAsync(sandbox,
            $"{Env} git diff --name-only {hash} --",
            TimeSpan.FromSeconds(20), ct);
        var untracked = await executor.ExecAsync(sandbox,
            $"{Env} git ls-files --others --exclude-standard " +
            $"--directory -- '{WorkTree}/'",
            TimeSpan.FromSeconds(20), ct);
        return (Lines(tracked.StdOut), Lines(untracked.StdOut));
    }

    /// <inheritdoc />
    public async Task RevertAsync(Guid conversationId, DomainSandbox sandbox,
        string hash, CancellationToken ct)
    {
        var gate = Locks.GetOrAdd(conversationId, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // HEAD do ckpt-branch volta ao alvo — histórico posterior
            // fica no reflog (pre-revert snapshot recuperável).
            var r = await executor.ExecAsync(sandbox,
                $"{Env} git reset --hard {hash}",
                TimeSpan.FromSeconds(60), ct);
            if (r.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"checkpoint revert failed: {r.StdErr.Trim()}");
            }
        }
        finally { gate.Release(); }
    }

    // ---- internals -----------------------------------------------------

    /// <summary>
    /// <c>git init --bare</c> + alternates p/ o object store do repo —
    /// idempotente (<c>test -d</c> guarda).
    /// </summary>
    private async Task EnsureInitializedAsync(DomainSandbox sb,
        CancellationToken ct)
    {
        var probe = await executor.ExecAsync(sb,
            $"test -d '{Dir}/objects'", TimeSpan.FromSeconds(10), ct);
        if (probe.ExitCode == 0)
        {
            return;
        }

        var r = await executor.ExecAsync(sb,
            $"git init --bare '{Dir}' && " +
            $"mkdir -p '{Dir}/objects/info' && " +
            $"echo '{WorkTree}/.git/objects' > " +
            $"'{Dir}/objects/info/alternates'",
            TimeSpan.FromSeconds(30), ct);
        if (r.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"checkpoint init failed: {r.StdErr.Trim()}");
        }
    }

    /// <summary>
    /// Retenção <c>MaxCount</c>: <c>rev-list</c> sai do mais novo — o
    /// N-ésimo é o mais antigo retido. <c>replace --graft</c> sem pais
    /// o transforma em raiz (histórico trunca ali p/ log/rev-list) e
    /// reflog expire + gc soltam os ancestrais. Qualquer falha no graft
    /// aborta antes das partes destrutivas (cadeia com
    /// <c>&amp;&amp;</c>).
    /// </summary>
    private async Task PruneAsync(DomainSandbox sb, int max, CancellationToken ct)
    {
        if (max <= 0) { return; }

        await executor.ExecAsync(sb,
            $"n=$({Env} git rev-list --count HEAD 2>/dev/null || echo 0); " +
            $"if [ \"$n\" -gt {max} ]; then " +
            $"t=$({Env} git rev-list HEAD | sed -n '{max}p') && " +
            $"{Env} git -c user.name=openhands " +
            $"-c user.email=ohn@local replace --graft \"$t\" && " +
            $"{Env} git reflog expire --expire=now --all && " +
            $"{Env} git gc --prune=now --quiet; fi",
            TimeSpan.FromSeconds(60), ct);
    }

    /// <summary>Stats via <c>show --shortstat</c> do commit criado.</summary>
    private async Task<CheckpointItem> StatsOfAsync(DomainSandbox sb,
        CheckpointItem item, CancellationToken ct)
    {
        var r = await executor.ExecAsync(sb,
            $"{Env} git show {item.Hash} --shortstat --format=",
            TimeSpan.FromSeconds(15), ct);
        if (r.ExitCode != 0)
        {
            return item;
        }

        return item with
        {
            FilesChanged = Grab(r.StdOut, FilesRe()),
            Insertions = Grab(r.StdOut, InsRe()),
            Deletions = Grab(r.StdOut, DelRe()),
        };
    }

    private static int Grab(string text, Regex re) =>
        re.Match(text) is { Success: true } m
            && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;

    private static string[] Lines(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries);

    private static string Escape(string s) =>
        s.Replace("'", "'\\''");

    [GeneratedRegex("^[0-9a-f]{7,64}$", RegexOptions.IgnoreCase)]
    private static partial Regex HashRe();

    // Partes do shortstat — cada uma opcional na saída do git.
    [GeneratedRegex(@"(\d+) files? changed")]
    private static partial Regex FilesRe();
    [GeneratedRegex(@"(\d+) insertions?\(\+\)")]
    private static partial Regex InsRe();
    [GeneratedRegex(@"(\d+) deletions?\(-\)")]
    private static partial Regex DelRe();
}