using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.Memory;
using OpenHandsNET.Infrastructure.Agent.SystemPrompt;

using Shouldly;

using Xunit;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>
/// SPEC-90 — pipeline componível de system prompt: contributors
/// ordenados, tolerância a falha, autonomia T1/T2/T3, superfície SDD e
/// política de delegação.
/// </summary>
public sealed class Spec90PromptContributorTests
{
    private static readonly ToolSchema Bash = new(
        "bash", "run shell commands", JsonDocument.Parse("{}").RootElement);
    private static readonly ToolSchema TaskTool = new(
        "task", "spawn a subagent", JsonDocument.Parse("{}").RootElement);

    private static AgentPromptContext Ctx(
        ToolSchema[]? tools = null,
        string? repoContext = null,
        IReadOnlyList<string>? catalog = null,
        Func<CancellationToken, Task<bool>>? sddProbe = null,
        Func<CancellationToken, Task<IReadOnlyList<MemoryEntry>>>?
            digest = null,
        Func<CancellationToken, Task<string>>? workdir = null) =>
        new()
        {
            Tools = tools ?? [Bash],
            RepoContext = repoContext,
            SubagentCatalog = catalog,
            SddProbeFactory = sddProbe,
            MemoryDigestFactory = digest,
            WorkdirFactory = workdir,
            Ct = CancellationToken.None,
        };

    private static SystemPromptComposer Composer(
        params IAgentPromptContributor[] contributors) =>
        new(contributors,
            NullLogger<SystemPromptComposer>.Instance);

    private sealed class Stub(
        int order, string section, bool throws = false)
        : IAgentPromptContributor
    {
        public int Order => order;
        public Task<IReadOnlyList<string>> ContributeAsync(
            AgentPromptContext ctx) =>
            throws
                ? Task.FromException<IReadOnlyList<string>>(
                    new InvalidOperationException("boom"))
                : Task.FromResult<IReadOnlyList<string>>([section]);
    }

    // RF-002/RF-003: blocos migrados reproduzem o builder estático
    [Fact]
    public async Task MigratedBlocks_MatchStaticBuilderOutput()
    {
        var skills = new List<Domain.Skills.Skill>
        {
            new()
            {
                Id = Guid.NewGuid(), Name = "skill one",
                ContentMd = "skill body",
            },
        };
        var ctx = new AgentPromptContext
        {
            SelectedRepository = "owner/repo",
            Tools = [Bash],
            Skills = skills,
            RepoContext = "repo conventions here",
            Ct = CancellationToken.None,
        };

        var expected = Infrastructure.Agent.SystemPromptBuilder.Build(
            ctx.SelectedRepository, ctx.Tools, skills, ctx.RepoContext);
        var actual = await Composer(
            new BaseAgentContributor(),
            new RepoContextContributor(),
            new SkillsContributor())
            .ComposeAsync(ctx);

        actual.ShouldBe(expected);
    }

    // RF-004: composer ordena por Order crescente, ignora vazios
    [Fact]
    public async Task Composer_OrdersByAscendingOrder_SkipsEmpty()
    {
        var output = await Composer(
            new Stub(50, "third"),
            new Stub(0, "first"),
            new Stub(20, ""),
            new Stub(10, "second"))
            .ComposeAsync(Ctx());

        output.ShouldBe("first\n\nsecond\n\nthird\n");
    }

    // RF-004: exceção de um contributor omite só a seção dele
    [Fact]
    public async Task Composer_ToleratesContributorFailure()
    {
        var output = await Composer(
            new Stub(0, "before"),
            new Stub(10, "", throws: true),
            new Stub(20, "after"))
            .ComposeAsync(Ctx());

        output.ShouldBe("before\n\nafter\n");
    }

    // RF-005: bloco de autonomia com tiers T1/T2/T3
    [Fact]
    public async Task AutonomyTiers_AlwaysEmitted()
    {
        var output = await Composer(
            new AutonomyTiersContributor()).ComposeAsync(Ctx());

        output.ShouldContain("T1");
        output.ShouldContain("T2");
        output.ShouldContain("T3");
        output.ShouldContain("never execute them silently");
    }

