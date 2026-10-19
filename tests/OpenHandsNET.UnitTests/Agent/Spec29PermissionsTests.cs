using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Application.Agent;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Infrastructure.Agent;
using OpenHandsNET.Infrastructure.Conversations;

using Shouldly;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>
/// SPEC-29 AC: regras allow/deny/ask (glob + especificidade), org deny floor,
/// plan mode overlay, deny fora do catálogo, ask força confirmação.
/// </summary>
public sealed class Spec29PermissionsTests
{
    private const string FinishScript = """
        [{"tool":{"name":"finish","arguments":{"final_message":"done"}}}]
        """;

    // ---------- PermissionEvaluator (puro) ----------

    [Fact]
    public void Glob_Rule_Matches_Mcp_Prefix()
    {
        var eval = new PermissionEvaluator(
            null,
            [new AgentPermissionRule("mcp__*", PermissionDecision.Deny)],
            null, planMode: false);
        eval.Decide("mcp__github__create_issue")
            .ShouldBe(PermissionDecision.Deny);
        eval.Decide("bash").ShouldBeNull();
    }

    [Fact]
    public void More_Specific_Rule_Wins_Over_Glob()
    {
        var eval = new PermissionEvaluator(
            null,
            [
                new AgentPermissionRule("mcp__*", PermissionDecision.Deny),
                new AgentPermissionRule(
                    "mcp__docs__*", PermissionDecision.Allow),
            ],
            null, planMode: false);
        eval.Decide("mcp__docs__search").ShouldBe(PermissionDecision.Allow);
        eval.Decide("mcp__github__pr").ShouldBe(PermissionDecision.Deny);
    }

    [Fact]
    public void Org_Deny_Is_A_Floor_Over_Conversation_Allow()
    {
        var eval = new PermissionEvaluator(
            [new AgentPermissionRule("bash", PermissionDecision.Deny)],
            [new AgentPermissionRule("bash", PermissionDecision.Allow)],
            null, planMode: false);
        eval.Decide("bash").ShouldBe(PermissionDecision.Deny);
    }

    [Fact]
    public void Plan_Mode_Denies_Mutations_And_Asks_Bash()
    {
        var eval = new PermissionEvaluator(
            null,
            [new AgentPermissionRule("file_write", PermissionDecision.Allow)],
            null, planMode: true);
        eval.Decide("file_write").ShouldBe(PermissionDecision.Deny);
        eval.Decide("bash").ShouldBe(PermissionDecision.Ask);
        eval.Decide("search").ShouldBeNull(); // read-only passa
        eval.Decide("finish").ShouldBeNull();
    }

    [Fact]
    public void Parse_Org_Settings_Extracts_Agent_Permissions()
    {
        var rules = PermissionRules.ParseOrgSettings(
            """{"agent_permissions":[{"pattern":"git_*","decision":"deny"}]}""");
        rules.Count.ShouldBe(1);
        rules[0].Matches("git_commit").ShouldBeTrue();
    }

    // ---------- PermissionedToolRegistry ----------

    [Fact]
    public void Denied_Tools_Are_Hidden_From_The_Catalog()
    {
        var scopes = new ServiceCollection().BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        var registry = new ToolRegistry(
            new InMemoryTaskTrackerService(scopes),
            Microsoft.Extensions.Options.Options.Create(new AgentLlmOptions()));
        var eval = new PermissionEvaluator(
            null,
            [new AgentPermissionRule("file_write", PermissionDecision.Deny),
             new AgentPermissionRule("mcp__*", PermissionDecision.Deny)],
            null, planMode: false);
        var filtered = new PermissionedToolRegistry(registry, eval);

        filtered.All.ShouldNotContain(t => t.Schema.Name == "file_write");
        filtered.All.ShouldContain(t => t.Schema.Name == "file_read");
        filtered.Find("file_write").ShouldBeNull();
        filtered.Find("mcp__x__y").ShouldBeNull(); // deny antes do bridge
        filtered.Find("file_read").ShouldNotBeNull();
    }

    // ---------- Runner e2e ----------

    [Fact]
    public async Task Denied_Tool_Call_Emits_Permission_Deny_Diagnostic()
    {
        const string script = """
            [{"tool":{"name":"file_write","arguments":{"path":"/x","content":"y"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"ok"}}}]
            """;
        await using var fx = new AgentRunnerFixture(script,
            configureConv: c => c.PermissionsJson = PermissionRules.ToJson(
                [new AgentPermissionRule(
                    "file_write", PermissionDecision.Deny)]));
        await fx.Runner.RunAsync(CancellationToken.None);

        fx.Events.Events.OfType<AgentDiagnosticEvent>()
            .ShouldContain(d => d.Code == "permission_deny");
        var obs = fx.Events.Events.OfType<ObservationEvent>()
            .First(e => e.ToolName == "file_write");
        obs.Observation.GetProperty("error").GetString()!
            .ShouldContain("denied");
        (await fx.RecordStatusAsync()).ShouldBe("Finished");
    }

    [Fact]
    public async Task Plan_Mode_Denies_File_Write()
    {
        const string script = """
            [{"tool":{"name":"file_write","arguments":{"path":"/x","content":"y"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"ok"}}}]
            """;
        await using var fx = new AgentRunnerFixture(script,
            configureConv: c => c.Mode = "plan");
        await fx.Runner.RunAsync(CancellationToken.None);

        fx.Events.Events.OfType<AgentDiagnosticEvent>()
            .ShouldContain(d => d.Code == "permission_deny");
        (await fx.RecordStatusAsync()).ShouldBe("Finished");
    }

    [Fact]
    public async Task Ask_Rule_On_Non_Risky_Tool_Still_Confirms()
    {
        const string script = """
            [{"tool":{"name":"think","arguments":{"thought":"hmm"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"ok"}}}]
            """;
        await using var fx = new AgentRunnerFixture(script,
            configureConv: c => c.PermissionsJson = PermissionRules.ToJson(
                [new AgentPermissionRule("think", PermissionDecision.Ask)]));
        var run = fx.Runner.RunAsync(CancellationToken.None);

        await WaitForAsync(() =>
            fx.Events.Events.OfType<ConversationStateUpdateEvent>()
                .LastOrDefault(e => e.Key == "execution_status"
                    && e.Value?.ToString().Contains("waiting") == true));
        var actionId = fx.Events.Events.OfType<ActionEvent>()
            .Single(e => e.ToolName == "think").Id;
        fx.Runner.ResolveConfirmation(actionId, approved: true);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        fx.Events.Events.OfType<ObservationEvent>()
            .ShouldContain(e => e.ToolName == "think");
        (await fx.RecordStatusAsync()).ShouldBe("Finished");
    }

    [Fact]
    public async Task Explicit_Allow_Skips_Confirmation_Mode()
    {
        const string script = """
            [{"tool":{"name":"bash","arguments":{"command":"echo hi"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"ok"}}}]
            """;
        await using var fx = new AgentRunnerFixture(script,
            confirmationMode: true,
            configureConv: c => c.PermissionsJson = PermissionRules.ToJson(
                [new AgentPermissionRule("bash", PermissionDecision.Allow)]));
        await fx.Runner.RunAsync(CancellationToken.None);

        fx.Executor.Commands.ShouldContain("echo hi");
        fx.Events.Events.OfType<DecisionAutoEvent>()
            .ShouldContain(d => d.Reason == "permission_allow");
        (await fx.RecordStatusAsync()).ShouldBe("Finished");
    }

    private static async Task WaitForAsync(
        Func<object?> probe, int ms = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (probe() is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
    }
}