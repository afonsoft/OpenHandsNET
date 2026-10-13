using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OpenHandsNET.Client.Pages;
using OpenHandsNET.Client.Services;

namespace OpenHandsNET.Client.Tests;

/// <summary>
/// SPEC-05 bUnit tests — componentes-chave renderizam estados
/// (running / confirmation / error) sem depender de backend real.
/// </summary>
public class HomePageTests : TestContext
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> fn)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(fn(request));
    }

    private static OpenHandsNetApiClient Api(
        Func<HttpRequestMessage, HttpResponseMessage> fn) =>
        new(new HttpClient(new StubHandler(fn))
        {
            BaseAddress = new Uri("http://localhost:8080"),
        });

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    [Fact]
    public void Home_Renders_Brand_And_New_Conversation_Form()
    {
        Services.AddSingleton(Api(_ =>
            Json("""{"items":[],"next_page_id":null}""")));

        var cut = RenderComponent<Home>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("OpenHandsNET");
            cut.Markup.ShouldContain("Run");
        });
    }

    [Fact]
    public void Home_Lists_Conversations_With_Status_Badges()
    {
        Services.AddSingleton(Api(_ => Json("""
            {"items":[{"id":"c1","title":"fix bug","execution_status":"RUNNING",
                       "tags":[],"pinned":false,"confirmation_mode":false,
                       "created_at":"2026-01-01T00:00:00Z",
                       "updated_at":"2026-01-01T00:00:00Z"}],
              "next_page_id":null}
            """)));

        var cut = RenderComponent<Home>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("fix bug");
            cut.Markup.ShouldContain("RUNNING");
            cut.Find("a.conv-card").GetAttribute("href")
                .ShouldBe("/conversations/c1");
        });
    }
}
