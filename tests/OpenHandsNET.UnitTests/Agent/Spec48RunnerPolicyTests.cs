using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Application.Agent;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Infrastructure.Agent;
using OpenHandsNET.Infrastructure.Agent.Security;
using OpenHandsNET.Infrastructure.Conversations;
using OpenHandsNET.Infrastructure.Persistence;

using Shouldly;

using Xunit;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>
/// SPEC-48 RF-002/004: policy por org no gate do runner — ask de
/// <c>mcp__*</c>, <c>MinConfirmRisk</c>, <c>AnalyzerOverride</c> e resume
/// fail-closed (<c>ResumeStrict</c>).
/// </summary>
public sealed class Spec48RunnerPolicyTests
{
    private const string FinishScript = """
        [{"tool":{"name":"finish","arguments":{"final_message":"done"}}}]
        """;

    private const string McpScript = """
        [{"tool":{"name":"mcp__srv__do","arguments":{}}},
         {"tool":{"name":"finish","arguments":{"final_message":"done"}}}]
        """;

    // ---------- AskMcpTools ----------

    [Fact]
    public async Task Mcp_Tool_Asks_By_Default_Guardrail()
    {
        // Status quo (SPEC-15): mcp__* é fronteira de credenciais —
        // balanced mantém o piso mesmo com AskMcpTools=false.
        await using var fx = new AgentRunnerFixture(McpScript);
        var run = fx.Runner.RunAsync(CancellationToken.None);

        await WaitForAsync(() =>
            fx.Events.Events.OfType<ConversationStateUpdateEvent>()
                .LastOrDefault(e => e.Key == "execution_status"
                    && e.Value != null
                    && e.Value.ToString()!.Contains("waiting")));

        var actionId = fx.Events.Events.OfType<ActionEvent>()
            .Single(e => e.ToolName == "mcp__srv__do").Id;
        fx.Runner.ResolveConfirmation(actionId, approved: true);
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        (await fx.RecordStatusAsync()).ShouldBe("Finished");
    }

    [Fact]
    public async Task Mcp_Tool_Runs_Free_Under_Permissive()
    {
        // permissive: opt-out explícito da org sobre o piso mcp__*.
        await using var fx = new AgentRunnerFixture(McpScript,
            securityPolicy: new FixedPolicy(
                SecurityPolicyPresets.Resolve("permissive", null)));
        await fx.Runner.RunAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(15));

