using System.Text.Json;

using Sandcastle.Cli.Services;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Sandcastle.Cli.Commands;

/// <summary>
/// <c>sandcastle pickup &lt;conversation-id|pr-number&gt;</c> — SPEC-150
/// RF-002: baixa o handoff-package (diff do sandbox vs base) e aplica no
/// working tree local (<c>git apply --3way</c>; conflito →
/// <c>--reject</c> + hunks listados + exit 2).
/// </summary>
public sealed class PickupCommand : AsyncCommand<PickupCommand.Settings>
{
    /// <summary>Settings do comando.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>Conversation id (guid) ou número de PR com --pr.</summary>
        [CommandArgument(0, "<CONVERSATION>")]
        public required string Conversation { get; init; }

        /// <summary>Argumento é número de PR — resolve a conversa dona.</summary>
        [CommandOption("--pr")]
        public bool Pr { get; init; }

        /// <summary>Auto-stash de mudanças locais sujas antes do apply.</summary>
        [CommandOption("--force")]
        public bool Force { get; init; }

        /// <summary>Não pede confirmação antes do apply.</summary>
        [CommandOption("--yes")]
        public bool Yes { get; init; }

        /// <summary>Força regeneração do package (ignora cache TTL).</summary>
        [CommandOption("--regenerate")]
        public bool Regenerate { get; init; }

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
        var api = await clientFactory(
            settings.ServerUrl, new CredentialStore(), null, ct);

        var convId = settings.Conversation;
        if (settings.Pr)
        {
            if (!int.TryParse(convId, out var pr))
            {
                Write(settings, "[red]--pr exige número: pickup --pr 123[/]");
                return 1;
            }

            convId = await api.FindConversationByPrAsync(pr, ct);
            if (convId is null)
            {
                Write(settings,
                    $"[red]nenhuma conversa dona do PR #{pr}[/]");
                return 1;
            }
        }

        var (status, package) = await api.GetHandoffPackageAsync(
            convId, settings.Regenerate, ct);
        if (status != 200)
        {
            var detail = package.ValueKind == JsonValueKind.Object
                && package.TryGetProperty("detail", out var d)
                ? d.GetString() : $"HTTP {status}";
            Write(settings, $"[red]handoff-package falhou: {detail}[/]");
            return 1;
        }

        var patch = package.GetProperty("diff_patch").GetString() ?? "";
        var headBranch = package.TryGetProperty("head_branch", out var hb)
            ? hb.GetString() : null;
        var summary = package.TryGetProperty("summary", out var su)
            ? su.GetString() : null;
        var binaries = package.TryGetProperty("binary_files", out var bf)
            && bf.ValueKind == JsonValueKind.Array
            ? bf.EnumerateArray().Select(e => e.GetString() ?? "")
                .Where(s => s.Length > 0).ToList()
            : [];

        // cwd limpo ou --force (auto-stash) — nunca sobrescreve dirty.
        var dirty = await git.RunAsync(
            "status --porcelain", ct);
        if (dirty.StdOut.Trim().Length > 0)
        {
            if (!settings.Force)
            {
                Write(settings,
                    "[red]working tree sujo — commite/stashe ou rode "
                    + "com --force (auto-stash)[/]");
                return 1;
            }

            var stash = await git.RunAsync(
                "stash push -u -m sandcastle-pickup", ct);
            if (stash.ExitCode != 0)
            {
                Write(settings,
                    $"[red]git stash falhou: {stash.StdErr.Trim()}[/]");
                return 1;
            }
        }

        var (files, adds, dels) = HandoffDiffTool.Stat(patch);
        var (stripped, packageBinaries) = HandoffDiffTool.StripBinary(patch);
        var allBinaries = binaries.Concat(packageBinaries)
            .Distinct().ToList();
        if (!settings.Json)
        {
            AnsiConsole.MarkupLine(
                $"[bold]pickup[/] {convId} — {files} files, "
                + $"+{adds} -{dels}");
            foreach (var b in allBinaries)
            {
                AnsiConsole.MarkupLine(
                    $"  [yellow]binary (skipped): {b}[/]");
            }

            if (summary is { Length: > 0 } sum)
            {
                AnsiConsole.MarkupLine($"[dim]agent summary:[/] {sum}");
            }
        }

        if (!settings.Yes
            && !AnsiConsole.Confirm(
                "Apply the package to the local working tree?", false))
        {
            return 1;
        }

        // branch: PR branch quando --pr, senão handoff/{short-id}.
        var shortId = convId.Length > 8 ? convId[..8] : convId;
        var branch = settings.Pr && headBranch is { Length: > 0 }
            ? headBranch : $"handoff/{shortId}";
        var checkout = await git.RunAsync($"checkout -B {branch}", ct);
        if (checkout.ExitCode != 0)
        {
            Write(settings,
                $"[red]git checkout -B {branch} falhou: "
                + $"{checkout.StdErr.Trim()}[/]");
            return 1;
        }

        var applied = await git.RunWithInputAsync(
            "apply --3way", stripped, ct);
        if (applied.ExitCode == 0)
        {
            Emit(settings, convId, branch, true, []);
            return 0;
        }

        // conflito → --reject: hunks falhados viram *.rej listados (exit 2).
        await git.RunAsync("reset --hard", ct);
        var rejected = await git.RunWithInputAsync(
            "apply --reject", stripped, ct);
        var rejs = await git.RunAsync(
            "ls-files --others --exclude-standard -- '*.rej'", ct);
        var failed = HandoffDiffTool.ParseRejects(rejs.StdOut);
        if (failed.Count > 0 || rejected.ExitCode != 0)
        {
            foreach (var f in failed)
            {
                Write(settings, $"  [red]rejected: {f}[/]");
            }

            Write(settings,
                $"[red]apply com conflitos — revise os .rej "
                + $"({failed.Count} arquivo(s))[/]");
            Emit(settings, convId, branch, false, failed);
            return 2;
        }

        Emit(settings, convId, branch, true, []);
        return 0;
    }

    private static void Emit(
        Settings s, string convId, string branch, bool applied,
        IReadOnlyList<string> rejected)
    {
        if (s.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                conversation_id = convId,
                branch,
                applied,
                rejected_files = rejected,
            }));
        }
        else if (applied)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] applied on {branch}");
        }
    }

    private static void Write(Settings s, string markup)
    {
        if (!s.Json)
        {
            AnsiConsole.MarkupLine(markup);
        }
    }
}