using System.Net;
using System.Text;

using Bunit;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

using OpenHandsNET.Client.Components;
using OpenHandsNET.Client.Services;

using Shouldly;

namespace OpenHandsNET.Client.Tests;

/// <summary>
/// SPEC-75 RF-005: AttentionFeed — sino com badge + dropdown do inbox;
/// item navega para a conversa e fecha o dropdown.
/// </summary>
public class Spec75AttentionFeedTests : TestContext
{
    public Spec75AttentionFeedTests()
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

    private AttentionInboxState InboxWith(
        Func<HttpRequestMessage, HttpResponseMessage> fn)
    {
        var api = new OpenHandsNetApiClient(new HttpClient(
            new StubHandler(fn))
        {
            BaseAddress = new Uri("http://localhost:8080"),
        });
        return new AttentionInboxState(api,
            new ConversationHubClient(
                new Uri("http://localhost/hubs/conversations")),
            new ToastService(), JSInterop.JSRuntime);
    }

    private const string AttentionJson = """
        {"count":2,"items":[
          {"conversation_id":"aaa","title":"c1","kind":"confirmation",
           "since":1700000002,"excerpt":null},
          {"conversation_id":"bbb","title":"c2","kind":"failed",
           "since":1700000001,"excerpt":"boom"}]}
        """;

    // AC: bell badge mostra o count; dropdown abre listando itens.
    [Fact]
    public async Task Feed_Toggle_Lists_Inbox_Items()
    {
        await using var inbox = InboxWith(_ => Json(AttentionJson));
        await inbox.EnsureStartedAsync();
        Services.AddSingleton(inbox);

        var cut = RenderComponent<AttentionFeed>();
        cut.Find(".attention-count").TextContent.Trim().ShouldBe("2");
        cut.FindAll(".attention-drop").ShouldBeEmpty();

        cut.Find(".attention-badge").Click();
        var items = cut.FindAll(".attention-item");
        items.Count.ShouldBe(2);
        items.ElementAt(0).QuerySelector(".atitle")!.TextContent.Trim()
            .ShouldBe("c1");
    }

    // AC: item clicado navega para a conversa e fecha o dropdown.
    [Fact]
    public async Task Feed_Item_Navigates_And_Closes()
    {
        await using var inbox = InboxWith(_ => Json(AttentionJson));
        await inbox.EnsureStartedAsync();
        Services.AddSingleton(inbox);

        var cut = RenderComponent<AttentionFeed>();
        cut.Find(".attention-badge").Click();
        cut.FindAll(".attention-item").ElementAt(1).Click();

        var nav = Services.GetRequiredService<
            Bunit.TestDoubles.FakeNavigationManager>();
        nav.Uri.ShouldEndWith("/conversations/bbb");
        cut.FindAll(".attention-drop").ShouldBeEmpty();
    }
}