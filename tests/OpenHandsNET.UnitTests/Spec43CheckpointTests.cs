using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Checkpoints;
using OpenHandsNET.Application.Git;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Sandbox;
using OpenHandsNET.Infrastructure.Checkpoints;
using Shouldly;
using Xunit;
using DomainSandbox = OpenHandsNET.Domain.Sandbox.Sandbox;

namespace OpenHandsNET.UnitTests;

/// <summary>
/// SPEC-43: <see cref="CheckpointService"/> — init idempotente do git-dir,
/// parse de log/shortstat/numstat, drift e revert — tudo sobre
/// <see cref="ISandboxExecutor"/> scripted (sem Docker).
/// </summary>
public sealed class Spec43CheckpointServiceTests
{
    private static readonly DomainSandbox Sb = new(Guid.NewGuid(),
        Guid.Empty, null, "fake", SandboxStatus.Running, null,
        DateTimeOffset.UtcNow, null, null);

    /// <summary>Executor que responde por matcher de substring do comando.</summary>
    private sealed class ScriptExecutor : ISandboxExecutor
    {
        public List<string> Commands { get; } = [];
        public List<(string Match, CommandResult Result)> Script { get; } = [];

        public Task<CommandResult> ExecAsync(DomainSandbox s, string command,
            TimeSpan timeout, CancellationToken ct)
        {
            Commands.Add(command);
            var hit = Script.FirstOrDefault(x => command.Contains(x.Match));
            return Task.FromResult(hit.Result
                ?? new CommandResult(0, "", "", false));
        }

