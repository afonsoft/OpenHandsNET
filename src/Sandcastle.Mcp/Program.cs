using Sandcastle.Mcp;

// Sandcastle.Mcp — servidor MCP stdio (SPEC-124 RF-001) + transporte
// Streamable HTTP opcional (SPEC-145 RF-004, off por default).
// O cliente MCP (Claude Code, OpenCode, etc.) spawna este processo e
// fala JSON-RPC 2.0 newline-delimited no stdin/stdout. Config:
//   SC_API_URL — base do app (default http://localhost:8080)
//   SC_API_KEY — chave sk-* do usuário (tools falham fechado sem ela)
//   SC_MCP_IP_ALLOWLIST — CIDRs do modo HTTP (vazio = loopback)
//   --http :PORT      — sobe o endpoint POST /mcp (bearer sk-* por
//                       request) em vez do loop stdio.
// Logs nunca vão pro stdout (é protocolo) — erros vão pro stderr.

// S1075 won't-fix: default local documentado, sobrescrito por SC_API_URL.
const string DefaultBaseUrl = "http://localhost:8080"; // NOSONAR
var baseUrl = Environment.GetEnvironmentVariable("SC_API_URL")
    ?? DefaultBaseUrl;
var apiKey = Environment.GetEnvironmentVariable("SC_API_KEY");

// SPEC-145: `--http :PORT` / `--http PORT` — modo HTTP (sem stdio).
var httpPort = ParseHttpPort(args);
if (httpPort is { } port)
{
    var allowlist = Environment.GetEnvironmentVariable(
        "SC_MCP_IP_ALLOWLIST");
    using var mcpHttp = new McpHttpServer(port, baseUrl, allowlist);
    await Console.Error.WriteLineAsync(
        $"sandcastle-mcp http listening on :{port} (/mcp)");
    await mcpHttp.RunAsync(CancellationToken.None);
    return 0;
}

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

static int? ParseHttpPort(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--http" && i + 1 < args.Length)
        {
            var v = args[i + 1].TrimStart(':');
            if (int.TryParse(v, out var p) && p is > 0 and < 65536)
            {
                return p;
            }
            if (args[i + 1] == "*" || args[i + 1].Length == 0)
            {
                return 8081; // `--http *` sem porta — default dev.
            }
        }
        if (args[i].StartsWith("--http:", StringComparison.Ordinal)
            && int.TryParse(args[i]["--http:".Length..], out var p2)
            && p2 is > 0 and < 65536)
        {
            return p2;
        }
    }
    return null;
}