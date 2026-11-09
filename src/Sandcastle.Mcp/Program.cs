using Sandcastle.Mcp;

// Sandcastle.Mcp — servidor MCP stdio (SPEC-124 RF-001).
// O cliente MCP (Claude Code, OpenCode, etc.) spawna este processo e
// fala JSON-RPC 2.0 newline-delimited no stdin/stdout. Config:
//   SC_API_URL — base do app (default http://localhost:8080)
//   SC_API_KEY — chave sk-* do usuário (tools falham fechado sem ela)
// Logs nunca vão pro stdout (é protocolo) — erros vão pro stderr.

// S1075 won't-fix: default local documentado, sobrescrito por SC_API_URL.
const string DefaultBaseUrl = "http://localhost:8080"; // NOSONAR
var baseUrl = Environment.GetEnvironmentVariable("SC_API_URL")
    ?? DefaultBaseUrl;
var apiKey = Environment.GetEnvironmentVariable("SC_API_KEY");

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
var api = new ScApiClient(http, baseUrl, apiKey);
var server = new McpServer();

using var reader = Console.In;
await using var writer = Console.Out;

while (await reader.ReadLineAsync() is { } line)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    var response = await server.HandleLineAsync(
        line, api, CancellationToken.None);
    if (response is not null)
    {
        await writer.WriteLineAsync(response);
        await writer.FlushAsync();
    }
}

return 0;