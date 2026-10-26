using System.Net;
using System.Text;

using Bunit;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

using OpenHandsNET.Client.Components.Conversation;
using OpenHandsNET.Client.Services;

using Shouldly;

namespace OpenHandsNET.Client.Tests;

/// <summary>
/// SPEC-113 RF-004: DesktopTab — surface desktop do sandbox embute o
/// noVNC via iframe (<c>{url}vnc.html?autoconnect…</c>); sem a surface,
/// empty-state.
/// </summary>
public class Spec113DesktopTabTests : TestContext
{
    public Spec113DesktopTabTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        L10nStub.Register(this);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> fn)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(fn(request));
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8,
                "application/json"),
        };

    private static HttpResponseMessage Empty() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8,
                "application/json"),
        };

    private const string SurfacesJson = """
        [{"name":"desktop","protocol":"http","port":6080,
          "url":"/api/v1/sandboxes/abc/surfaces/6080/",
          "expires_at":null}]
        """;

    private static ConversationStateService StateWith(
        Func<HttpRequestMessage, HttpResponseMessage> fn,
        out OpenHandsNetApiClient api)
    {
        api = new OpenHandsNetApiClient(new HttpClient(
            new StubHandler(fn))
        {
            BaseAddress = new Uri("http://localhost:8080"),
        });
        var state = new ConversationStateService(api,
            new ConversationHubClient(
                new Uri("http://localhost/hubs/conversations")));
        typeof(ConversationStateService)
            .GetProperty(nameof(ConversationStateService.Conversation))!
            .SetValue(state, new ConversationItem
            {
                Id = "conv-1",
                ExecutionStatus = "READY",
            });
        return state;
    }

    // AC: surface desktop → iframe do noVNC com autoconnect.
    [Fact]
    public void Desktop_Surface_Embeds_NoVnc_Iframe()
    {
        var state = StateWith(
            r => r.RequestUri!.AbsolutePath.EndsWith("/surfaces")
                ? Json(SurfacesJson) : Empty(), out var api);
        Services.AddSingleton(api);

        var cut = RenderComponent<DesktopTab>(
            p => p.Add(x => x.State, state));
        cut.WaitForAssertion(() =>
            cut.Find("iframe.desktop-frame").ShouldNotBeNull());

        var src = cut.Find("iframe.desktop-frame").GetAttribute("src")!;
        src.ShouldContain(
            "/api/v1/sandboxes/abc/surfaces/6080/vnc.html");
        src.ShouldContain("autoconnect=true");
        src.ShouldContain("resize=scale");
    }

    // AC: sem surface desktop → empty-state.
    [Fact]
    public void No_Desktop_Surface_Shows_Empty_State()
    {
        var state = StateWith(_ => Empty(), out var api);
        Services.AddSingleton(api);

        var cut = RenderComponent<DesktopTab>(
            p => p.Add(x => x.State, state));
        cut.WaitForAssertion(() =>
            cut.FindAll("iframe.desktop-frame").ShouldBeEmpty());
        cut.Find(".empty-state").ShouldNotBeNull();
    }
}