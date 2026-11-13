using System.Net;
using System.Text;
using System.Text.Json;

using Sandcastle.Cli.Commands;
using Sandcastle.Cli.Services;

using Shouldly;

namespace Sandcastle.UnitTests.Cli;

/// <summary>
/// SPEC-150 — CLI: HandoffDiffTool, HandoffCollect (gitignore/untracked/
/// blocklist/oversize), pickup clean/dirty/force/conflict e handoff
/// smoke com HTTP mockado.
/// </summary>
public sealed class Spec150CliTests
{
    private const string Diff =
        "diff --git a/a.txt b/a.txt\n"
        + "index 1111111..2222222 100644\n"
        + "--- a/a.txt\n"
        + "+++ b/a.txt\n"
        + "@@ -1 +1 @@\n"
        + "-old\n"
        + "+new\n";

    // ------------------------------------------------- diff tool

    [Fact]
    public void DiffTool_Stat_And_Look()
    {
        var (files, adds, dels) = HandoffDiffTool.Stat(Diff);
        files.ShouldBe(1);
        adds.ShouldBe(1);
        dels.ShouldBe(1);
        HandoffDiffTool.LooksLikeDiff(Diff).ShouldBeTrue();
        HandoffDiffTool.LooksLikeDiff("junk").ShouldBeFalse();
    }

    [Fact]
    public void DiffTool_Strips_Binary_To_Metadata()
    {
        var bin = "diff --git a/i.png b/i.png\n"
            + "index 1..2 100644\n"
            + "Binary files a/i.png and b/i.png differ\n";
        var (patch, bins) = HandoffDiffTool.StripBinary(Diff + bin);
        patch.ShouldNotContain("Binary files");
        patch.ShouldContain("a.txt");
        bins.ShouldBe(["i.png"]);
    }

    [Theory]
    [InlineData(".env", true)]
    [InlineData("d/x.pem", true)]
    [InlineData("d/id_rsa_backup", true)]
    [InlineData("src/ok.cs", false)]
    public void DiffTool_SecretBlocklist(string path, bool expected) =>
        HandoffDiffTool.IsSecretPath(path).ShouldBe(expected);

    // ------------------------------------------------- fake git

    private sealed class FakeGit(
        Dictionary<string, string> outputs,
        Func<string, string, (int, string, string)>? inputHandler = null)
        : IGitRunner
    {
        public List<string> Commands { get; } = [];

        public string WorkDir => Directory.GetCurrentDirectory();

        public Task<GitResult> RunAsync(
            string args, CancellationToken ct = default)
        {
            Commands.Add(args);
            foreach (var (k, v) in outputs)
            {
                if (args.StartsWith(k, StringComparison.Ordinal))
                {
                    return Task.FromResult(
                        new GitResult(v == "!" ? 1 : 0,
                            v == "!" ? "" : v, v == "!" ? "err" : ""));
                }
            }

            return Task.FromResult(new GitResult(1, "", "unknown"));
        }

        public Task<GitResult> RunWithInputAsync(
            string args, string input, CancellationToken ct = default)
        {
            Commands.Add(args);
            var (code, stdout, stderr) =
                inputHandler?.Invoke(args, input) ?? (0, "", "");
            return Task.FromResult(new GitResult(code, stdout, stderr));
        }
    }

    private static Dictionary<string, string> BaseRepo(
        string untracked = "") => new()
        {
            ["rev-parse --is-inside-work-tree"] = "true\n",
            ["remote get-url origin"] = "https://github.com/o/r.git\n",
            ["rev-parse --abbrev-ref @{u}"] = "origin/main\n",
            ["merge-base HEAD origin/main"] = "abc1234def\n",
            ["rev-parse --abbrev-ref HEAD"] = "feat\n",
            ["rev-parse HEAD"] = "abc1234def\n",
            ["diff abc1234def"] = Diff,
            ["ls-files --others --exclude-standard"] = untracked,
            ["status --porcelain"] = "",
            ["checkout -B"] = "\n",
        };

    // ------------------------------------------------- collect

    [Fact]
    public async Task Collect_Tracked_Only_By_Default()
    {
        var git = new FakeGit(BaseRepo("untracked.txt\n"));
        var r = await new HandoffCollect(git)
            .CollectAsync(includeUntracked: false, default);
        r.Patch.ShouldContain("a.txt");
        r.Patch.ShouldNotContain("untracked.txt");
        r.BaseSha.ShouldBe("abc1234def");
        r.Repository.ShouldContain("o/r");
    }

