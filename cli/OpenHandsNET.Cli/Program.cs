using Spectre.Console.Cli;
using OpenHandsNET.Cli.Commands;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("openhandsnet");
    config.SetApplicationVersion("0.1.0");
    config.AddCommand<LoginCommand>("login")
        .WithDescription("device flow (OIDC) ou --api-key para CI");
    config.AddCommand<LogoutCommand>("logout");
    config.AddCommand<CloudCommand>("cloud")
        .WithDescription("cria conversa remota: -t|-f [[--repo]] [[--watch]]");
    config.AddCommand<RunCommand>("run")
        .WithDescription("headless CI: --json --exit-on-finish --timeout-min");
    config.AddCommand<ServeCommand>("serve")
        .WithDescription("sobe OpenHandsNET.Api local");
    config.AddCommand<TuiCommand>("tui").IsHidden()
        .WithDescription("interactive TUI (default sem args)");
    config.AddBranch("conversations", b =>
    {
        b.AddCommand<ConversationsListCommand>("list");
        b.AddCommand<ConversationsStatusCommand>("status");
        b.AddCommand<ConversationsDeleteCommand>("delete");
    });
});

return args.Length == 0
    ? await app.RunAsync(["tui"])
    : await app.RunAsync(args);
