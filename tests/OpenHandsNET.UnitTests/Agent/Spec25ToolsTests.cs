using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Sandbox;
using OpenHandsNET.Domain.Skills;
using OpenHandsNET.Infrastructure.Agent.Tools;
using OpenHandsNET.Infrastructure.Discovery;
using OpenHandsNET.Infrastructure.Skills;
using OpenHandsNET.Infrastructure.Web;
using SandboxOptions = OpenHandsNET.Sandboxing.SandboxOptions;
using Shouldly;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>
/// SPEC-25: file_edit modes (exact/anchor/hashline), search confinement,
/// repo discovery + repo skills, OutputPolicy caps, web_fetch SSRF guard.
/// </summary>
public sealed class Spec25ToolsTests
{
    private static readonly Domain.Sandbox.Sandbox TestSandbox = new(
        Guid.NewGuid(), Guid.Empty, null, "fake", SandboxStatus.Running,
        null, DateTimeOffset.UtcNow, null, null);

    private static ToolCallContext Ctx(
        FakeSandboxExecutor exec, IServiceProvider? services = null) => new(
        Guid.NewGuid(), TestSandbox, exec, new FakeEventService(),
        services ?? new ServiceCollection().BuildServiceProvider(),
        TimeSpan.FromSeconds(30));

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    // ---- file_edit modes -------------------------------------------------

    [Fact]
    public async Task FileEdit_Exact_Replaces_Single_Occurrence()
    {
        var exec = new FakeSandboxExecutor();
        exec.SeedFile("/workspace/a.cs", "one\ntwo\nthree\n");
        var res = await BuiltinTools.FileEdit().Handler(Ctx(exec),
            Args("""{"path":"/workspace/a.cs","old_string":"two","new_string":"TWO"}"""),
            CancellationToken.None);
        res.IsError.ShouldBeFalse();
        (await exec.ReadFileAsync(TestSandbox, "/workspace/a.cs",
            CancellationToken.None)).ShouldBe("one\nTWO\nthree\n");
    }

    [Fact]
    public async Task FileEdit_Exact_Not_Found_Returns_Hint()
    {
        var exec = new FakeSandboxExecutor();
        exec.SeedFile("/workspace/a.cs", "alpha\nbeta\n");
        var res = await BuiltinTools.FileEdit().Handler(Ctx(exec),
            Args("""{"path":"/workspace/a.cs","old_string":"zzz","new_string":"x"}"""),
            CancellationToken.None);
        res.IsError.ShouldBeTrue();
        res.Observation.GetRawText().ShouldContain("file starts with");
    }

    [Fact]
    public async Task FileEdit_Anchor_Replaces_Marker_Line()
    {
        var exec = new FakeSandboxExecutor();
        exec.SeedFile("/workspace/a.md",
            "keep\n// anchor: deploy\nlast\n");
        var res = await BuiltinTools.FileEdit().Handler(Ctx(exec),
            Args("""{"path":"/workspace/a.md","mode":"anchor","anchor":"deploy","new_string":"replaced line"}"""),
            CancellationToken.None);
        res.IsError.ShouldBeFalse();
        (await exec.ReadFileAsync(TestSandbox, "/workspace/a.md",
            CancellationToken.None)).ShouldBe("keep\nreplaced line\nlast\n");
    }

    [Fact]
    public async Task FileEdit_Anchor_Not_Unique_Errors()
    {
        var exec = new FakeSandboxExecutor();
        exec.SeedFile("/workspace/a.md",
            "# anchor: x\nbody\n# anchor: x\n");
        var res = await BuiltinTools.FileEdit().Handler(Ctx(exec),
            Args("""{"path":"/workspace/a.md","mode":"anchor","anchor":"x","new_string":"y"}"""),
            CancellationToken.None);
        res.IsError.ShouldBeTrue();
        res.Observation.GetRawText().ShouldContain("matches 2 lines");
    }

