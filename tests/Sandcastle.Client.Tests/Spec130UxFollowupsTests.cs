using Bunit;
using Bunit.TestDoubles;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Client.Components;
using Sandcastle.Client.Layout;
using Sandcastle.Client.Services;

namespace Sandcastle.Client.Tests;

/// <summary>
/// SPEC-130 AC: SettingsModal abre/fecha via DialogService com
/// focus-trap; rail colapsa e persiste em <c>sc.sidebar.collapsed</c>;
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
        var api = new SandcastleApiClient(new HttpClient
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
        // SPEC-132: MainLayout injeta AuthStateService (guard de auth).
        AuthStub.Register(this);
    }

    private IRenderedComponent<MainLayout> RenderShell() =>
        Render<MainLayout>(p => p.Add(
            x => x.Body, (RenderFragment)(_ => { })));

    [Fact]
    public void SettingsModal_Opens_Renders_Content_And_Closes()
    {
        var cut = Render<DialogHost>();
        _ = _dialogs.ShowSettingsAsync();

        cut.WaitForElement("div.settings-modal[role=dialog]");
        cut.WaitForElement("nav.settings-rail");
        cut.WaitForAssertion(() =>
            JSInterop.Invocations["sc.trapFocus"].ShouldNotBeEmpty());

        _dialogs.Resolve(null);
        cut.WaitForAssertion(() =>
            cut.FindAll("div.settings-modal").ShouldBeEmpty());
    }

    [Fact]
    public void SettingsModal_Escape_Closes()
    {
        var cut = Render<DialogHost>();
        _ = _dialogs.ShowSettingsAsync();
        cut.WaitForElement("div.settings-modal[role=dialog]")
            .KeyDown(new KeyboardEventArgs { Key = "Escape" });
        cut.WaitForAssertion(() =>
            cut.FindAll("div.settings-modal").ShouldBeEmpty());
    }

    [Fact]
    public void Gear_Opens_Settings_Modal_From_Sidebar()
    {
        var cut = RenderShell();
        _dialogs.Current.ShouldBeNull();
        var label = Services.GetRequiredService<LocalizationService>()
            ["sidebar.settings"];
        var gear = cut.WaitForElement($"button.sb-item[aria-label]");
        cut.FindAll("button.sb-item")
            .First(b => b.GetAttribute("aria-label") == label).Click();
        _dialogs.Current?.Kind.ShouldBe(DialogKind.Settings);
    }

    [Fact]
    public void Rail_Toggle_Persists_Collapsed_State()
    {
        var cut = RenderShell();
        var toggle = cut.WaitForElement("aside.sidebar button[aria-expanded]");
        toggle.Click();
        cut.WaitForAssertion(() =>
            JSInterop.Invocations["localStorage.setItem"]
                .ShouldContain(i =>
                    i.Arguments.Contains("sc.sidebar.collapsed")));
    }
}