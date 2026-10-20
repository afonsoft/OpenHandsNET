using System.Net;
using System.Reflection;
using System.Text;

using Bunit;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

using OpenHandsNET.Client.Components.Ide;
using OpenHandsNET.Client.Services;

using Shouldly;

namespace OpenHandsNET.Client.Tests;

/// <summary>
/// SPEC-77 RF-002: IdeTab — surface vscode do sandbox aparece como toggle
/// na activity bar e embute o code-server via iframe (URL já traz o
/// preview token).
/// </summary>
public class Spec77VscodeSurfaceTests : TestContext
{
    public Spec77VscodeSurfaceTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        L10nStub.Register(this);
        Services.AddSingleton(new MonacoInterop(JSInterop.JSRuntime));
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
        [{"name":"vscode","protocol":"http","port":8080,
          "url":"https://preview.example/vscode?token=abc",
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
        // Conversation é private-set — o teste injeta a conversa mínima
        // direto (equivale ao que OpenAsync carrega antes do hub).
        typeof(ConversationStateService)
            .GetProperty(nameof(ConversationStateService.Conversation))!
            .SetValue(state, new ConversationItem
            {
                Id = "conv-1",
                ExecutionStatus = "READY",
            });
        return state;
    }

    // AC: surface vscode → botão na activity bar → iframe com o URL.
    [Fact]
    public async Task Vscode_Toggle_Embeds_CodeServer_Iframe()
    {
        var state = StateWith(
            r => r.RequestUri!.AbsolutePath.EndsWith("/surfaces")
                ? Json(SurfacesJson) : Empty(), out var api);
        Services.AddSingleton(api);

        var cut = RenderComponent<IdeTab>(p => p.Add(x => x.State, state));
        cut.WaitForAssertion(() =>
            cut.Find(".ide-activity-vscode").ShouldNotBeNull());

        cut.Find(".ide-activity-vscode").Click();
        var frame = cut.Find("iframe.ide-vscode");
        frame.GetAttribute("src")
            .ShouldBe("https://preview.example/vscode?token=abc");

        // Toggle off — volta ao Monaco/empty-state.
        cut.Find(".ide-activity-vscode").Click();
        cut.FindAll("iframe.ide-vscode").ShouldBeEmpty();
        await Task.CompletedTask;
    }

    // AC: sem surface vscode → sem botão (workspace legado/agente off).
    [Fact]
    public void No_Vscode_Surface_Hides_Toggle()
    {
        var state = StateWith(_ => Empty(), out var api);
        Services.AddSingleton(api);

        var cut = RenderComponent<IdeTab>(p => p.Add(x => x.State, state));
        cut.WaitForAssertion(() =>
            cut.FindAll(".ide-activity-vscode").ShouldBeEmpty());
    }
}