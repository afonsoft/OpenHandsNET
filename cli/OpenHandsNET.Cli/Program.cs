using Spectre.Console;

// SPEC-06 owns the full CLI surface (device flow login, conversations, TUI).
// This skeleton only provides the entry point and branding.
AnsiConsole.MarkupLine("[bold]OpenHandsNET[/] CLI");
AnsiConsole.WriteLine("Full CLI arrives with SPEC-06. Server: " +
    (Environment.GetEnvironmentVariable("OPENHANDSNET_SERVER") ?? "http://localhost:8080"));
return 0;
