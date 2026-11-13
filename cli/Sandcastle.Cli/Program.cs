using Sandcastle.Cli.Commands;

using Spectre.Console.Cli;

const string StatusCommandName = "status";

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("sandcastle");
    config.SetApplicationVersion("0.1.0");
    config.AddCommand<LoginCommand>("login")
        .WithDescription("device flow (OIDC) ou --api-key para CI");
    config.AddCommand<LogoutCommand>("logout");
    config.AddCommand<CloudCommand>("cloud")
        .WithDescription("cria conversa remota: -t|-f [[--repo]] [[--watch]]");
    // SPEC-150: handoff local↔cloud.
    config.AddCommand<HandoffCommand>("handoff")
        .WithDescription("envia working tree p/ cloud: "
            + "[--message] [--include-untracked] [--dry-run] [--open]");
    config.AddCommand<PickupCommand>("pickup")
        .WithDescription("traz conversa p/ local: <id> "
            + "[--pr] [--force] [--yes] [--regenerate]");
    config.AddCommand<RunCommand>("run")
        .WithDescription("headless CI: --json --exit-on-finish --timeout-min");
    config.AddCommand<ServeCommand>("serve")
        .WithDescription("sobe Sandcastle.Api local");
    config.AddCommand<DoctorCommand>("doctor")
        .WithDescription("self-check: api/db/docker/llm/disk [--json|--preflight]");
    config.AddCommand<TuiCommand>("tui")
        .WithDescription("interactive TUI (default sem args)");
    // SPEC-31 RF-001: parity com opencode/claude-code/codex CLIs.
    config.AddCommand<ModelsCommand>("models")
        .WithDescription("catálogo OmniRoute: provider/model/window/price");
    config.AddCommand<StatsCommand>("stats")
        .WithDescription("uso agregado (conversas + org usage)");
    config.AddCommand<SkillsCommand>("skills")
        .WithDescription("tools efetivas da conversa [--all]");
    config.AddCommand<ExportCommand>("export")
        .WithDescription("dump JSONL de eventos [--out file]");
    config.AddCommand<AttachCommand>("attach")
        .WithDescription("TUI contra backend remoto: --url");
    config.AddBranch("eval", b =>
    {
        b.SetDescription("SPEC-39: harness de regressão — eval run|report|list");
        b.AddCommand<EvalRunCommand>("run")
            .WithDescription("roda suite/task: [--parallel N] [--mode live|recorded] [--gate]");
        b.AddCommand<EvalReportCommand>("report")
            .WithDescription("tabela/JSON de um dir de resultados [--baseline dir] [--gate]");
        b.AddCommand<EvalListCommand>("list")
            .WithDescription("lista + valida evals/tasks e evals/suites");
    });
    config.AddBranch("mcp", b =>
    {
        b.AddCommand<McpListCommand>("list");
        b.AddCommand<McpTestCommand>("test");
        b.AddCommand<McpServeCommand>("serve")
            .WithDescription("SPEC-124: sobe o servidor MCP stdio "
                + "(sandcastle-mcp) p/ agentes externos");
    });
    config.AddBranch("marketplace", b =>
    {
        b.SetDescription("SPEC-148: catálogo instalável — skills/"
            + "agents/mcp/hooks por escopo (user|org|system)");
        b.AddCommand<MarketplaceListCommand>("list")
            .WithDescription("catálogo [--kind] [-q] | --installed [--scope]");
        b.AddCommand<MarketplaceInstallCommand>("install")
            .WithDescription("<entry> [--scope] [--org] [--version] [--yes]");
        b.AddCommand<MarketplaceRemoveCommand>("remove")
            .WithDescription("<installed-id|entry-id> [--scope] [--org]");
        b.AddCommand<MarketplaceUpgradeCommand>("upgrade")
            .WithDescription("<installed-id|entry-id> [--scope] [--org]");
    });
    config.AddBranch("conversations", b =>
    {
        b.AddCommand<ConversationsListCommand>("list");
        b.AddCommand<ConversationsStatusCommand>(StatusCommandName);
        b.AddCommand<ConversationsDeleteCommand>("delete");
        b.AddCommand<ResumeCommand>("resume");
    });
    config.AddBranch("scm", b =>
    {
        b.AddCommand<ScmStatusCommand>(StatusCommandName);
        b.AddCommand<ScmConnectCommand>("connect");
        b.AddBranch("pr", pb => pb.AddCommand<ScmPrCreateCommand>("create"));
    });
    config.AddBranch("chat", b =>
    {
        b.AddCommand<ChatStatusCommand>(StatusCommandName);
        b.AddCommand<ChatTestCommand>("test");
    });
    config.AddBranch("tickets", b =>
    {
        b.AddCommand<TicketsStatusCommand>(StatusCommandName);
        b.AddCommand<TicketsTestCommand>("test");
        b.AddCommand<TicketsRunCommand>("run");
    });
    config.AddBranch("service-users", b =>
    {
        b.AddCommand<ServiceUsersListCommand>("list");
        b.AddCommand<ServiceUsersCreateCommand>("create");
        b.AddCommand<ServiceUsersKeyCommand>("key");
    });
});

return args.Length == 0
    ? await app.RunAsync(["tui"])
    : await app.RunAsync(args);