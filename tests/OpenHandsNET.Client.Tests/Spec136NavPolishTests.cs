using Bunit;

using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Client.Components;
using OpenHandsNET.Client.Layout;
using OpenHandsNET.Client.Services;

using Shouldly;

using Xunit;

namespace OpenHandsNET.Client.Tests;

/// <summary>
/// SPEC-136: nav polish — rotas órfãs na shell (Knowledge, Skills &amp;
/// MCP) + novos ícones resolvíveis.
/// </summary>
public class Spec136NavPolishTests : BunitContext
{
    public Spec136NavPolishTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        L10nStub.Register(this);
        var toasts = new ToastService();
        Services.AddSingleton(toasts);
        var api = new OpenHandsNetApiClient(new HttpClient
        {
            BaseAddress = new Uri("http://localhost:8080"),
        });
        Services.AddSingleton(api);
        var adminApi = new AdminApiClient(new HttpClient
        {
            BaseAddress = new Uri("http://localhost:8080"),
        });
        Services.AddSingleton(adminApi);
        Services.AddSingleton(new ThemeService(JSInterop.JSRuntime, api,
            new HttpClient { BaseAddress = new Uri("http://localhost:8080") }));
        var hub = new ConversationHubClient(
            new Uri("http://localhost:8080/hubs/conversations"));
        Services.AddSingleton(hub);
        var inbox = new AttentionInboxState(api, hub, toasts,
            JSInterop.JSRuntime);
        Services.AddSingleton(inbox);
        Services.AddSingleton(new ChatNotificationsService(inbox,
            JSInterop.JSRuntime));
        Services.AddSingleton(new AdminState(adminApi));
        Services.AddSingleton(new PaletteService());
        Services.AddSingleton(new DialogService());
    }

    [Fact]
    public void Sidebar_Links_Knowledge_And_SkillsMcp()
    {
        var cut = Render<MainLayout>(p => p.Add(
            x => x.Body, (Microsoft.AspNetCore.Components.RenderFragment)(_ => { })));

        cut.Find("a.sb-item[href='/knowledge']").ShouldNotBeNull();
        cut.Find("a.sb-item[href='/settings/skills']").ShouldNotBeNull();
    }

    [Theory]
    [InlineData("pin")]
    [InlineData("archive")]
    [InlineData("pencil")]
    [InlineData("share-nodes")]
    [InlineData("branch")]
    [InlineData("thumbs-up")]
    [InlineData("thumbs-down")]
    [InlineData("sparkles")]
    [InlineData("globe")]
    [InlineData("tag")]
    [InlineData("mic")]
    [InlineData("sliders")]
    [InlineData("link-external")]
    public void New_Icons_Resolve_To_Own_Path(string name)
    {
        var path = Icons.Get(name);
        path.ShouldNotBe(Icons.Get("info"));
        path.ShouldContain("<");
    }
}