        fx.Events.Events.OfType<ConversationStateUpdateEvent>()
            .ShouldNotContain(e => e.Key == "execution_status"
                && e.Value != null
                && e.Value.ToString()!.Contains("waiting"));
        (await fx.RecordStatusAsync()).ShouldBe("Finished");
    }

    [Fact]
    public async Task Mcp_Tool_Asks_When_Policy_Requires()
    {
        await using var fx = new AgentRunnerFixture(McpScript,
            securityPolicy: new FixedPolicy(
                SecurityPolicyPresets.Resolve("strict", null)));
        var run = fx.Runner.RunAsync(CancellationToken.None);

        await WaitForAsync(() =>
            fx.Events.Events.OfType<ConversationStateUpdateEvent>()
                .LastOrDefault(e => e.Key == "execution_status"
                    && e.Value?.ToString().Contains("waiting") == true));

        var actionId = fx.Events.Events.OfType<ActionEvent>()
            .Single(e => e.ToolName == "mcp__srv__do").Id;
        fx.Runner.ResolveConfirmation(actionId, approved: true);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        (await fx.RecordStatusAsync()).ShouldBe("Finished");
    }

    // ---------- MinConfirmRisk ----------

    [Fact]
    public async Task Strict_Floor_Confirms_Medium_Risk()
    {
        // "sudo apt update" → pattern analyzer Medium; strict confirma.
        const string script = """
            [{"tool":{"name":"bash","arguments":{"command":"sudo apt update"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"ok"}}}]
            """;
        await using var fx = new AgentRunnerFixture(script,
            security: new PatternSecurityAnalyzer(),
            securityPolicy: new FixedPolicy(
                SecurityPolicyPresets.Resolve("strict", null)));
        var run = fx.Runner.RunAsync(CancellationToken.None);

        await WaitForAsync(() =>
            fx.Events.Events.OfType<ConversationStateUpdateEvent>()
                .LastOrDefault(e => e.Key == "execution_status"
                    && e.Value?.ToString().Contains("waiting") == true));

        fx.Events.Events.OfType<AgentDiagnosticEvent>()
            .ShouldContain(d => d.Code == "security_risk"
                && d.Message.Contains("policy=strict")
                && d.Message.Contains("min_confirm=medium"));
        var actionId = fx.Events.Events.OfType<ActionEvent>()
            .Single(e => e.ToolName == "bash").Id;
        fx.Runner.ResolveConfirmation(actionId, approved: true);
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        fx.Executor.Commands.ShouldContain("sudo apt update");
    }

    [Fact]
    public async Task Permissive_Keeps_Medium_At_Info_Only()
    {
        const string script = """
            [{"tool":{"name":"bash","arguments":{"command":"sudo apt update"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"ok"}}}]
            """;
        await using var fx = new AgentRunnerFixture(script,
            security: new PatternSecurityAnalyzer(),
            securityPolicy: new FixedPolicy(
                SecurityPolicyPresets.Resolve("permissive", null)));
        await fx.Runner.RunAsync(CancellationToken.None);

        fx.Events.Events.OfType<AgentDiagnosticEvent>()
            .ShouldContain(d => d.Code == "security_risk"
                && d.Severity == "info");
        fx.Executor.Commands.ShouldContain("sudo apt update");
        (await fx.RecordStatusAsync()).ShouldBe("Finished");
    }

    // ---------- AnalyzerOverride ----------

    [Fact]
    public async Task Analyzer_Override_Off_Skips_Analysis()
    {
        var counting = new CountingAnalyzer();
        const string script = """
            [{"tool":{"name":"bash","arguments":{"command":"echo hello"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"ok"}}}]
            """;
        await using var fx = new AgentRunnerFixture(script,
            security: counting,
            securityPolicy: new FixedPolicy(
                SecurityPolicyPresets.Resolve("permissive",
                    new SecurityPolicyUpdate(null, "off", null, null, null))));
        await fx.Runner.RunAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(15));

        counting.Calls.ShouldBe(0);
        fx.Events.Events.OfType<AgentDiagnosticEvent>()
            .ShouldNotContain(d => d.Code == "security_risk");
        fx.Executor.Commands.ShouldContain("echo hello"); // off → sem analyzer
    }

    [Fact]
    public async Task Analyzer_Override_Llm_Without_Cache_Falls_Back()
    {
        // Fixture não registra IMemoryCache → fallback + diagnostic.
        var counting = new CountingAnalyzer();
        const string script = """
            [{"tool":{"name":"bash","arguments":{"command":"ls -la"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"ok"}}}]
            """;
        await using var fx = new AgentRunnerFixture(script,
            security: counting,
            securityPolicy: new FixedPolicy(
                SecurityPolicyPresets.Resolve("strict", null)));
        await fx.Runner.RunAsync(CancellationToken.None);

        fx.Events.Events.OfType<AgentDiagnosticEvent>()
            .ShouldContain(d => d.Code == "security_analyzer_fallback"
                && d.Severity == "warning");
        counting.Calls.ShouldBeGreaterThan(0); // caiu no global
    }

    // ---------- ResumeStrict ----------

    [Fact]
    public async Task Strict_Resume_Blocks_Shrunken_Toolset()
    {
        await using var fx = new AgentRunnerFixture(FinishScript,
            securityPolicy: new FixedPolicy(
                SecurityPolicyPresets.Resolve("strict", null)));
        await fx.Runner.RunAsync(CancellationToken.None);

        var db = fx.ScopeServices.GetRequiredService<AppDbContext>();
        var conv = await db.Conversations.FindAsync(fx.ConversationId);
        conv!.PermissionsJson = PermissionRules.ToJson(
            [new AgentPermissionRule("file_write", PermissionDecision.Deny)]);
        conv.ExecutionStatus = "Running";
        await db.SaveChangesAsync();

        await fx.Runner.RunAsync(CancellationToken.None);

        fx.Events.Events.OfType<AgentDiagnosticEvent>()
            .ShouldContain(d => d.Code == "resume_drift"
                && d.Severity == "error"
                && d.Message.Contains("file_write"));
        (await fx.RecordStatusAsync()).ShouldBe("Error");
    }

    [Fact]
    public async Task Open_Resume_Still_Warns_And_Continues()
    {
        await using var fx = new AgentRunnerFixture(FinishScript);
        await fx.Runner.RunAsync(CancellationToken.None);

        var db = fx.ScopeServices.GetRequiredService<AppDbContext>();
        var conv = await db.Conversations.FindAsync(fx.ConversationId);
        conv!.PermissionsJson = PermissionRules.ToJson(
            [new AgentPermissionRule("file_write", PermissionDecision.Deny)]);
        conv.ExecutionStatus = "Running";
        await db.SaveChangesAsync();

        await fx.Runner.RunAsync(CancellationToken.None);

        fx.Events.Events.OfType<AgentDiagnosticEvent>()
            .ShouldContain(d => d.Code == "resume_drift"
                && d.Severity == "warning");
        (await fx.RecordStatusAsync()).ShouldBe("Finished");
    }

    // ---------- doubles ----------

    private sealed class FixedPolicy(SecurityPolicy policy)
        : ISecurityPolicyService
    {
        public Task<SecurityPolicy> GetAsync(Guid? orgId, CancellationToken ct) =>
            Task.FromResult(policy);
        public Task<SecurityPolicy> SetAsync(
            Guid orgId, SecurityPolicyUpdate? update, Guid? actorUserId,
            CancellationToken ct) => Task.FromResult(policy);
    }

    private sealed class CountingAnalyzer : ISecurityAnalyzer
    {
        public int Calls;
        public Task<SecurityRisk> AnalyzeAsync(
            string toolName, System.Text.Json.JsonElement args,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(SecurityRisk.Low);
        }
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