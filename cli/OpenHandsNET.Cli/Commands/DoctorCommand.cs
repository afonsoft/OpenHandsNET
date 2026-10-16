using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using OpenHandsNET.Cli.Services;

namespace OpenHandsNET.Cli.Commands;

/// <summary>
/// <c>openhandsnet doctor</c> — environment self-check (SPEC-14 RF-003):
/// api / db / docker / llm / workspace-disk probes; table by default,
/// <c>--json</c> for machines; exit 1 when any required probe fails.
/// </summary>
public sealed class DoctorCommand : AsyncCommand<DoctorCommand.Settings>
{
    /// <summary>CLI options for doctor.</summary>
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }

        [CommandOption("--workspace <PATH>")]
        public string Workspace { get; init; } = "data/workspaces";

        [CommandOption("--json")]
        public bool Json { get; init; }

        /// <summary>SPEC-31 RF-002: install preflight (DNS/egress/disk/ports).</summary>
        [CommandOption("--preflight")]
        public bool Preflight { get; init; }

        /// <summary>SPEC-37 §3: inclui probes de cluster/sysbox/storageclass.</summary>
        [CommandOption("--k8s")]
        public bool K8s { get; init; }
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings)
    {
        var url = settings.ServerUrl
            ?? Environment.GetEnvironmentVariable("OPENHANDSNET_SERVER_URL")
            ?? Environment.GetEnvironmentVariable("OPENHANDSNET_CLOUD_URL")
            ?? "http://localhost:8080";
        var svc = new DoctorService(new HttpClient());
        var report = settings.Preflight
            ? await svc.RunPreflightAsync(
                url, settings.Workspace, CancellationToken.None,
                k8s: settings.K8s)
            : await svc.RunAsync(
                url, settings.Workspace, CancellationToken.None);

        if (settings.Json)
        {
            var payload = new
            {
                server_url = report.ServerUrl,
                healthy = report.Healthy,
                probes = report.Probes.Select(p => new
                {
                    name = p.Name, ok = p.Ok, detail = p.Detail,
                }),
            };
            Console.WriteLine(JsonSerializer.Serialize(payload,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            var table = new Table()
                .AddColumn("probe").AddColumn("status").AddColumn("detail");
            foreach (var p in report.Probes)
            {
                table.AddRow(p.Name,
                    p.Ok ? "[green]ok[/]" : "[red]fail[/]",
                    Markup.Escape(p.Detail));
            }
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine(report.Healthy
                ? "[green]all checks passed[/]"
                : "[red]doctor found failures[/]");
        }

        return report.Healthy ? 0 : 1;
    }
}
