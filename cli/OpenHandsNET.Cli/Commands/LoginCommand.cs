using OpenHandsNET.Cli.Services;

using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenHandsNET.Cli.Commands;

/// <summary><c>openhandsnet login</c> — device flow ou --api-key direto.</summary>
public sealed class LoginCommand : AsyncCommand<LoginCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }

        [CommandOption("--api-key <KEY>")]
        public string? ApiKey { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings)
    {
        var ct = CancellationToken.None;
        var url = settings.ServerUrl
            ?? Environment.GetEnvironmentVariable("OPENHANDSNET_SERVER_URL")
            ?? Environment.GetEnvironmentVariable("OPENHANDSNET_CLOUD_URL")
            ?? "http://localhost:8080";
        var store = new CredentialStore();

        if (settings.ApiKey is { } key)
        {
            await store.SaveAsync(new CredentialStore.Credential
            {
                ServerUrl = url,
                ApiKey = key,
            }, ct);
            AnsiConsole.MarkupLine(
                $"[green]✓[/] API key gravada para [bold]{url}[/] " +
                $"([grey]{Mask(key)}[/])");
            return 0;
        }

        // Device flow (RFC 8628) — só servidores com OIDC (SPEC-08).
        var flow = new DeviceFlowClient(new HttpClient());
        try
        {
            var code = await flow.RequestCodeAsync(url, ct);
            AnsiConsole.MarkupLine(
                $"Abra [bold]{code.VerificationUri}[/] e informe o código " +
                $"[bold yellow]{code.UserCode}[/]");
            if (code.VerificationUriComplete is { } complete)
            {
                AnsiConsole.MarkupLine($"[grey]ou direto: {complete}[/]");
                TryOpenBrowser(complete);
            }

            var token = await flow.PollTokenAsync(url, code,
                s => AnsiConsole.MarkupLine($"[grey]{s}[/]"), ct);
            if (token is null)
            {
                AnsiConsole.MarkupLine("[red]device flow expirado/negado.[/]");
                return 1;
            }

            await store.SaveAsync(new CredentialStore.Credential
            {
                ServerUrl = url,
                AccessToken = token.AccessToken,
                RefreshToken = token.RefreshToken,
            }, ct);
            AnsiConsole.MarkupLine($"[green]✓[/] Login em [bold]{url}[/]");
            return 0;
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
            AnsiConsole.MarkupLine(
                "[grey]dica: openhandsnet login --api-key sk-…[/]");
            return 1;
        }
    }

    internal static string Mask(string key) =>
        key.Length > 7 ? $"sk-***{key[^4..]}" : "***";

    private static void TryOpenBrowser(string url)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch { /* headless */ }
    }
}