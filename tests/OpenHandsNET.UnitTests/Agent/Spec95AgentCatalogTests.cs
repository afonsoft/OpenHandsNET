using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Sandbox;
using OpenHandsNET.Infrastructure.Agent;
using OpenHandsNET.Infrastructure.Agent.Subagents;
using OpenHandsNET.Infrastructure.Agent.Tools;

using Shouldly;

using Xunit;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>
/// SPEC-95 — catálogo declarativo de agents (<c>*.agent.md</c> em camadas)
/// + tool <c>agent_catalog</c> + <c>subagent_type</c> declarativo no
/// <c>task</c>.
/// </summary>
public sealed class Spec95AgentCatalogTests
{
    private static AgentCatalogService Catalog() =>
        new(NullLogger<AgentCatalogService>.Instance);

    private static Sandbox Sandbox() => new(Guid.NewGuid(), Guid.Empty,
        null, "fake", SandboxStatus.Running, null,
        DateTimeOffset.UtcNow, null, null);

    // ---------- RF-002: parser tolerante ----------

    [Fact]
    public void Parse_Valid_ReturnsDefinition()
    {
        var def = Catalog().Parse("""
            ---
            name: repo-reviewer
            description: Reviews diffs for correctness
            tools: [file_read, bash]
            model: inherit
            ---
            You are a strict reviewer.
            """, "workspace", "/w/.claude/agents/r.agent.md");

        def.ShouldNotBeNull();
        def.Name.ShouldBe("repo-reviewer");
        def.Description.ShouldBe("Reviews diffs for correctness");
        def.Tools.ShouldBe(["file_read", "bash"]);
        def.Model.ShouldBeNull(); // inherit → null
        def.Body.ShouldBe("You are a strict reviewer.");
        def.Source.ShouldBe("workspace");
    }

    [Theory]
    [InlineData("---\ndescription: no name\n---\nbody")]
    [InlineData("---\nname: no-desc\n---\nbody")]
    [InlineData("no frontmatter at all")]
    [InlineData("---\nname: x\ndescription: y")] // sem fence de fechamento
    public void Parse_Invalid_ReturnsNull(string md)
    {
        Catalog().Parse(md, "user", "/a.agent.md").ShouldBeNull();
    }

    [Fact]
    public void Parse_ToolsCsv_AndModelOverride()
    {
        var def = Catalog().Parse("""
            ---
            name: a
            description: d
            tools: file_read, search
            model: gpt-4o-mini
            ---
            b
            """, "user", "/a.agent.md");

        def!.Tools.ShouldBe(["file_read", "search"]);
        def.Model.ShouldBe("gpt-4o-mini");
    }

    // ---------- RF-006: caps ----------

    [Fact]
    public void Parse_Over64Kb_ReturnsNull()
    {
        var md = "---\nname: big\ndescription: d\n---\n"
            + new string('x', 70 * 1024);
        Catalog().Parse(md, "workspace", "/big.agent.md").ShouldBeNull();
    }

    // ---------- RF-001: descoberta em camadas ----------

