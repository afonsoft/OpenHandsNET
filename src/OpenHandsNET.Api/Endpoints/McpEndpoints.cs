using System.Text.Json;

using OpenHandsNET.Api.ProblemDetails;
using OpenHandsNET.Application.Mcp;
using OpenHandsNET.Domain.Mcp;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// <c>/api/v1/mcp</c> — SPEC-07 §5: servers CRUD + probe + tool-call direto +
/// OAuth authorize/callback/disconnect. Owner real chega com SPEC-08
/// (hoje single-tenant, owner <c>"local"</c>).
/// </summary>
public static class McpEndpoints
{
    private const string Owner = "local";

    /// <summary>Maps MCP endpoints.</summary>
    public static IEndpointRouteBuilder MapMcpEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/mcp/servers", ListAsync).WithTags("Mcp");
        app.MapPost("/api/v1/mcp/servers", SaveAsync).WithTags("Mcp");
        app.MapDelete("/api/v1/mcp/servers/{name}", DeleteAsync).WithTags("Mcp");
        app.MapPost("/api/v1/mcp/test", ProbeAsync).WithTags("Mcp");
        app.MapPost("/api/v1/mcp/tool-call", ToolCallAsync).WithTags("Mcp")
            .ProducesProblem(400).ProducesProblem(502);
        app.MapGet("/api/v1/mcp/oauth/authorize", AuthorizeAsync)
            .WithTags("Mcp");
        app.MapGet("/api/v1/mcp/oauth/callback", CallbackAsync).WithTags("Mcp");
        app.MapPost("/api/v1/mcp/oauth/disconnect", DisconnectAsync)
            .WithTags("Mcp");
        return app;
    }

    private static string StatusOf(McpServerConfig cfg, bool authed)
    {
        if (!cfg.Enabled)
        {
            return "disabled";
        }

        if (cfg.OAuthAuthorizeUrl is not null && !authed)
        {
            return "auth_required";
        }

        return "configured";
    }

    private sealed record ServerDto(
        McpServerConfig Config, string Status, bool Authenticated);

    private static async Task<IResult> ListAsync(
        IMcpSettingsService settings, IMcpOAuthService oauth,
        CancellationToken ct)
    {
        var list = new List<ServerDto>();
        foreach (var cfg in await settings.ListAsync(Owner, ct))
        {
            var authed = cfg.OAuthAuthorizeUrl is not null
                && await oauth.IsAuthenticatedAsync(Owner, cfg.Name, ct);
            list.Add(new ServerDto(cfg,
                Status: StatusOf(cfg, authed),
                Authenticated: authed));
        }

        return Results.Ok(list);
    }

    private static async Task<IResult> SaveAsync(
        McpServerConfig config, IMcpSettingsService settings,
        HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.Name))
        {
            return OpenHandsNetProblemFactory.Create(
                http, 400, "name é obrigatório");
        }

        await settings.SaveAsync(Owner, config, ct);
        return Results.Ok(config);
    }

    private static async Task<IResult> DeleteAsync(
        string name, IMcpSettingsService settings, CancellationToken ct) =>
        await settings.DeleteAsync(Owner, name, ct)
            ? Results.NoContent() : Results.NotFound();

    private sealed record ProbeRequest(McpServerConfig Server);

    private static async Task<IResult> ProbeAsync(
        ProbeRequest req, IMcpClientService mcp, CancellationToken ct) =>
        Results.Ok(await mcp.ProbeAsync(req.Server, ct));

    private sealed record ToolCallRequest(
        Guid ConversationId, string Server, string Tool,
        JsonElement Arguments);

    private static async Task<IResult> ToolCallAsync(
        ToolCallRequest req, IMcpClientService mcp, HttpContext http,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Server)
            || string.IsNullOrWhiteSpace(req.Tool))
        {
            return OpenHandsNetProblemFactory.Create(
                http, 400, "server and tool are required");
        }
        var result = await mcp.CallToolAsync(
            req.ConversationId, req.Server, req.Tool, req.Arguments, ct);
        if (!result.IsError)
        {
            return Results.Ok(JsonDocument.Parse(
                result.Observation.GetRawText()).RootElement);
        }
        var err = result.Observation.GetRawText();
        // Server desconhecido/não configurado = erro do cliente, não 502.
        // GetRawText devolve o JSON escapado ("n\u00E3o") — match em
        // substrings ASCII seguras.
        var clientError = err.Contains("configurado",
                              StringComparison.OrdinalIgnoreCase)
            || err.Contains("ausente", StringComparison.OrdinalIgnoreCase);
        return OpenHandsNetProblemFactory.Create(
            http, clientError ? 400 : 502, $"mcp tool call falhou: {err}");
    }

    private static async Task<IResult> AuthorizeAsync(
        string server, IMcpOAuthService oauth, HttpContext http,
        CancellationToken ct)
    {
        var callback = new Uri(
            $"{http.Request.Scheme}://{http.Request.Host}" +
            "/api/v1/mcp/oauth/callback");
        var url = await oauth.BuildAuthorizeUrlAsync(
            Owner, server, callback, ct);
        return url is null
            ? OpenHandsNetProblemFactory.Create(
                http, 400, $"servidor '{server}' sem OAuth configurado")
            : Results.Redirect(url.ToString());
    }

    private static async Task<IResult> CallbackAsync(
        string code, string state, IMcpOAuthService oauth, HttpContext http,
        CancellationToken ct)
    {
        try
        {
            var callback = new Uri(
                $"{http.Request.Scheme}://{http.Request.Host}" +
                "/api/v1/mcp/oauth/callback");
            var server = await oauth.HandleCallbackAsync(
                code, state, callback, ct);
            return Results.Ok(new { server, status = "authenticated" });
        }
        catch (InvalidOperationException ex)
        {
            return OpenHandsNetProblemFactory.Create(http, 400, ex.Message);
        }
    }

    private sealed record DisconnectRequest(string Server);

    private static async Task<IResult> DisconnectAsync(
        DisconnectRequest req, IMcpOAuthService oauth, CancellationToken ct)
    {
        await oauth.DisconnectAsync(Owner, req.Server, ct);
        return Results.NoContent();
    }
}