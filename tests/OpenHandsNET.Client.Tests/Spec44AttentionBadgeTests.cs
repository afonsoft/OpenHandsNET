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
/// SPEC-44 RF-003: AttentionBadge — contador do inbox, breakdown no
/// title e callback de toggle do filtro.
/// </summary>
public class Spec44AttentionBadgeTests : TestContext
{
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

    private static AttentionInboxState InboxWith(
        Func<HttpRequestMessage, HttpResponseMessage> fn,
        IJSRuntime js)
    {
        var api = new OpenHandsNetApiClient(new HttpClient(
            new StubHandler(fn))
        {
            BaseAddress = new Uri("http://localhost:8080"),
        });
        return new AttentionInboxState(api,
            new ConversationHubClient(
                new Uri("http://localhost/hubs/conversations")),
            new ToastService(), js);
    }

    private const string AttentionJson = """
        {"count":2,"items":[
          {"conversation_id":"aaa","title":"c1","kind":"confirmation",
           "since":1700000002,"excerpt":null},
          {"conversation_id":"bbb","title":"c2","kind":"failed",
           "since":1700000001,"excerpt":"boom"}]}
        """;

    // AC: badge exibe o count do inbox (sem count quando zero).
    [Fact]
    public async Task Badge_Renders_Count_From_Inbox()
    {
        await using var inbox = InboxWith(_ => Json(AttentionJson),
            JSInterop.JSRuntime);
        await inbox.EnsureStartedAsync();
        Services.AddSingleton(inbox);

        var cut = RenderComponent<AttentionBadge>();
        cut.Find(".attention-count").TextContent.Trim().ShouldBe("2");
        cut.Find("button").GetAttribute("title")!
            .ShouldContain("confirmation");
    }

    // AC: click dispara OnClick (toggle do filtro na sidebar).
    [Fact]
    public async Task Badge_Click_Fires_OnClick()
    {
        await using var inbox = InboxWith(
            _ => Json("""{"count":0,"items":[]}"""),
            JSInterop.JSRuntime);
        await inbox.EnsureStartedAsync();
        Services.AddSingleton(inbox);

        var clicked = 0;
        var cut = RenderComponent<AttentionBadge>(p =>
            p.Add(x => x.OnClick, () => clicked++));
        cut.FindAll(".attention-count").ShouldBeEmpty();
        cut.Find("button").Click();
        clicked.ShouldBe(1);
    }

    // AC: Changed re-renderiza (tick de invalidação → novo count).
    [Fact]
    public async Task Badge_Updates_When_Inbox_Changes()
    {
        var calls = 0;
        await using var inbox = InboxWith(_ =>
            {
                calls++;
                return Json(calls == 1
                    ? """{"count":0,"items":[]}"""
                    : AttentionJson);
            }, JSInterop.JSRuntime);
        await inbox.EnsureStartedAsync();
        Services.AddSingleton(inbox);

        var cut = RenderComponent<AttentionBadge>();
        cut.FindAll(".attention-count").ShouldBeEmpty();
        await inbox.RefreshAsync(notifyNew: false);
        cut.Find(".attention-count").TextContent.Trim().ShouldBe("2");
    }
}
