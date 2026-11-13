using System.Reflection;
using System.Text.Json;

using Sandcastle.Cli.Services;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Sandcastle.Cli.Commands;

/// <summary>
/// <c>sandcastle handoff</c> — SPEC-150 RF-001: empacota o working tree
/// local (diff vs base) e cria uma conversa cloud que continua o
/// trabalho (clone do base_sha + <c>git apply --3way</c> no sandbox).
/// </summary>
public sealed class HandoffCommand : AsyncCommand<HandoffCommand.Settings>
{
    /// <summary>Settings do comando.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>Mensagem/prompt inicial da conversa.</summary>
        [CommandOption("-m|--message <TEXT>")]
        public string? Message { get; init; }

        /// <summary>Inclui arquivos untracked (exceto .gitignore+secrets).</summary>
        [CommandOption("--include-untracked")]
        public bool IncludeUntracked { get; init; }

        /// <summary>Só mostra o que seria enviado — não chama a API.</summary>
        [CommandOption("--dry-run")]
        public bool DryRun { get; init; }

        /// <summary>Abre a conversa no browser ao READY.</summary>
        [CommandOption("--open")]
        public bool Open { get; init; }

        /// <summary>Saída JSON para scripting.</summary>
        [CommandOption("--json")]
        public bool Json { get; init; }

        /// <summary>Servidor (--server-url &gt; env &gt; store &gt; default).</summary>
        [CommandOption("--server-url <URL>")]
        public string? ServerUrl { get; init; }
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings,
        CancellationToken cancellationToken) =>
        await RunAsync(settings, new GitRunner(),
            CloudApiClient.CreateAsync, cancellationToken);

    /// <summary>Núcleo testável — git e factory do client injetados.</summary>
    internal static async Task<int> RunAsync(
        Settings settings, IGitRunner git,
        Func<string?, CredentialStore, HttpClient?, CancellationToken,
            Task<CloudApiClient>> clientFactory,
        CancellationToken ct)
    {
        HandoffCollect.Result collect;
        try
        {
            collect = await new HandoffCollect(git)
                .CollectAsync(settings.IncludeUntracked, ct);
        }
        catch (HandoffCollect.CollectException ex)
        {
            Write(settings, $"[red]{ex.Message}[/]");
            return 1;
        }

        if (collect.Files == 0 && collect.BinaryFiles.Count == 0)
        {
            Write(settings,
                "[yellow]nada para handoff — working tree limpo vs base[/]");
            return 0;
        }

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                repository = collect.Repository,
                base_sha = collect.BaseSha,
                base_branch = collect.BaseBranch,
                files = collect.Files,
                additions = collect.Additions,
                deletions = collect.Deletions,
                binary_files = collect.BinaryFiles,
                secrets_skipped = collect.SecretsSkipped,
                untracked_included = collect.UntrackedIncluded,
                patch_bytes = collect.PatchBytes,
                dry_run = settings.DryRun,
            }));
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[bold]handoff[/] {collect.Repository} @ "
                + $"{collect.BaseSha[..Math.Min(7, collect.BaseSha.Length)]}"
                + (collect.BaseBranch is { } b ? $" ({b})" : ""));
            AnsiConsole.MarkupLine(
                $"  {collect.Files} files, +{collect.Additions} "
                + $"-{collect.Deletions} ({collect.PatchBytes / 1024} KB)");
            foreach (var bf in collect.BinaryFiles)
            {
                AnsiConsole.MarkupLine(
                    $"  [yellow]binary (placeholder): {bf}[/]");
            }

            foreach (var s in collect.SecretsSkipped)
            {
                AnsiConsole.MarkupLine(
                    $"  [red]secret blocklist (skipped): {s}[/]");
            }
        }

        if (settings.DryRun)
        {
            return 0; // stat preview only — nunca chama a API
        }

        CloudApiClient api;
        try
        {
            api = await clientFactory(
                settings.ServerUrl, new CredentialStore(), null, ct);
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "dev";
            var task = await api.CreateHandoffAsync(
                collect.Repository, collect.BaseSha, collect.BaseBranch,
                collect.Patch, settings.Message, new
                {
                    cli_version = version,
                    files = collect.Files,
                    additions = collect.Additions,
                    deletions = collect.Deletions,
                    binary_files = collect.BinaryFiles,
                    secrets_skipped = collect.SecretsSkipped,
                    untracked_included = collect.UntrackedIncluded,
                }, ct);
            var taskId = task.GetProperty("id").GetString()!;
            var (convId, error) = await AnsiConsole.Status().StartAsync(
                "provisioning cloud sandbox…", ctx =>
                    new RunExecutor(api).WaitReadyAsync(
                        taskId, s => ctx.Status($"start-task: {s}"), ct));
            if (error is not null || convId is null)
            {
                Write(settings,
                    $"[red]handoff failed: {error ?? "no conversation"}[/]");
                return 1;
            }

            var url = $"{api.BaseUrl}/conversations/{convId}";
            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    conversation_id = convId,
                    url,
                }));
            }
            else
            {
                AnsiConsole.MarkupLine($"[green]✓[/] {url}");
            }

            if (settings.Open)
            {
                OpenBrowser(url);
            }

            return 0;
        }
        catch (HttpRequestException ex)
        {
            Write(settings, $"[red]handoff request failed: {ex.Message}[/]");
            return 1;
        }
    }

    private static void Write(Settings s, string markup)
    {
        if (!s.Json)
        {
            AnsiConsole.MarkupLine(markup);
        }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url)
                { UseShellExecute = true });
        }
        catch (Exception)
        {
            AnsiConsole.MarkupLine($"[dim]open: {url}[/]");
        }
    }
}