    [Fact]
    public async Task ListAsync_LayerOverlay_WorkspaceWins()
    {
        var home = Directory.CreateTempSubdirectory();
        try
        {
            var prevHome = AgentCatalogService.HomeResolver;
            AgentCatalogService.HomeResolver = () => home.FullName;
            try
            {
                var userDir = Path.Combine(
                    home.FullName, ".openhandsnet/agents");
                Directory.CreateDirectory(userDir);
                await File.WriteAllTextAsync(
                    Path.Combine(userDir, "explore.agent.md"), """
                        ---
                        name: explore
                        description: user-level override
                        ---
                        user body
                        """);

                var executor = new FakeSandboxExecutor();
                executor.SeedFile(
                    "/workspace/repo/.claude/agents/explore.agent.md", """
                    ---
                    name: explore
                    description: workspace override
                    ---
                    ws body
                    """);
                executor.SeedFile(
                    "/workspace/repo/.claude/agents/reviewer.agent.md", """
                    ---
                    name: reviewer
                    description: reviews code
                    tools: [file_read, think, finish]
                    ---
                    Review carefully.
                    """);

                var defs = await Catalog().ListAsync(
                    Sandbox(), executor, CancellationToken.None);

                var explore = defs.Single(d => d.Name == "explore");
                explore.Source.ShouldBe("workspace");
                explore.Description.ShouldBe("workspace override");

                defs.ShouldContain(d => d.Name == "reviewer"
                    && d.Source == "workspace");
                defs.ShouldContain(d => d.Name == "general"
                    && d.Source == "builtin");
            }
            finally
            {
                AgentCatalogService.HomeResolver = prevHome;
            }
        }
        finally
        {
            home.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ListAsync_NoSandbox_BuiltinPlusUserOnly()
    {
        var defs = await Catalog().ListAsync(
            null, null, CancellationToken.None);
        defs.ShouldNotBeEmpty();
        defs.Select(d => d.Source).Distinct().ToList()
            .ShouldBeSubsetOf(["builtin", "user"]);
    }

    // ---------- RF-003: agent_catalog tool ----------

    [Fact]
    public async Task AgentCatalogTool_ListsEffectiveCatalog()
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<IAgentCatalog>(Catalog());
        var sp = sc.BuildServiceProvider();
        var ctx = new ToolCallContext(Guid.NewGuid(), Sandbox(),
            new FakeSandboxExecutor(), new FakeEventService(), sp,
            TimeSpan.FromMinutes(1));

        var result = await BuiltinTools.AgentCatalogTool().Handler(
            ctx, JsonDocument.Parse("{}").RootElement,
            CancellationToken.None);

        result.IsError.ShouldBeFalse();
        var text = result.Observation.GetProperty("catalog").GetString()!;
        text.ShouldContain("explore — ");
        text.ShouldContain("(builtin)");
    }

    // ---------- RF-004: subagent_type declarativo ----------

    [Fact]
    public async Task Task_DeclarativeType_RunsWithAgentBody()
    {
        var executor = new FakeSandboxExecutor();
        executor.SeedFile(
            "/workspace/repo/.claude/agents/reviewer.agent.md", """
            ---
            name: reviewer
            description: reviews code
            tools: [think, finish]
            ---
            STRICT REVIEWER PERSONA
            """);
        var llm = new CapturingLlm();
        var scoped = new ServiceCollection()
            .AddSingleton<IAgentCatalog>(Catalog())
            .AddSingleton<IAgentLlmProvider>(llm)
            .AddSingleton<ISubagentHost, SubagentHost>()
            .AddSingleton<ITaskTrackerService, InMemoryTaskTrackerService>()
            .AddSingleton<IToolRegistry, ToolRegistry>()
            .AddSingleton(Options.Create(new AgentLlmOptions()))
            .BuildServiceProvider();
        var ctx = new ToolCallContext(Guid.NewGuid(), Sandbox(),
            executor, new FakeEventService(), scoped,
            TimeSpan.FromMinutes(1), ToolCallId: "call-1");

        var result = await BuiltinTools.SpawnTask().Handler(ctx,
            JsonDocument.Parse("""
                {"prompt":"review it","subagent_type":"reviewer"}
                """).RootElement, CancellationToken.None);

        result.IsError.ShouldBeFalse();
        result.Observation.GetProperty("result").GetString()!
            .ShouldBe("child done");
        llm.SystemPrompt.ShouldNotBeNull()
            .ShouldContain("STRICT REVIEWER PERSONA");
    }

    [Fact]
    public async Task Task_UnknownType_ListsDeclarativeNames()
    {
        var executor = new FakeSandboxExecutor();
        executor.SeedFile(
            "/workspace/repo/.claude/agents/reviewer.agent.md", """
            ---
            name: reviewer
            description: reviews code
            ---
            body
            """);
        var services = new ServiceCollection()
            .AddSingleton<IAgentCatalog>(Catalog())
            .AddSingleton<IAgentLlmProvider>(
                new MockLlmProvider())
            .AddSingleton<ISubagentHost, SubagentHost>()
            .AddSingleton(Options.Create(new AgentLlmOptions()))
            .BuildServiceProvider();
        var ctx = new ToolCallContext(Guid.NewGuid(), Sandbox(),
            executor, new FakeEventService(), services,
            TimeSpan.FromMinutes(1));

        var result = await BuiltinTools.SpawnTask().Handler(ctx,
            JsonDocument.Parse("""
                {"prompt":"x","subagent_type":"nope"}
                """).RootElement, CancellationToken.None);

        result.IsError.ShouldBeTrue();
        var error = result.Observation.GetProperty("error").GetString()!;
        error.ShouldContain("unknown subagent_type 'nope'");
        error.ShouldContain("reviewer");
        error.ShouldContain("explore");
    }

    // ---------- RF-005: allowlist nunca escala ----------

    [Fact]
    public void DeclarativeAllowlist_NeverEscapesRegistryOrReserved()
    {
        // agente declara tool que não existe no run + reservada
        var allow = SubagentPresets.ResolveDeclarative(
            ["admin_shell", "bash", "task"]);
        var scopeFactory = new ServiceCollection().BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        var inner = new ToolRegistry(
            new InMemoryTaskTrackerService(scopeFactory),
            Options.Create(new AgentLlmOptions()));
        var filtered = new FilterToolRegistry(inner, allow);

        // admin_shell não existe no registry → não aparece;
        // task é reservada → excluída mesmo declarada.
        filtered.All.Select(t => t.Schema.Name)
            .ShouldBe(["bash"]);
    }

    [Fact]
    public void DeclarativeAllowlist_NullTools_InheritsMinusReserved()
    {
        var allow = SubagentPresets.ResolveDeclarative(null);
        allow("task").ShouldBeFalse();
        allow("task_status").ShouldBeFalse();
        allow("task_result").ShouldBeFalse();
        allow("bash").ShouldBeTrue();
    }

    /// <summary>LLM que captura o system prompt e finaliza via finish.</summary>
    private sealed class CapturingLlm : IAgentLlmProvider
    {
        public string? SystemPrompt { get; private set; }
        public string ModelId => "capture";
        public Task<ModelInfo> GetModelInfoAsync(CancellationToken ct) =>
            Task.FromResult(new ModelInfo(ModelId, "mock"));

        public async IAsyncEnumerable<ILlmStreamChunk> ChatStreamAsync(
            IReadOnlyList<LlmMessage> messages,
            IReadOnlyList<ToolSchema> tools,
            LlmOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken ct)
        {
            SystemPrompt ??= messages
                .FirstOrDefault(m => m.Role == "system")?.Content;
            yield return new ToolCallDelta(0, "c1", "finish",
                "{\"final_message\":\"child done\"}");
            await Task.CompletedTask;
        }
    }
}