    // RF-006: Agent:Sdd=auto injeta só quando a probe encontra specs
    [Theory]
    [InlineData("auto", true, true)]
    [InlineData("auto", false, false)]
    [InlineData("on", false, true)]
    [InlineData("off", true, false)]
    public async Task SddWorkflow_ModeAndProbe(
        string mode, bool probe, bool expected)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["Agent:Sdd"] = mode })
            .Build();
        var contributor = new SddWorkflowContributor(cfg);
        var ctx = Ctx(
            sddProbe: _ => System.Threading.Tasks.Task.FromResult(probe),
            workdir: _ => System.Threading.Tasks.Task.FromResult(
                "/nonexistent"));

        var sections = await contributor.ContributeAsync(ctx);

        (sections.Count > 0).ShouldBe(expected);
        if (expected)
        {
            sections[0].ShouldContain("SPEC-*.md");
            sections[0].ShouldContain("RF-###");
        }
    }

    // RF-006: auto sem probe cai na checagem de filesystem do workdir
    [Fact]
    public async Task SddWorkflow_AutoFallsBackToWorkdirFilesystem()
    {
        var cfg = new ConfigurationBuilder().Build();
        var contributor = new SddWorkflowContributor(cfg);
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var ctx = Ctx(
                workdir: _ => System.Threading.Tasks.Task.FromResult(
                    dir.FullName));
            (await contributor.ContributeAsync(ctx)).ShouldBeEmpty();

            Directory.CreateDirectory(Path.Combine(dir.FullName, ".specs"));
            File.WriteAllText(
                Path.Combine(dir.FullName, ".specs", "SPEC-1.md"), "x");
            (await contributor.ContributeAsync(ctx)).ShouldNotBeEmpty();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // RF-007: delegação só com tools task + catálogo não-vazio
    [Fact]
    public async Task Delegation_RequiresTaskToolsAndCatalog()
    {
        var contributor = new DelegationPolicyContributor();

        // sem tool task → silêncio mesmo com catálogo
        (await contributor.ContributeAsync(
            Ctx(catalog: ["explore"]))).ShouldBeEmpty();

        // task habilitado mas catálogo vazio → silêncio
        (await contributor.ContributeAsync(
            Ctx(tools: [TaskTool], catalog: []))).ShouldBeEmpty();

        // task + catálogo → política com os tipos
        var sections = await contributor.ContributeAsync(
            Ctx(tools: [TaskTool], catalog: ["explore", "plan"]));
        sections.ShouldHaveSingleItem()
            .ShouldContain("`explore`, `plan`");
    }

    // RF-001.4: digest de memória memoizado (factory roda 1x)
    [Fact]
    public async Task MemoryDigest_MemoizedAndRendered()
    {
        var calls = 0;
        var contributor = new MemoryDigestContributor();
        var ctx = Ctx(digest: _ =>
        {
            calls++;
            return System.Threading.Tasks.Task
                .FromResult<IReadOnlyList<MemoryEntry>>(
                    [new("k1", "remember this", "org",
                        DateTimeOffset.UtcNow)]);
        });

        var sections = await contributor.ContributeAsync(ctx);
        await ctx.GetMemoryDigestAsync(); // memoized — não re-chama

        calls.ShouldBe(1);
        sections.ShouldHaveSingleItem()
            .ShouldContain("k1: remember this");
    }

    // RF-001.4: sem factory → contributor silencia
    [Fact]
    public async Task MemoryDigest_NullFactory_Silent()
    {
        (await new MemoryDigestContributor()
            .ContributeAsync(Ctx())).ShouldBeEmpty();
    }

    // RF-008: DI resolve IEnumerable<IAgentPromptContributor> + composer
    [Fact]
    public void Di_ResolvesAllContributorsAndComposer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddSingleton<IAgentPromptContributor,
            BaseAgentContributor>();
        services.AddSingleton<IAgentPromptContributor,
            AutonomyTiersContributor>();
        services.AddSingleton<IAgentPromptContributor,
            SddWorkflowContributor>();
        services.AddSingleton<IAgentPromptContributor,
            RepoContextContributor>();
        services.AddSingleton<IAgentPromptContributor,
            SkillsContributor>();
        services.AddSingleton<IAgentPromptContributor,
            MemoryDigestContributor>();
        services.AddSingleton<IAgentPromptContributor,
            DelegationPolicyContributor>();
        services.AddSingleton<SystemPromptComposer>();

        var sp = services.BuildServiceProvider();
        sp.GetServices<IAgentPromptContributor>()
            .Count().ShouldBe(7);
        sp.GetRequiredService<SystemPromptComposer>()
            .ShouldNotBeNull();
    }
}