        public Task<IBashSession> OpenBashSessionAsync(DomainSandbox s,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<string> ReadFileAsync(DomainSandbox s, string p,
            CancellationToken ct) => throw new NotSupportedException();
        public Task WriteFileAsync(DomainSandbox s, string p, string c,
            CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<FileEntry>> ListFilesAsync(DomainSandbox s,
            string p, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FileEntry>>([]);
        public Task<GitDiff> GetGitDiffAsync(DomainSandbox s,
            CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class StubGit : IGitService
    {
        public bool HasRepo { get; set; } = true;
        public Task<bool> HasRepositoryAsync(DomainSandbox s,
            CancellationToken ct) => Task.FromResult(HasRepo);
        public Task CloneAsync(DomainSandbox s, string u, string b,
            string? o, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> ProvisionGhAuthAsync(DomainSandbox s, string? o,
            CancellationToken ct) => Task.FromResult(false);
        public Task<GitChanges> GetChangesAsync(DomainSandbox s,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<GitDiffReport?> GetDiffAsync(DomainSandbox s, string? p,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetRawDiffAsync(DomainSandbox s, string? p,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GitLogEntry>> GetLogAsync(DomainSandbox s,
            int l, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<string?> GetFileAtRefAsync(DomainSandbox s, string p,
            string r, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<GitCommitResult> CommitAsync(DomainSandbox s, string m,
            IReadOnlyList<string>? f, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static CheckpointService New(ScriptExecutor exec,
        int max = 50) => new(exec, new StubGit(),
        Options.Create(new CheckpointOptions { MaxCount = max }));

    [Fact]
    public async Task Snapshot_Inits_Dir_Once_And_Returns_Stats()
    {
        var exec = new ScriptExecutor();
        exec.Script.Add(("test -d", new CommandResult(1, "", "", false)));
        exec.Script.Add(("rev-parse HEAD",
            new CommandResult(0, "abc1234def\n", "", false)));
        exec.Script.Add(("show abc1234def --shortstat",
            new CommandResult(0, " 2 files changed, 5 insertions(+),"
                + " 1 deletion(-)\n", "", false)));

        var item = await New(exec).SnapshotAsync(
            Guid.NewGuid(), Sb, "turn 3", CancellationToken.None);

        item.Hash.ShouldBe("abc1234def");
        item.FilesChanged.ShouldBe(2);
        item.Insertions.ShouldBe(5);
        item.Deletions.ShouldBe(1);
        // init rodou (test -d falhou → git init --bare + alternates).
        exec.Commands.ShouldContain(c => c.Contains("git init --bare"));
        exec.Commands.ShouldContain(c => c.Contains("alternates"));
        exec.Commands.ShouldContain(c => c.Contains("git add -A"));
    }

    [Fact]
    public async Task List_Parses_Log_Shortstat_Blocks()
    {
        var exec = new ScriptExecutor();
        exec.Script.Add(("git log", new CommandResult(0,
            "aaa1111\u001f1700000002\u001fturn 2\n" +
            " 1 file changed, 3 insertions(+)\n\n" +
            "bbb2222\u001f1700000001\u001fturn 1\n" +
            " 4 files changed, 9 insertions(+), 2 deletions(-)\n",
            "", false)));

        var items = await New(exec).ListAsync(Sb, CancellationToken.None);

        items.Count.ShouldBe(2);
        items[0].Hash.ShouldBe("aaa1111");
        items[0].Label.ShouldBe("turn 2");
        items[0].Insertions.ShouldBe(3);
        items[1].FilesChanged.ShouldBe(4);
        items[1].Deletions.ShouldBe(2);
    }

    [Fact]
    public async Task Get_Parses_Numstat_And_Rejects_Bad_Hash()
    {
        var exec = new ScriptExecutor();
        exec.Script.Add(("git show deadbeef",
            new CommandResult(0,
                "1700000002\u001fmanual\n" +
                "10\t2\tsrc/a.cs\n-\t-\timg.png\n", "", false)));

        var svc = New(exec);
        var d = await svc.GetAsync(Sb, "deadbeef", CancellationToken.None);
        d.ShouldNotBeNull();
        d!.Label.ShouldBe("manual");
        d.Files.Count.ShouldBe(2);
        d.Files[0].Path.ShouldBe("src/a.cs");
        d.Files[0].Ins.ShouldBe(10);
        d.Files[1].Ins.ShouldBe(0); // binário → "-"

        (await svc.GetAsync(Sb, "not a hash!!", CancellationToken.None))
            .ShouldBeNull();
        (await svc.GetAsync(Sb, "aa'; rm -rf /", CancellationToken.None))
            .ShouldBeNull();
    }

    [Fact]
    public async Task Drift_Splits_Tracked_And_Untracked()
    {
        var exec = new ScriptExecutor();
        exec.Script.Add(("diff --name-only",
            new CommandResult(0, "src/a.cs\nsrc/b.cs\n", "", false)));
        exec.Script.Add(("ls-files --others",
            new CommandResult(0, "notes/new.txt\n", "", false)));

        var (conflicts, untracked) = await New(exec).DriftAsync(
            Sb, "abcdef1", CancellationToken.None);
        conflicts.ShouldBe(["src/a.cs", "src/b.cs"]);
        untracked.ShouldBe(["notes/new.txt"]);
    }

    [Fact]
    public async Task Revert_Resets_Hard_And_Fails_Loud()
    {
        var exec = new ScriptExecutor();
        exec.Script.Add(("reset --hard badf00d",
            new CommandResult(128, "", "fatal: bad revision", false)));

        var svc = New(exec);
        await svc.RevertAsync(Guid.NewGuid(), Sb,
            "abcdef1", CancellationToken.None);
        exec.Commands.ShouldContain(c => c.Contains("reset --hard abcdef1"));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            svc.RevertAsync(Guid.NewGuid(), Sb,
                "badf00d", CancellationToken.None));
    }

    [Fact]
    public async Task Snapshot_Prunes_To_MaxCount()
    {
        var exec = new ScriptExecutor();
        exec.Script.Add(("rev-parse HEAD",
            new CommandResult(0, "abc1234\n", "", false)));
        exec.Script.Add(("rev-list --count",
            new CommandResult(0, "60\n", "", false)));
        exec.Script.Add(("rev-list HEAD",
            new CommandResult(0,
                string.Join('\n', Enumerable.Range(0, 60)
                    .Select(i => $"h{i:d4}")) + "\n", "", false)));

        await New(exec, max: 50).SnapshotAsync(
            Guid.NewGuid(), Sb, "t", CancellationToken.None);
        // Trunca no 50º mais novo via graft — nunca move o branch p/ trás.
        exec.Commands.ShouldContain(c => c.Contains("replace --graft"));
        exec.Commands.ShouldContain(c => c.Contains("sed -n '50p'"));
    }

    [Fact]
    public async Task Unavailable_Without_Repo()
    {
        var svc = new CheckpointService(new ScriptExecutor(),
            new StubGit { HasRepo = false },
            Options.Create(new CheckpointOptions()));
        (await svc.IsAvailableAsync(Sb, CancellationToken.None))
            .ShouldBeFalse();
    }
}
