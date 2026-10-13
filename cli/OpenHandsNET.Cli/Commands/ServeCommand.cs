using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenHandsNET.Cli.Commands;

/// <summary>
/// <c>openhandsnet serve</c> — sobe o servidor local: preferindo
/// <c>dotnet run --project</c> num repo OpenHandsNET ou binário publicado.
/// </summary>
public sealed class ServeCommand : AsyncCommand<ServeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--port <PORT>")]
        public int Port { get; init; } = 8080;

        [CommandOption("--api-path <PATH>")]
        public string? ApiPath { get; init; }

        [CommandOption("--mount-cwd")]
        public bool MountCwd { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings)
    {
        var ct = CancellationToken.None;
        var api = settings.ApiPath ?? FindApiProject();
        if (api is null)
        {
            AnsiConsole.MarkupLine(
                "[red]OpenHandsNET.Api não encontrado[/] — rode dentro do repo " +
                "ou use --api-path para um build publicado");
            return 1;
        }

        var env = new Dictionary<string, string>
        {
            ["ASPNETCORE_URLS"] = $"http://localhost:{settings.Port}",
        };
        if (settings.MountCwd)
        {
            env["OPENHANDSNET_WORKSPACE"] = Directory.GetCurrentDirectory();
        }

        var psi = api.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            ? new System.Diagnostics.ProcessStartInfo(
                "dotnet", $"run --project \"{api}\"")
            : new System.Diagnostics.ProcessStartInfo("dotnet", $"\"{api}\"");

        foreach (var kv in env)
        {
            psi.Environment[kv.Key] = kv.Value;
        }

        psi.RedirectStandardOutput = false;
        AnsiConsole.MarkupLine(
            $"subindo servidor em [bold]http://localhost:{settings.Port}[/] …");
        var proc = System.Diagnostics.Process.Start(psi)!;
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }

    private static string? FindApiProject()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, "src", "OpenHandsNET.Api", "OpenHandsNET.Api.csproj");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
