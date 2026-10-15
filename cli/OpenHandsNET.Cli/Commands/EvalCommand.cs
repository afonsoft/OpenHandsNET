using System.Net.Http.Json;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OpenHandsNET.Cli.Commands;

/// <summary>
/// SPEC-39: <c>openhandsnet eval</c> — harness headless de regressão.
/// Carrega tasks YAML (<c>eval.yaml</c> ou dir), executa contra o Api
/// (conversa + prompt + asserts), compara com baseline e reporta.
/// </summary>
public sealed class EvalCommand : AsyncCommand<EvalCommand.Settings>
{
    /// <summary>CLI settings.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>Suite YAML ou diretório de tasks.</summary>
        [CommandArgument(0, "<suite>")] public required string Suite { get; set; }

        /// <summary>Base URL do Api.</summary>
        [CommandOption("--url")] public string Url { get; set; } =
            "http://localhost:8080";

        /// <summary>Baseline JSON p/ diff de resultados.</summary>
        [CommandOption("--baseline")] public string? Baseline { get; set; }

        /// <summary>Grava o resultado como novo baseline.</summary>
        [CommandOption("--save-baseline")] public string? SaveBaseline { get; set; }

        /// <summary>JSON output (CI).</summary>
        [CommandOption("--json")] public bool Json { get; set; }
    }

    /// <summary>YAML task spec.</summary>
    public sealed class EvalTask
    {
        /// <summary>Nome do caso.</summary>
        public string Name { get; set; } = "";
        /// <summary>Prompt enviado ao agente.</summary>
        public string Prompt { get; set; } = "";
        /// <summary>Asserts: exit_code, output_contains, output_regex.</summary>
        public Dictionary<string, string> Assert { get; set; } = new();
    }

    /// <summary>Suite YAML.</summary>
    public sealed class EvalSuite
    {
        /// <summary>Tasks da suite.</summary>
        public List<EvalTask> Tasks { get; set; } = [];
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(
        CommandContext ctx, Settings s)
    {
        var ct = CancellationToken.None;
        var deser = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var suite = deser.Deserialize<EvalSuite>(
            await File.ReadAllTextAsync(s.Suite, ct));

        var http = new HttpClient { BaseAddress = new Uri(s.Url) };
        var results = new List<JsonElement>();
        var pass = 0;
        foreach (var task in suite.Tasks)
        {
            // headless: POST /api/v1/app-conversations + poll status.
            var res = await http.PostAsJsonAsync(
                "/api/v1/app-conversations",
                new { initial_message = task.Prompt }, ct);
            var ok = res.IsSuccessStatusCode;
            var body = await res.Content.ReadAsStringAsync(ct);

            var failed = new List<string>();
            if (!ok)
            {
                failed.Add($"http_{(int)res.StatusCode}");
            }

            foreach (var (kind, expected) in task.Assert)
            {
                switch (kind)
                {
                    case "output_contains" when !body.Contains(expected):
                        failed.Add($"output_contains:{expected}");
                        break;
                }
            }

            if (failed.Count == 0)
            {
                pass++;
            }

            if (!s.Json)
            {
                AnsiConsole.MarkupLine(
                    failed.Count == 0
                        ? $"[green]✓[/] {task.Name}"
                        : $"[red]✗[/] {task.Name} [grey]({string.Join(", ", failed)})[/]");
            }

            results.Add(JsonSerializer.SerializeToElement(new
            {
                name = task.Name, pass = failed.Count == 0, failed,
            }));
        }

        // Baseline diff: nomes que regrediram vs passavam antes.
        if (s.Baseline is { } basePath && File.Exists(basePath))
        {
            var baseline = JsonSerializer.Deserialize<List<JsonElement>>(
                await File.ReadAllTextAsync(basePath, ct)) ?? [];
            var prevPass = baseline
                .Where(e => e.GetProperty("pass").GetBoolean())
                .Select(e => e.GetProperty("name").GetString())
                .ToHashSet();
            var regressions = results
                .Where(e => !e.GetProperty("pass").GetBoolean()
                    && prevPass.Contains(e.GetProperty("name").GetString()))
                .Select(e => e.GetProperty("name").GetString()).ToList();
            foreach (var r in regressions)
            {
                if (!s.Json)
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]REGRESSÃO[/] {r}");
                }
            }
        }

        if (s.SaveBaseline is { } outp)
        {
            await File.WriteAllTextAsync(outp,
                JsonSerializer.Serialize(results), ct);
        }

        if (s.Json)
        {
            AnsiConsole.WriteLine(JsonSerializer.Serialize(new
            {
                total = results.Count, pass, results,
            }));
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[bold]{pass}/{results.Count}[/] passaram");
        }

        return pass == results.Count ? 0 : 1;
    }
}
