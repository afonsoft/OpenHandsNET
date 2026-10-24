using System.Text.Json;

using OpenHandsNET.Cli.Tui;

using Shouldly;

using Spectre.Console;
using Spectre.Console.Rendering;

namespace OpenHandsNET.UnitTests.Cli;

/// <summary>
/// SPEC-101: regressão do dispatch extraído do <see cref="EventRenderer"/>
/// — cada kind de evento tem seu writer e o switch não engole kinds
/// desconhecidos.
/// </summary>
public sealed class EventRendererTests
{
    private static string Render(string json)
    {
        var writer = new StringWriter();
        var prev = AnsiConsole.Console;
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        try
        {
            EventRenderer.Write(
                JsonDocument.Parse(json).RootElement);
            return writer.ToString();
        }
        finally
        {
            AnsiConsole.Console = prev;
        }
    }

    [Fact]
    public void MessageEvent_Renders_Role_And_Text()
    {
        Render("{\"kind\":\"MessageEvent\",\"llm_message\":"
            + "{\"role\":\"user\",\"content\":"
            + "[{\"type\":\"text\",\"text\":\"hello agent\"}]}}")
            .ShouldContain("hello agent");
        Render("{\"kind\":\"MessageEvent\",\"llm_message\":"
            + "{\"role\":\"user\",\"content\":"
            + "[{\"type\":\"text\",\"text\":\"hi\"}]}}")
            .ShouldContain("você");
        Render("{\"kind\":\"MessageEvent\",\"llm_message\":"
            + "{\"role\":\"assistant\",\"content\":"
            + "[{\"type\":\"text\",\"text\":\"ok\"}]}}")
            .ShouldContain("agent");
    }

    [Fact]
    public void ActionEvent_Renders_Tool_And_Command()
    {
        var output = Render("{\"kind\":\"ActionEvent\",\"tool\":"
            + "{\"name\":\"bash\",\"arguments\":{\"command\":\"ls -la\"}}}");
        output.ShouldContain("bash");
        output.ShouldContain("ls -la");
    }

    [Fact]
    public void ObservationEvent_Renders_Output_And_Trims()
    {
        var payload = new string('x', 250);
        var output = Render("{\"kind\":\"ObservationEvent\","
            + "\"observation\":{\"output\":\"" + payload + "\"}}");
        output.ShouldContain("└─");
        // Trim(200) — o renderer quebra linhas na largura do console;
        // contando os x's (join) prova o corte em 200 + reticência.
        output.Replace("\r", "").Replace("\n", "")
            .ShouldContain(new string('x', 200) + "…");
    }

    [Fact]
    public void StateUpdate_And_Errors_Render()
    {
        Render("{\"kind\":\"ConversationStateUpdateEvent\","
            + "\"value\":\"paused\"}")
            .ShouldContain("paused");
        var errorOut = Render(
            "{\"kind\":\"AgentErrorEvent\",\"error\":\"boom\"}");
        errorOut.ShouldContain("AgentErrorEvent");
        errorOut.ShouldContain("boom");
        Render("{\"kind\":\"UserRejectObservation\"}")
            .ShouldContain("action rejected by user");
        Render("{\"kind\":\"CondensationEvent\"}")
            .ShouldContain("CondensationEvent");
    }

    [Fact]
    public void Unknown_Kind_Writes_Nothing()
    {
        Render("{\"kind\":\"SomethingElse\"}").Trim()
            .ShouldBeEmpty();
        Render("{\"no_kind\":true}").Trim()
            .ShouldBeEmpty();
    }
}