    [Fact]
    public async Task FileEdit_Hashline_Verifies_Sha1_Prefix()
    {
        var exec = new FakeSandboxExecutor();
        exec.SeedFile("/workspace/a.txt", "l1\nl2\nl3\n");
        var hash = BuiltinTools.LineHash("l2")[..8];
        var res = await BuiltinTools.FileEdit().Handler(Ctx(exec),
            Args($$"""{"path":"/workspace/a.txt","mode":"hashline","line":"2:{{hash}}","new_string":"L2"}"""),
            CancellationToken.None);
        res.IsError.ShouldBeFalse();
        (await exec.ReadFileAsync(TestSandbox, "/workspace/a.txt",
            CancellationToken.None)).ShouldBe("l1\nL2\nl3\n");
    }

    [Fact]
    public async Task FileEdit_Hashline_Mismatch_Errors()
    {
        var exec = new FakeSandboxExecutor();
        exec.SeedFile("/workspace/a.txt", "l1\nl2\nl3\n");
        var res = await BuiltinTools.FileEdit().Handler(Ctx(exec),
            Args("""{"path":"/workspace/a.txt","mode":"hashline","line":"2:deadbeef","new_string":"L2"}"""),
            CancellationToken.None);
        res.IsError.ShouldBeTrue();
        res.Observation.GetRawText().ShouldContain("hash mismatch");
    }

    // ---- search ----------------------------------------------------------

    [Fact]
    public async Task Search_Content_Mode_Builds_Confined_Rg()
    {
        var exec = new FakeSandboxExecutor
        {
            ExecHandler = c => new CommandResult(0, "a.cs:1:x\n", "", false),
        };
        var res = await BuiltinTools.Search(TimeSpan.FromSeconds(30))
            .Handler(Ctx(exec), Args("""{"query":"foo","path":"src"}"""),
                CancellationToken.None);
        res.IsError.ShouldBeFalse();
        exec.Commands.ShouldHaveSingleItem().ShouldContain("rg --line-number");
    }

    [Fact]
    public async Task Search_Files_Mode_Uses_Rg_Files()
    {
        var exec = new FakeSandboxExecutor
        {
            ExecHandler = c => new CommandResult(0, "a.cs\nb.cs\n", "", false),
        };
        await BuiltinTools.Search(TimeSpan.FromSeconds(30))
            .Handler(Ctx(exec),
                Args("""{"mode":"files","query":"x","glob":"*.cs"}"""),
                CancellationToken.None);
        exec.Commands.ShouldHaveSingleItem().ShouldContain("rg --files");
    }

    [Fact]
    public async Task Search_No_Matches_Exit_1_Is_Not_Error()
    {
        var exec = new FakeSandboxExecutor
        {
            ExecHandler = c => new CommandResult(1, "", "", false),
        };
        var res = await BuiltinTools.Search(TimeSpan.FromSeconds(30))
            .Handler(Ctx(exec), Args("""{"query":"zzz"}"""),
                CancellationToken.None);
        res.IsError.ShouldBeFalse();
    }

    // ---- repo discovery + repo skills ------------------------------------

    [Fact]
    public async Task Discovery_Parses_Probe_Output_Into_Manifest()
    {
        var exec = new FakeSandboxExecutor
        {
            ExecHandler = c => new CommandResult(0,
                "INS:AGENTS.md\nSKILL:.openhands/skills/deploy/SKILL.md\n"
                    + "CFG:openhandsnet.yaml\nREADME:README.md\n",
                "", false),
        };
        var svc = new RepoDiscoveryService(
            NullLogger<RepoDiscoveryService>.Instance);
        var manifest = await svc.DiscoverAsync(exec, TestSandbox,
            CancellationToken.None);
        manifest.InstructionFiles.ShouldBe(["AGENTS.md"]);
        manifest.Skills.ShouldHaveSingleItem().Path
            .ShouldBe(".openhands/skills/deploy/SKILL.md");
        manifest.HasRepoConfig.ShouldBeTrue();
        manifest.ReadmePath.ShouldBe("README.md");
    }

