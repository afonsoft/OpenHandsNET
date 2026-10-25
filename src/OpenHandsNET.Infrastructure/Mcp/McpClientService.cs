using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.Mcp;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Domain.Mcp;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Mcp;

/// <summary>
/// Conexões MCP por conversa (SPEC-07 RF-002). Singleton — conexões podem
/// sobreviver ao request que as criou; serviços scoped resolvidos por chamada
/// via <see cref="IServiceScopeFactory"/>.
/// stdio → <c>docker exec -i</c> no container do sandbox (default) ou spawn no
/// host apenas com <c>Mcp:AllowHostMcp</c> (dev). Falha de um servidor degrada
/// o catálogo e emite <see cref="ServerErrorEvent"/> — nunca derruba a conversa.
/// </summary>
public sealed class McpClientService(
    IServiceScopeFactory scopes,
    IOptions<McpOptions> options,
    ILogger<McpClientService> logger) : IMcpClientService, IAsyncDisposable
{
    private sealed record ConnKey(Guid ConversationId, string Server);
    private sealed class Connection
    {
        public required McpClient Client { get; init; }
        public IList<McpClientTool>? Tools { get; set; }
    }

    private readonly ConcurrentDictionary<ConnKey, Connection> _conns = new();

    /// <inheritdoc />
    public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(
        Guid conversationId, CancellationToken ct = default)
    {
        var result = new List<McpToolDescriptor>();
        foreach (var cfg in await EffectiveConfigsAsync(conversationId, ct))
        {
            if (!cfg.Enabled)
            {
                continue;
            }

            try
            {
                var conn = await ConnectAsync(conversationId, cfg, ct);
                conn.Tools ??= await conn.Client.ListToolsAsync(
                    cancellationToken: ct);
                result.AddRange(conn.Tools.Select(t => new McpToolDescriptor(
                    cfg.Name, t.Name, McpToolCatalog.FullName(cfg.Name, t.Name),
                    t.Description, t.JsonSchema)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "MCP server {Server} falhou no catálogo da conversa {Conv}",
                    cfg.Name, conversationId);
                await ReportServerErrorAsync(
                    conversationId, cfg.Name, ex);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<ToolResult> CallToolAsync(
        Guid conversationId, string serverName, string toolName,
        JsonElement args, CancellationToken ct = default)
    {
        var cfg = (await EffectiveConfigsAsync(conversationId, ct))
            .FirstOrDefault(c => c.Name == serverName);
        if (cfg is null)
        {
            return ToolResult.Error($"mcp server '{serverName}' não configurado");
        }

        var callId = Guid.NewGuid().ToString("N");
        var fullName = McpToolCatalog.FullName(serverName, toolName);
        await AppendAsync(conversationId, new AcpToolCallEvent
        {
            ToolCallId = callId,
            Title = fullName,
            Status = "running",
            ToolKind = "execute",
            RawInput = args,
        }, ct);

        try
        {
            var conn = await ConnectAsync(conversationId, cfg, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(
                cfg.TimeoutSeconds > 0
                    ? cfg.TimeoutSeconds
                    : options.Value.DefaultCallTimeoutSeconds));
            var result = await conn.Client.CallToolAsync(
                toolName, ToArgsDict(args), cancellationToken: timeout.Token);

            var payload = JsonSerializer.SerializeToElement(new
            {
                tool = fullName,
                is_error = result.IsError ?? false,
                content = result.Content.Select(b => (object)b).ToArray(),
            }, JsonOpts);
            await AppendAsync(conversationId, new ObservationEvent
            {
                Observation = payload,
                ActionId = callId,
                ToolName = fullName,
                ToolCallId = callId,
            }, ct);
            return new ToolResult(payload, result.IsError ?? false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                tool = fullName,
                is_error = true,
                error = ex.Message,
            }, JsonOpts);
            await AppendAsync(conversationId, new ObservationEvent
            {
                Observation = payload,
                ActionId = callId,
                ToolName = fullName,
                ToolCallId = callId,
            }, CancellationToken.None);
            return new ToolResult(payload, IsError: true);
        }
    }

    /// <inheritdoc />
    public async Task<bool> RequiresConfirmationAsync(
        Guid conversationId, string serverName, CancellationToken ct = default) =>
        (await EffectiveConfigsAsync(conversationId, ct))
            .FirstOrDefault(c => c.Name == serverName)?.RequireConfirmation == true;

    /// <inheritdoc />
    public async Task<McpProbeResult> ProbeAsync(
        McpServerConfig config, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var client = await CreateClientAsync(
                config, conversationId: null, ct);
            var tools = await client.ListToolsAsync(cancellationToken: ct);
            return new McpProbeResult(true, tools.Count, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            return new McpProbeResult(
                false, 0, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(
        Guid conversationId, CancellationToken ct = default)
    {
        foreach (var key in _conns.Keys
            .Where(k => k.ConversationId == conversationId).ToList())
        {
            if (_conns.TryRemove(key, out var conn))
            {
                await conn.Client.DisposeAsync();
                // SPEC-25 RF-004: lifecycle state — a UI mostra badge/server.
                await EmitServerStateAsync(
                    conversationId, key.Server, "disconnected",
                    conn.Tools?.Count, null, CancellationToken.None);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var kv in _conns)
        {
            await kv.Value.Client.DisposeAsync();
        }

        _conns.Clear();
    }

    // ---------------------------------------------------------------- connect

    private async Task<Connection> ConnectAsync(
        Guid conversationId, McpServerConfig cfg, CancellationToken ct)
    {
        var key = new ConnKey(conversationId, cfg.Name);
        if (_conns.TryGetValue(key, out var cached))
        {
            return cached;
        }

        // SPEC-25 RF-004: state events mcp_servers (connecting → connected).
        await EmitServerStateAsync(
            conversationId, cfg.Name, "connecting", null, null, ct);
        var client = await CreateClientAsync(cfg, conversationId, ct);
        RegisterDiagnostics(client, cfg.Name, conversationId);
        var conn = _conns.GetOrAdd(key, new Connection { Client = client });
        await EmitServerStateAsync(
            conversationId, cfg.Name, "connected", null, null, ct);
        return conn;
    }

    /// <summary>
    /// SPEC-25 RF-004: MCP notifications → <c>agent_diagnostic</c>
    /// (<c>code="mcp_notification"</c>) — logging/progress/list-changed.
    /// </summary>
    private void RegisterDiagnostics(
        McpClient client, string server, Guid conversationId)
    {
        foreach (var method in new[]
        {
            "notifications/message", "notifications/progress",
            "notifications/tools/list_changed",
            "notifications/resources/list_changed",
        })
        {
            client.RegisterNotificationHandler(method,
                (notification, ct2) =>
                {
                    _ = EmitServerStateAsync(conversationId, server,
                        "notification", null,
                        $"{notification.Method}: "
                            + Truncate(
                                notification.Params?.ToString() ?? "", 400),
                        CancellationToken.None);
                    return ValueTask.CompletedTask;
                });
        }
    }

    private async Task EmitServerStateAsync(
        Guid conversationId, string server, string state,
        int? tools, string? error, CancellationToken ct)
    {
        try
        {
            if (state is "notification")
            {
                await AppendAsync(conversationId,
                    new AgentDiagnosticEvent
                    {
                        Severity = "info",
                        Code = "mcp_notification",
                        Message = $"mcp '{server}': {error}",
                    }, ct);
                return;
            }

            await AppendAsync(conversationId,
                new ConversationStateUpdateEvent
                {
                    Key = "mcp_servers",
                    Value = JsonSerializer.SerializeToElement(new
                    {
                        server,
                        state,
                        tools,
                        error,
                    }, JsonOpts),
                }, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "mcp_servers state emit falhou ({Server})",
                server);
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private async Task<McpClient> CreateClientAsync(
        McpServerConfig cfg, Guid? conversationId, CancellationToken ct)
    {
        IClientTransport transport;
        if (cfg.Transport == McpTransport.Stdio)
        {
            var (cmd, args) = await ResolveStdioAsync(
                cfg, conversationId, ct);
            transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = cfg.Name,
                Command = cmd,
                Arguments = args,
                EnvironmentVariables = cfg.Env?
                    .ToDictionary(kv => kv.Key, kv => (string?)kv.Value) // NOSONAR S1905 — cast exigido pelo target IDictionary<string,string?>,
            });
        }
        else
        {
            if (cfg.Url is null)
            {
                throw new InvalidOperationException(
                    $"mcp server '{cfg.Name}': url ausente p/ transporte http/sse");
            }

            var headers = new Dictionary<string, string>(
                cfg.Headers ?? new Dictionary<string, string>());
            if (!headers.ContainsKey("Authorization") && conversationId is not null)
            {
                await using var scope = scopes.CreateAsyncScope();
                var oauth = scope.ServiceProvider
                    .GetService<IMcpOAuthService>();
                var token = oauth is null ? null
                    : await oauth.GetAccessTokenAsync("local", cfg.Name, ct);
                if (token is not null)
                {
                    headers["Authorization"] = $"Bearer {token}";
                }
            }

            transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = cfg.Url,
                TransportMode = cfg.Transport == McpTransport.Sse
                    ? HttpTransportMode.Sse : HttpTransportMode.StreamableHttp,
                AdditionalHeaders = headers,
            });
        }

        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    /// <summary>
    /// stdio: dentro do sandbox via <c>docker exec -i</c> quando a conversa tem
    /// container; senão spawn no host — apenas com <c>Mcp:AllowHostMcp</c>.
    /// </summary>
    private async Task<(string cmd, IList<string> args)> ResolveStdioAsync(
        McpServerConfig cfg, Guid? conversationId, CancellationToken ct)
    {
        if (cfg.Command is null)
        {
            throw new InvalidOperationException(
                $"mcp server '{cfg.Name}': command ausente p/ stdio");
        }

        if (conversationId is not null)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var sandboxes = scope.ServiceProvider
                .GetRequiredService<ISandboxService>();
            var conv = await db.Conversations
                .FirstOrDefaultAsync(c => c.Id == conversationId, ct);
            if (conv?.SandboxId is { } sid)
            {
                var sandbox = await sandboxes.GetAsync(sid, ct);
                if (sandbox?.ContainerId is { } container)
                {
                    return ("docker",
                        (IList<string>)["exec", "-i", container, cfg.Command,
                            .. cfg.Args ?? []]);
                }
            }
        }

        if (!options.Value.AllowHostMcp)
        {
            throw new InvalidOperationException(
                $"mcp server '{cfg.Name}': stdio no host bloqueado " +
                "(Mcp:AllowHostMcp=false) e conversa sem sandbox");
        }

        return (cfg.Command, (IList<string>)(cfg.Args?.ToList() ?? []));
    }

    // ------------------------------------------------------------ persistence

    private async Task<IReadOnlyList<McpServerConfig>> EffectiveConfigsAsync(
        Guid conversationId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = scope.ServiceProvider
            .GetRequiredService<IMcpSettingsService>();
        var conv = await db.Conversations
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        return await settings.EffectiveForAsync(
            "local", conv?.McpConfigJson, ct);
    }

    private async Task AppendAsync(
        Guid conversationId, EventBase evt, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<
            Application.Events.IEventService>()
            .AppendAsync(conversationId, evt, ct);
    }

    private async Task ReportServerErrorAsync(
        Guid conversationId, string server, Exception ex)
    {
        try
        {
            await AppendAsync(conversationId, new ServerErrorEvent
            {
                Code = "mcp_server_error",
                Detail = $"servidor '{server}': {ex.Message}",
            }, CancellationToken.None);
            await EmitServerStateAsync(conversationId, server, "failed",
                null, ex.Message, CancellationToken.None);
        }
        catch (Exception inner)
        {
            logger.LogDebug(inner, "falha reportando erro MCP {Server}", server);
        }
    }

    private static IReadOnlyDictionary<string, object?> ToArgsDict(
        JsonElement args) => args.ValueKind == JsonValueKind.Object
        ? args.EnumerateObject().ToDictionary(
            p => p.Name, p => (object?)p.Value)
        : new Dictionary<string, object?>();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}