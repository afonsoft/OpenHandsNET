using Bunit;
using Bunit.TestDoubles;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Client.Components;
using OpenHandsNET.Client.Layout;
using OpenHandsNET.Client.Services;

namespace OpenHandsNET.Client.Tests;

/// <summary>
/// SPEC-130 AC: SettingsModal abre/fecha via DialogService com
/// focus-trap; rail colapsa e persiste em <c>ohn.sidebar.collapsed</c>;
/// engrenagem do sidebar abre o modal em vez de navegar.
/// </summary>
public class Spec130UxFollowupsTests : BunitContext
{
    private readonly DialogService _dialogs;

    public Spec130UxFollowupsTests()
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
        _dialogs = new DialogService();
        Services.AddSingleton(_dialogs);
    }

    private IRenderedComponent<MainLayout> RenderShell() =>
        Render<MainLayout>(p => p.Add(
            x => x.Body, (RenderFragment)(_ => { })));

    [Fact]
    public void SettingsModal_Opens_Renders_Content_And_Closes()
    {
        var cut = Render<DialogHost>();
        _ = _dialogs.ShowSettingsAsync();

        cut.Find("div.settings-modal[role=dialog]").ShouldNotBeNull();
        cut.Find("nav.settings-rail").ShouldNotBeNull();
        JSInterop.Invocations["ohn.trapFocus"].ShouldNotBeEmpty();

        _dialogs.Resolve(null);
        cut.FindAll("div.settings-modal").ShouldBeEmpty();
    }

    [Fact]
    public void SettingsModal_Escape_Closes()
    {
        var cut = Render<DialogHost>();
        _ = _dialogs.ShowSettingsAsync();
        cut.Find("div.settings-modal[role=dialog]")
            .KeyDown(new KeyboardEventArgs { Key = "Escape" });
        cut.FindAll("div.settings-modal").ShouldBeEmpty();
    }

    [Fact]
    public void Gear_Opens_Settings_Modal_From_Sidebar()
    {
        var cut = RenderShell();
        _dialogs.Current.ShouldBeNull();
        var label = Services.GetRequiredService<LocalizationService>()
            ["sidebar.settings"];
        var gear = cut.FindAll("button.sb-item")
            .First(b => b.GetAttribute("aria-label") == label);
        gear.Click();
        _dialogs.Current?.Kind.ShouldBe(DialogKind.Settings);
    }

    [Fact]
    public void Rail_Toggle_Persists_Collapsed_State()
    {
        var cut = RenderShell();
        var toggle = cut.Find("aside.sidebar button[aria-expanded]");
        toggle.Click();
        JSInterop.Invocations["localStorage.setItem"]
            .ShouldContain(i =>
                i.Arguments.Contains("ohn.sidebar.collapsed"));
    }
}