    [Fact]
    public async Task RepoSkillProvider_Parses_Frontmatter_And_Caches()
    {
        var exec = new FakeSandboxExecutor();
        exec.SeedFile("/workspace/repo/.openhands/skills/deploy/SKILL.md",
            "---\nname: deploy\ndescription: ship it\n---\nDo the deploy.\n");
        exec.ListHandler = path =>
            path.EndsWith(".openhands/skills", StringComparison.Ordinal)
                ? [new FileEntry("deploy", true, 0)]
                : [];
        var provider = new RepoSkillProvider(
            NullLogger<RepoSkillProvider>.Instance);
        var skills = await provider.LoadAsync(TestSandbox, exec,
            CancellationToken.None);
        var skill = skills.ShouldHaveSingleItem();
        skill.Name.ShouldBe("deploy");
        skill.Description.ShouldBe("ship it");
        skill.ContentMd.ShouldBe("Do the deploy.");
        skill.Source.ShouldBe(SkillSource.Repo);
        skill.RepoPath.ShouldBe(
            "/workspace/repo/.openhands/skills/deploy/SKILL.md");

        // segundo load vem do cache — nenhuma chamada extra de leitura.
        exec.ListHandler = _ => throw new InvalidOperationException("cached");
        (await provider.LoadAsync(TestSandbox, exec, CancellationToken.None))
            .ShouldHaveSingleItem();
        provider.Invalidate(TestSandbox.Id);
    }

    // ---- OutputPolicy -----------------------------------------------------

    [Fact]
    public void OutputPolicy_Truncate_Splits_Head_Tail_With_Marker()
    {
        var text = string.Join('\n',
            Enumerable.Range(1, 1000).Select(i => $"line {i}"));
        var (out_, truncated) = OutputPolicy.Truncate(text, 1000);
        truncated.ShouldBeTrue();
        out_.ShouldContain("truncated");
        out_.Length.ShouldBeLessThan(text.Length);
    }

    [Fact]
    public void OutputPolicy_Short_Text_Untouched()
    {
        var (out_, truncated) = OutputPolicy.Truncate("small", 100);
        truncated.ShouldBeFalse();
        out_.ShouldBe("small");
    }

    [Fact]
    public void OutputPolicy_CapFor_Honors_Tool_And_Mcp_Overrides()
    {
        var policy = new OutputPolicy(Options.Create(new AgentLlmOptions
        {
            ToolOutputCaps = new Dictionary<string, int>
            {
                ["bash"] = 100, ["mcp__*"] = 200,
            },
        }));
        policy.CapFor("bash").ShouldBe(100);
        policy.CapFor("mcp__github__x").ShouldBe(200);
        policy.CapFor("file_read").ShouldBe(60 * 1024);
    }

    // ---- web_fetch SSRF guard --------------------------------------------

    [Fact]
    public async Task WebFetch_Rejects_Non_Http_Scheme()
    {
        var svc = new WebFetchService(new StubHttpFactory(),
            Options.Create(new SandboxOptions()),
            NullLogger<WebFetchService>.Instance);
        var res = await svc.FetchAsync("file:///etc/passwd", 1024,
            CancellationToken.None);
        res.Ok.ShouldBeFalse();
        res.Error!.ShouldContain("http");
    }

    [Fact]
    public async Task WebFetch_Blocks_Loopback_And_Private()
    {
        var svc = new WebFetchService(new StubHttpFactory(),
            Options.Create(new SandboxOptions()),
            NullLogger<WebFetchService>.Instance);
        foreach (var url in new[]
        {
            "http://localhost/x", "http://127.0.0.1/x",
            "http://169.254.169.254/latest",
        })
        {
            var res = await svc.FetchAsync(url, 1024, CancellationToken.None);
            res.Ok.ShouldBeFalse();
            res.Error!.ShouldContain("SSRF");
        }
    }

    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
