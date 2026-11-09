using Spectre.Console;
using Spectre.Console.Cli;

namespace Sandcastle.Cli.Commands;

/// <summary>
/// <c>sandcastle mcp serve</c> — SPEC-124 RF-005: sobe o servidor MCP
/// stdio (<c>sandcastle-mcp</c>) — binário publicado no PATH ou
/// <c>dotnet run --project src/Sandcastle.Mcp</c> dentro do repo.
/// Stdin/stdout passam transparente (é protocolo JSON-RPC); config via
/// <c>SC_API_URL</c>/<c>SC_API_KEY</c> propagadas do ambiente.
/// </summary>
public sealed class McpServeCommand : AsyncCommand<McpServeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--mcp-path <PATH>")]
        public string? McpPath { get; init; }

        [CommandOption("--api-url <URL>")]
        public string? ApiUrl { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings,
        CancellationToken cancellationToken)
    {
        var binary = settings.McpPath ?? FindMcpBinary();
        System.Diagnostics.ProcessStartInfo psi;
        if (binary is not null)
        {
            psi = new System.Diagnostics.ProcessStartInfo(binary);
        }
        else
        {
            var proj = FindMcpProject();
            if (proj is null)
            {
                AnsiConsole.MarkupLine(
                    "[red]sandcastle-mcp não encontrado[/] — publique "
                    + "src/Sandcastle.Mcp ou rode dentro do repo");
                return 1;
            }
            psi = new System.Diagnostics.ProcessStartInfo(
                ResolveOnPath("dotnet"),
                $"run --project \"{proj}\"");
        }

        if (settings.ApiUrl is not null)
        {
            psi.Environment["SC_API_URL"] = settings.ApiUrl;
        }

        psi.RedirectStandardInput = false;
        psi.RedirectStandardOutput = false;
        psi.RedirectStandardError = false;
        var proc = System.Diagnostics.Process.Start(psi)!;
        await proc.WaitForExitAsync(cancellationToken);
        return proc.ExitCode;
    }

    private static string ResolveOnPath(string cmd)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH")
            ?? "").Split(Path.PathSeparator))
        {
            var full = Path.Combine(dir, cmd);
            if (File.Exists(full)) { return full; }
        }
        return cmd;
    }

    private static string? FindMcpBinary()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH")
            ?? "").Split(Path.PathSeparator))
        {
            var full = Path.Combine(dir, "sandcastle-mcp");
            if (File.Exists(full)) { return full; }
        }
        return null;
    }

    private static string? FindMcpProject()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src",
                "Sandcastle.Mcp", "Sandcastle.Mcp.csproj");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }
}