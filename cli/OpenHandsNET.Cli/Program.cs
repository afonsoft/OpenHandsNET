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
    });
    config.AddBranch("conversations", b =>
    {
        b.AddCommand<ConversationsListCommand>("list");
        b.AddCommand<ConversationsStatusCommand>("status");
        b.AddCommand<ConversationsDeleteCommand>("delete");
        b.AddCommand<ResumeCommand>("resume");
    });
    config.AddBranch("scm", b =>
    {
        b.AddCommand<ScmStatusCommand>("status");
        b.AddCommand<ScmConnectCommand>("connect");
        b.AddBranch("pr", pb => pb.AddCommand<ScmPrCreateCommand>("create"));
    });
    config.AddBranch("chat", b =>
    {
        b.AddCommand<ChatStatusCommand>("status");
        b.AddCommand<ChatTestCommand>("test");
    });
    config.AddBranch("tickets", b =>
    {
        b.AddCommand<TicketsStatusCommand>("status");
        b.AddCommand<TicketsTestCommand>("test");
        b.AddCommand<TicketsRunCommand>("run");
    });
});

return args.Length == 0
    ? await app.RunAsync(["tui"])
    : await app.RunAsync(args);