    [Fact]
    public async Task Collect_Untracked_OptIn_Respects_Gitignore()
    {
        // git ls-files --exclude-standard já exclui .gitignore —
        // o fake emula: só "note.txt" e ".env" voltam do ls-files.
        // ".env" bate a blocklist dura → secrets_skipped.
        var git = new FakeGit(BaseRepo("note.txt\n.env\n"));
        var scratch = Path.Combine(
            Directory.GetCurrentDirectory(), "note.txt");
        await File.WriteAllTextAsync(scratch, "hi\n");
        try
        {
            var r = await new HandoffCollect(git)
                .CollectAsync(includeUntracked: true, default);
            r.UntrackedIncluded.ShouldContain("note.txt");
            r.SecretsSkipped.ShouldContain(".env");
            r.Patch.ShouldContain("new file mode");
            r.Patch.ShouldContain("+hi");
        }
        finally
        {
            File.Delete(scratch);
        }
    }

    [Fact]
    public async Task Collect_Oversize_Patch_Fails()
    {
        var big = Diff + new string('+', 600 * 1024) + "\n";
        var git = new FakeGit(new Dictionary<string, string>
        {
            ["rev-parse --is-inside-work-tree"] = "true\n",
            ["remote get-url origin"] = "https://x/r\n",
            ["rev-parse --abbrev-ref @{u}"] = "origin/main\n",
            ["merge-base HEAD origin/main"] = "abc1234def\n",
            ["rev-parse --abbrev-ref HEAD"] = "feat\n",
            ["diff abc1234def"] = big,
        });
        var ex = await Should.ThrowAsync<
            HandoffCollect.CollectException>(() =>
                new HandoffCollect(git)
                    .CollectAsync(false, default));
        ex.Message.ShouldContain("MaxDiffKb");
    }

    [Fact]
    public async Task Collect_Requires_Git_Repo_And_Remote()
    {
        var noGit = new FakeGit(
            new Dictionary<string, string>());
        await Should.ThrowAsync<HandoffCollect.CollectException>(() =>
            new HandoffCollect(noGit).CollectAsync(false, default));

        var noRemote = new FakeGit(new Dictionary<string, string>
        {
            ["rev-parse --is-inside-work-tree"] = "true\n",
            ["remote get-url origin"] = "!",
        });
        var ex = await Should.ThrowAsync<
            HandoffCollect.CollectException>(() =>
                new HandoffCollect(noRemote)
                    .CollectAsync(false, default));
        ex.Message.ShouldContain("remote");
    }

