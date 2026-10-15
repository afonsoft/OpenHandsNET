using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Sandboxing;

namespace OpenHandsNET.UnitTests.SandboxOps;

/// <summary>SPEC-35: remote engine HTTP contract + provider selection.</summary>
public sealed class HttpSandboxEngineTests
{
    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(respond(request));
        }
    }

    private static HttpSandboxEngine Engine(HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("runtime-api")
            .ConfigureHttpClient(c =>
            {
                c.BaseAddress = new Uri("http://runtime-api:8081");
                c.DefaultRequestHeaders.Add("X-Runtime-Key", "test-key");
            })
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        var sp = services.BuildServiceProvider();
        return new HttpSandboxEngine(
            sp.GetRequiredService<IHttpClientFactory>(),
            NullLogger<HttpSandboxEngine>.Instance);
    }

    [Fact]
    public async Task Inspect_NotFound_Returns_Null()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(
            HttpStatusCode.NotFound));
        var engine = Engine(h);
        (await engine.InspectAsync("abc", CancellationToken.None))
            .ShouldBeNull();
        h.Last!.RequestUri!.ToString().ShouldBe(
            "http://runtime-api:8081/runtime/v1/sandboxes/abc");
    }

    [Fact]
    public async Task Inspect_Maps_EngineSandboxInfo()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"container_id\":\"c1\",\"state\":\"running\"," +
                "\"error\":null,\"labels\":{\"a\":\"b\"}," +
                "\"agent_url\":\"http://10.0.0.2:8000\"}"),
        });
        var info = await Engine(h).InspectAsync("c1", CancellationToken.None);
        info!.ContainerId.ShouldBe("c1");
        info.State.ShouldBe("running");
        info.AgentUrl!.ToString().ShouldBe("http://10.0.0.2:8000/");
    }

    [Fact]
    public async Task Actions_Post_To_Expected_Paths()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(
            HttpStatusCode.NoContent));
        var engine = Engine(h);
        await engine.PauseAsync("c1", CancellationToken.None);
        h.Last!.RequestUri!.AbsolutePath.ShouldBe(
            "/runtime/v1/sandboxes/c1/pause");
        await engine.UnpauseAsync("c1", CancellationToken.None);
        h.Last!.RequestUri!.AbsolutePath.ShouldBe(
            "/runtime/v1/sandboxes/c1/unpause");
        await engine.RemoveAsync("c1", CancellationToken.None);
        h.Last!.Method.ShouldBe(HttpMethod.Delete);
        h.Last!.Headers.GetValues("X-Runtime-Key")
            .ShouldBe(["test-key"]);
    }

    [Fact]
    public async Task Pause_NotFound_Maps_To_SandboxEngineException()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(
            HttpStatusCode.NotFound));
        var ex = await Should.ThrowAsync<SandboxEngineException>(
            () => Engine(h).PauseAsync("gone", CancellationToken.None));
        ex.NotFound.ShouldBeTrue();
    }

    [Fact]
    public async Task Error_Response_Throws_Without_Leaking_Key()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(
            HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("engine exploded"),
        });
        var ex = await Should.ThrowAsync<SandboxEngineException>(
            () => Engine(h).StopAsync("c1", CancellationToken.None));
        ex.Message.ShouldContain("500");
        ex.Message.ShouldNotContain("test-key");
    }

    [Fact]
    public async Task ClaimWarm_NoContent_Returns_Null()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(
            HttpStatusCode.NoContent));
        (await Engine(h).ClaimWarmAsync(
            new WarmClaimRequest("img", "ohn-sandbox-x", null),
            CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public void Provider_Selection_Local_Default_And_Remote()
    {
        foreach (var (provider, remote) in new[]
            { ("local", false), ("remote", true), (null as string, false) })
        {
            var cfg = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sandbox:Provider"] = provider,
                    ["Sandbox:RemoteBaseUrl"] = "http://runtime-api:8081",
                    ["Sandbox:RemoteApiKey"] = "k",
                })
                .Build();
            var sp = new ServiceCollection()
                .AddLogging()
                .AddSandboxing(cfg)
                .BuildServiceProvider();
            var engine = sp.GetRequiredService<ISandboxEngine>();
            engine.IsRemote.ShouldBe(remote);
            engine.GetType().Name.ShouldBe(
                remote ? "HttpSandboxEngine" : "DockerSandboxEngine");
        }
    }
}
