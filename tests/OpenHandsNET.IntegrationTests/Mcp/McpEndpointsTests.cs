using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

using Shouldly;

namespace OpenHandsNET.IntegrationTests.Mcp;

/// <summary>
/// SPEC-07 API contract: servers CRUD, /mcp/test probe (stdio real via
/// AllowHostMcp no teste), tool-call auditado e fluxo OAuth + PKCE.
/// Um mini-Kestrel faz de MCP-HTTP server + IdP fake em socket real.
/// </summary>
public sealed class McpEndpointsTests : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-mcp-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;
    private readonly WebApplication _fake;
    private readonly string _fakeBase;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public McpEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Database:Provider", "Sqlite");
            b.UseSetting("Database:ConnectionString", $"Data Source={_dbPath}");
            b.UseSetting("Observability:OtlpEndpoint", string.Empty);
            b.UseSetting("Sandbox:DefaultImage", "bash:5");
            b.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}");
            // Dev-only no teste: probe stdio no host (SPEC-07 §10 permite
            // via flag explícita; produção usa docker exec no sandbox).
            b.UseSetting("Mcp:AllowHostMcp", "true");
        });
        _http = TestAuth.Login(_factory, "mcp@local",
            new WebApplicationFactoryClientOptions
            {
                // OAuth authorize devolve 302 — o teste conduz o fluxo manualmente.
                AllowAutoRedirect = false,
            });
        (_fake, _fakeBase) = StartFakeServer();
    }

    /// <summary>Mini-Kestrel: MCP-HTTP (JSON-RPC) + IdP OAuth fake.</summary>
    private static (WebApplication app, string baseUrl) StartFakeServer()
    {
        var b = WebApplication.CreateBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Logging.ClearProviders();
        var app = b.Build();

        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            var req = await ctx.Request
                .ReadFromJsonAsync<JsonElement>();
            var method = req.GetProperty("method").GetString();
            // Notifications não carregam id → 202 sem body (spec MCP).
            if (!req.TryGetProperty("id", out var id) || method is null)
            {
                ctx.Response.StatusCode = 202;
                return;
            }
            object? result = method switch
            {
                "initialize" => new
                {
                    protocolVersion = req.GetProperty("params")
                        .GetProperty("protocolVersion").GetString(),
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "fake-http", version = "0.1" },
                },
                "tools/list" => new
                {
                    tools = new[]
                    {
                        new
                        {
                            name = "echo",
                            description = "echo back",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    text = new { type = "string" },
                                },
                            },
                        },
                    },
                },
                "tools/call" => new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = req.GetProperty("params")
                                .GetProperty("arguments")
                                .GetProperty("text").GetString(),
                        },
                    },
                    isError = false,
                },
                "ping" => new { },
                _ => null,
            };
            ctx.Response.ContentType = "application/json";
            if (result is null)
            {
                await ctx.Response.WriteAsJsonAsync(new
                {
                    jsonrpc = "2.0",
                    id,
                    error = new { code = -32601, message = "not found" },
                });
                return;
            }

            await ctx.Response.WriteAsJsonAsync(new
            {
                jsonrpc = "2.0",
                id,
                result,
            });
        });

        // IdP fake: token endpoint retorna access+refresh.
        app.MapPost("/idp/token", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(new
            {
                access_token = "fake-access-"
                    + form["grant_type"].ToString(),
                refresh_token = "fake-refresh-1",
                expires_in = 3600,
                token_type = "Bearer",
            });
        });
        app.MapGet("/idp/authorize", (HttpContext ctx) =>
        {
            // IdP real redirecionaria de volta com code+state.
            var state = ctx.Request.Query["state"].ToString();
            var redirect = ctx.Request.Query["redirect_uri"].ToString();
            ctx.Response.Redirect($"{redirect}?code=fake-code&state={state}");
        });

        app.StartAsync().GetAwaiter().GetResult();
        return (app, app.Urls.Single());
    }

    private object HttpServerConfig(string name, bool oauth = false) => new
    {
        name,
        transport = "http",
        url = $"{_fakeBase}/mcp",
        oauth_authorize_url = oauth ? $"{_fakeBase}/idp/authorize" : null,
        oauth_token_url = oauth ? $"{_fakeBase}/idp/token" : null,
        oauth_client_id = oauth ? "fake-client" : null,
    };

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _fake.DisposeAsync();
        await _factory.DisposeAsync();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task Servers_Crud_RoundTrip()
    {
        var save = await _http.PostAsJsonAsync(
            "/api/v1/mcp/servers", HttpServerConfig("http-echo"), Json);
        save.StatusCode.ShouldBe(HttpStatusCode.OK);

        var list = await _http.GetFromJsonAsync<JsonElement[]>(
            "/api/v1/mcp/servers");
        list!.ShouldContain(s =>
            s.GetProperty("config").GetProperty("name").GetString()
                == "http-echo");

        (await _http.DeleteAsync("/api/v1/mcp/servers/http-echo"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _http.DeleteAsync("/api/v1/mcp/servers/http-echo"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Probe_Http_Server_Reports_Tools_Count_And_Latency()
    {
        var resp = await _http.PostAsJsonAsync("/api/v1/mcp/test", new
        {
            server = HttpServerConfig("probe-http"),
        }, Json);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var r = await resp.Content.ReadFromJsonAsync<JsonElement>();
        r.GetProperty("ok").GetBoolean().ShouldBeTrue(
            $"probe falhou: {r.GetRawText()}");
        r.GetProperty("tools_count").GetInt32().ShouldBe(1);
        r.GetProperty("latency_ms").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Probe_Stdio_Server_Via_Host_Flag()
    {
        var script = Path.Combine(AppContext.BaseDirectory,
            "Mcp", "fake_mcp_stdio.py");
        var resp = await _http.PostAsJsonAsync("/api/v1/mcp/test", new
        {
            server = new
            {
                name = "stdio-echo",
                transport = "stdio",
                command = "python3",
                args = new[] { script },
            },
        }, Json);
        var r = await resp.Content.ReadFromJsonAsync<JsonElement>();
        r.GetProperty("ok").GetBoolean().ShouldBeTrue(
            $"probe falhou: {r.GetRawText()}");
        r.GetProperty("tools_count").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Probe_Broken_Server_Returns_Ok_False()
    {
        var resp = await _http.PostAsJsonAsync("/api/v1/mcp/test", new
        {
            server = new
            {
                name = "broken",
                transport = "http",
                url = "http://127.0.0.1:1/mcp",
            },
        }, Json);
        var r = await resp.Content.ReadFromJsonAsync<JsonElement>();
        r.GetProperty("ok").GetBoolean().ShouldBeFalse();
        r.GetProperty("error").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Oauth_Authorize_Callback_Persists_Token()
    {
        await _http.PostAsJsonAsync(
            "/api/v1/mcp/servers", HttpServerConfig("oauth-mcp", oauth: true), Json);

        // 1) authorize → 302 p/ IdP com state+PKCE.
        var auth = await _http.GetAsync(
            "/api/v1/mcp/oauth/authorize?server=oauth-mcp");
        auth.StatusCode.ShouldBe(HttpStatusCode.Redirect,
            $"authorize → {(int)auth.StatusCode}: "
                + await auth.Content.ReadAsStringAsync());
        var loc = new Uri(auth.Headers.Location!.ToString());
        loc.AbsolutePath.ShouldBe("/idp/authorize");
        var q = System.Web.HttpUtility.ParseQueryString(loc.Query);
        q["code_challenge"].ShouldNotBeNullOrEmpty();
        q["code_challenge_method"].ShouldBe("S256");
        var state = q["state"]!;

        // 2) IdP redirecta de volta → callback troca code por token.
        var cb = await _http.GetAsync(
            $"/api/v1/mcp/oauth/callback?code=fake-code&state={state}");
        cb.StatusCode.ShouldBe(HttpStatusCode.OK);

        // 3) servidor agora autenticado.
        var list = await _http.GetFromJsonAsync<JsonElement[]>(
            "/api/v1/mcp/servers");
        var srv = list!.Single(s => s.GetProperty("config")
            .GetProperty("name").GetString() == "oauth-mcp");
        srv.GetProperty("authenticated").GetBoolean().ShouldBeTrue();

        // 4) disconnect remove.
        (await _http.PostAsJsonAsync("/api/v1/mcp/oauth/disconnect",
            new { server = "oauth-mcp" })).StatusCode
                .ShouldBe(HttpStatusCode.NoContent);
        list = await _http.GetFromJsonAsync<JsonElement[]>(
            "/api/v1/mcp/servers");
        list!.Single(s => s.GetProperty("config")
            .GetProperty("name").GetString() == "oauth-mcp")
            .GetProperty("status").GetString().ShouldBe("auth_required");
    }

    [Fact]
    public async Task ToolCall_Audits_Acp_Plus_Observation_Events()
    {
        // Servidor HTTP fake registrado.
        await _http.PostAsJsonAsync(
            "/api/v1/mcp/servers", HttpServerConfig("call-http"), Json);

        // Conversa real via start-task (aguarda READY = sandbox running).
        var create = await _http.PostAsJsonAsync(
            "/api/v1/app-conversations", new
            {
                initial_message = new
                {
                    role = "user",
                    content = new[] { new { type = "text", text = "oi" } },
                    run = false,
                },
            });
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var task = await create.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = task.GetProperty("id").GetString()!;

        string? convId = null;
        for (var i = 0; i < 60 && convId is null; i++)
        {
            await Task.Delay(1500);
            var t = await _http.GetFromJsonAsync<JsonElement>(
                $"/api/v1/app-conversations/start-tasks/{taskId}");
            var st = t.GetProperty("status").GetString();
            if (st == "READY")
            {
                convId = t.GetProperty("app_conversation_id").GetString();
            }
            else if (st == "ERROR")
            {
                throw new InvalidOperationException(
                    $"start-task ERROR: {t.GetRawText()}");
            }
        }

        convId.ShouldNotBeNull("start-task não chegou a READY");

        // Tool call direto → echo via MCP-HTTP fake.
        var call = await _http.PostAsJsonAsync("/api/v1/mcp/tool-call", new
        {
            conversation_id = convId,
            server = "call-http",
            tool = "echo",
            arguments = new { text = "hello-mcp" },
        });
        call.StatusCode.ShouldBe(HttpStatusCode.OK,
            await call.Content.ReadAsStringAsync());
        var payload = await call.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("tool").GetString()
            .ShouldBe("mcp__call-http__echo");
        payload.GetProperty("is_error").GetBoolean().ShouldBeFalse();

        // Auditoria: AcpToolCallEvent + ObservationEvent no EventStream.
        var evts = await _http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/conversation/{convId}/events/search?limit=100");
        var kinds = evts.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("kind").GetString()).ToList();
        kinds.ShouldContain("ACPToolCallEvent");
        kinds.ShouldContain("ObservationEvent");
        var obs = evts.GetProperty("items").EnumerateArray()
            .First(e => e.GetProperty("kind").GetString()
                == "ObservationEvent");
        obs.GetProperty("observation").GetRawText()
            .ShouldContain("hello-mcp");
    }

    [Fact]
    public async Task Oauth_Callback_With_Bad_State_Returns_400()
    {
        var cb = await _http.GetAsync(
            "/api/v1/mcp/oauth/callback?code=x&state=bogus");
        cb.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}