    // ------------------------------------------------- http stub

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> fn)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(fn(request));
    }

    private static HttpResponseMessage Json(object payload,
        HttpStatusCode code = HttpStatusCode.OK) => new(code)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload),
            Encoding.UTF8, "application/json"),
        };

    // ------------------------------------------------- handoff cmd

    [Fact]
    public async Task Handoff_DryRun_Never_Calls_Api()
    {
        var apiCalls = 0;
        var settings = new HandoffCommand.Settings
        { DryRun = true, Json = true };
        var git = new FakeGit(BaseRepo());
        var code = await HandoffCommand.RunAsync(settings, git,
            (u, s, h, ct) =>
            {
                apiCalls++;
                return Task.FromResult(new CloudApiClient(
                    new HttpClient(new StubHandler(_ =>
                        Json(new { }))), "http://stub"));
            }, default);
        code.ShouldBe(0);
        apiCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Handoff_Posts_Patch_And_Polls_Task()
    {
        string? postedPatch = null;
        var settings = new HandoffCommand.Settings
        { Json = true, Message = "continue" };
        var git = new FakeGit(BaseRepo());
        var calls = 0;
        var code = await HandoffCommand.RunAsync(settings, git,
            (u, s, h, ct) => Task.FromResult(new CloudApiClient(
                new HttpClient(new StubHandler(req =>
                {
                    var path = req.RequestUri!.AbsolutePath;
                    if (path == "/api/v1/conversations/handoff")
                    {
                        var body = req.Content!
                            .ReadAsStringAsync(ct).Result;
                        postedPatch = JsonDocument
                            .Parse(body).RootElement
                            .GetProperty("diff_patch").GetString();
                        return Json(new
                        {
                            id = "task-1",
                            status = "WORKING",
                        }, HttpStatusCode.Accepted);
                    }

                    if (path.Contains("start-tasks"))
                    {
                        calls++;
                        return Json(new
                        {
                            id = "task-1",
                            status = "READY",
                            app_conversation_id = "conv-9",
                        });
                    }

                    return Json(new { }, HttpStatusCode.NotFound);
                })), "http://stub")), default);
        code.ShouldBe(0);
        postedPatch.ShouldBe(Diff);
        calls.ShouldBeGreaterThan(0);
    }

    // ------------------------------------------------- pickup cmd

    private static HttpResponseMessage Package(string patch = Diff) =>
        Json(new
        {
            repo = "o/r",
            head_branch = "work-x",
            diff_patch = patch,
            summary = "did stuff",
            binary_files = Array.Empty<string>(),
            generated_at = DateTimeOffset.UtcNow,
        });

    private static PickupCommand.Settings P(
        string conv = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
        bool force = false, bool yes = true, bool pr = false) =>
        new()
        {
            Conversation = conv,
            Force = force,
            Yes = yes,
            Pr = pr,
            Json = true,
        };

    [Fact]
    public async Task Pickup_Clean_Applies_On_Handoff_Branch()
    {
        var settings = P();
        var git = new FakeGit(BaseRepo(), (args, input) =>
            args.Contains("--3way") ? (0, "", "") : (0, "", ""));
        var code = await PickupCommand.RunAsync(settings, git,
            (u, s, h, ct) => Task.FromResult(new CloudApiClient(
                new HttpClient(new StubHandler(_ => Package())),
                "http://stub")), default);
        code.ShouldBe(0);
        git.Commands.ShouldContain("checkout -B handoff/aaaaaaaa");
    }

    [Fact]
    public async Task Pickup_Dirty_Refuses_Without_Force()
    {
        var settings = P();
        var outputs = BaseRepo();
        outputs["status --porcelain"] = " M dirty.txt\n";
        var git = new FakeGit(outputs);
        var code = await PickupCommand.RunAsync(settings, git,
            (u, s, h, ct) => Task.FromResult(new CloudApiClient(
                new HttpClient(new StubHandler(_ => Package())),
                "http://stub")), default);
        code.ShouldBe(1);
        git.Commands.ShouldNotContain(c => c.Contains("apply"));
    }

    [Fact]
    public async Task Pickup_Force_Auto_Stashes()
    {
        var settings = P(force: true);
        var outputs = BaseRepo();
        outputs["status --porcelain"] = " M dirty.txt\n";
        outputs["stash push"] = "Saved\n";
        var git = new FakeGit(outputs, (args, input) =>
            args.Contains("--3way") ? (0, "", "") : (0, "", ""));
        var code = await PickupCommand.RunAsync(settings, git,
            (u, s, h, ct) => Task.FromResult(new CloudApiClient(
                new HttpClient(new StubHandler(_ => Package())),
                "http://stub")), default);
        code.ShouldBe(0);
        git.Commands.ShouldContain(
            c => c.StartsWith("stash push"));
    }

    [Fact]
    public async Task Pickup_Conflict_Lists_Rejects_Exit2()
    {
        var settings = P();
        var outputs = BaseRepo();
        outputs["ls-files --others --exclude-standard -- '*.rej'"] =
            "src/a.txt.rej\n";
        var git = new FakeGit(outputs, (args, input) =>
            args.Contains("--3way") ? (1, "", "conflict")
            : args.Contains("--reject") ? (1, "", "rej") : (0, "", ""));
        var code = await PickupCommand.RunAsync(settings, git,
            (u, s, h, ct) => Task.FromResult(new CloudApiClient(
                new HttpClient(new StubHandler(_ => Package())),
                "http://stub")), default);
        code.ShouldBe(2);
        git.Commands.ShouldContain("reset --hard");
        git.Commands.ShouldContain(c => c.Contains("--reject"));
    }

    [Fact]
    public async Task Pickup_Pr_Resolves_Conversation()
    {
        var settings = P("42", pr: true);
        var git = new FakeGit(BaseRepo(), (a, i) => (0, "", ""));
        var convId = "cccccccc-1111-2222-3333-444444444444";
        var code = await PickupCommand.RunAsync(settings, git,
            (u, s, h, ct) => Task.FromResult(new CloudApiClient(
                new HttpClient(new StubHandler(req =>
                {
                    var p = req.RequestUri!.AbsolutePath;
                    if (p.EndsWith("/search"))
                    {
                        return Json(new
                        {
                            items = new[] { new { id = convId } },
                        });
                    }

                    if (p.EndsWith($"/{convId}"))
                    {
                        return Json(new
                        {
                            id = convId,
                            pull_requests = new[]
                            {
                                new { number = 42, url = "u" },
                            },
                        });
                    }

                    if (p.Contains("handoff-package"))
                    {
                        return Package();
                    }

                    return Json(new { }, HttpStatusCode.NotFound);
                })), "http://stub")), default);
        code.ShouldBe(0);
        // --pr → checkout da branch do PR (head_branch do package).
        git.Commands.ShouldContain("checkout -B work-x");
    }
}