using System.Net;
using System.Text;

using Bunit;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Client.Components;
using Sandcastle.Client.Pages;
using Sandcastle.Client.Services;

using Shouldly;

using Xunit;

namespace Sandcastle.Client.Tests;

/// <summary>
/// SPEC-132: beta auth UX — AuthStateService (probe/sanitize/guard),
/// AuthPage (rendering por provider) e UserMenu (avatar, theme, sign-out).
/// </summary>
public class Spec132AuthUxTests : BunitContext
{
    private sealed class SessionHandler(Func<string> json)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json(), Encoding.UTF8,
                    "application/json"),
            });
    }

    private static HttpClient SessionClient(Func<string> json) =>
        new(new SessionHandler(json))
        {
            BaseAddress = new Uri("http://localhost:8080"),
        };

    // JSON do probe /session — definido no teste ANTES do primeiro render
    // (o bUnit congela o DI na primeira resolução de serviço).
    private string _sessionJson = SessionNone;

    private const string SessionNone =
        """{"provider":"none","authenticated":false,"user":null}""";
    private const string SessionOidcAnon =
        """{"provider":"oidc","authenticated":false,"user":null}""";
    private const string SessionOidcUser =
        """{"provider":"oidc","authenticated":true,"user":{"id":"u1","email":"a@b.c","display_name":"Ana Bia","is_super_admin":true}}""";
    private const string SessionLocalAnon =
        """{"provider":"local","authenticated":false,"user":null}""";

    public Spec132AuthUxTests()
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
        Services.AddSingleton(new ThemeService(JSInterop.JSRuntime, api,
            new HttpClient { BaseAddress = new Uri("http://localhost:8080") }));
        Services.AddSingleton(new DialogService());
        // AuthStateService via factory lazy: lê _sessionJson no primeiro
        // resolve (render), depois de o teste já ter escolhido o provider.
        Services.AddSingleton(sp =>
        {
            var nav = sp.GetRequiredService<NavigationManager>();
            var svc = new AuthStateService(
                SessionClient(() => _sessionJson), nav);
            svc.InitializeAsync().GetAwaiter().GetResult();
            return svc;
        });
    }

    private AuthStateService BuildAuth(string json)
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        var svc = new AuthStateService(SessionClient(() => json), nav);
        svc.InitializeAsync().GetAwaiter().GetResult();
        return svc;
    }

    private AuthStateService DiAuth() =>
        Services.GetRequiredService<AuthStateService>();

    // ---- AuthStateService -------------------------------------------------

    [Fact]
    public void Probe_None_Is_Local_Mode()
    {
        var auth = BuildAuth(SessionNone);
        auth.RequiresAuth.ShouldBeFalse();
        auth.IsAuthenticated.ShouldBeFalse();
        auth.User.ShouldBeNull();
    }

    [Fact]
    public void Probe_Oidc_User_Parses_Profile()
    {
        var auth = BuildAuth(SessionOidcUser);
        auth.RequiresAuth.ShouldBeTrue();
        auth.IsAuthenticated.ShouldBeTrue();
        auth.User!.Email.ShouldBe("a@b.c");
        auth.User.DisplayName.ShouldBe("Ana Bia");
        auth.User.IsSuperAdmin.ShouldBeTrue();
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/conversations/abc?x=1", "/conversations/abc?x=1")]
    [InlineData("https://evil.com", "/")]
    [InlineData("//evil.com", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    [InlineData("conversations/abc", "/")]
    [InlineData("/\\evil", "/")]
    public void SanitizeReturnUrl_Rejects_Open_Redirect(
        string? input, string expected) =>
        AuthStateService.SanitizeReturnUrl(input).ShouldBe(expected);

    [Theory]
    [InlineData("auth", true)]
    [InlineData("share/abc123", true)]
    [InlineData("shared/abc123", true)]
    [InlineData("conversations/abc", false)]
    [InlineData("admin", false)]
    public void IsPublicPath_Matches_Route_Set(string path, bool expected) =>
        AuthStateService.IsPublicPath(path).ShouldBe(expected);

    // ---- AuthPage ----------------------------------------------------------

    [Fact]
    public void AuthPage_Oidc_Shows_Sso_Button()
    {
        _sessionJson = SessionOidcAnon;
        var cut = Render<AuthPage>();
        cut.Find(".auth-submit").TextContent.ShouldContain(
            Services.GetRequiredService<LocalizationService>()
                ["auth.sso_button"]);
    }

    [Fact]
    public void AuthPage_None_Shows_Local_Mode()
    {
        _sessionJson = SessionNone;
        var cut = Render<AuthPage>();
        cut.Find(".authcard-note").TextContent.ShouldContain(
            Services.GetRequiredService<LocalizationService>()
                ["auth.local_mode"]);
        cut.FindAll("form").ShouldBeEmpty();
    }

    [Fact]
    public void AuthPage_Local_Shows_Email_Form()
    {
        _sessionJson = SessionLocalAnon;
        var cut = Render<AuthPage>();
        cut.Find("#auth-email").ShouldNotBeNull();
        cut.Find("button[type=submit].auth-submit").ShouldNotBeNull();
    }

    [Fact]
    public void AuthPage_Authenticated_Redirects_To_ReturnUrl()
    {
        _sessionJson = SessionOidcUser;
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(
            "http://localhost:8080/auth?returnUrl=%2Fconversations%2Fabc");
        Render<AuthPage>();
        nav.Uri.ShouldBe("http://localhost:8080/conversations/abc");
    }

    // ---- UserMenu ----------------------------------------------------------

    [Fact]
    public void UserMenu_Authenticated_Shows_Initials_And_SignOut()
    {
        _sessionJson = SessionOidcUser;
        var cut = Render<UserMenu>();
        cut.Find(".avatar-initials").TextContent.Trim().ShouldBe("AB");
        cut.Find("button.avatar-btn").Click();
        var l = Services.GetRequiredService<LocalizationService>();
        var items = cut.FindAll(".menu-item");
        items.ShouldContain(i =>
            i.TextContent.Contains(l["user_menu.sign_out"]));
        items.ShouldContain(i =>
            i.TextContent.Contains(l["sidebar.administration"]));
    }

    [Fact]
    public void UserMenu_Local_Mode_No_SignOut()
    {
        _sessionJson = SessionNone;
        var cut = Render<UserMenu>();
        cut.Find("button.avatar-btn").Click();
        var l = Services.GetRequiredService<LocalizationService>();
        cut.Find(".usermenu-sub").TextContent
            .ShouldContain(l["user_menu.local_mode"]);
        cut.FindAll(".menu-item").ShouldNotContain(i =>
            i.TextContent.Contains(l["user_menu.sign_out"]));
    }

    [Fact]
    public void UserMenu_Unauthenticated_Renders_Nothing()
    {
        _sessionJson = SessionOidcAnon;
        var cut = Render<UserMenu>();
        cut.FindAll(".avatar-btn").ShouldBeEmpty();
    }

    // ---- Guard ---------------------------------------------------------------

    [Fact]
    public async Task Guard_Redirects_Internal_Route_When_Unauthenticated()
    {
        var auth = BuildAuth(SessionOidcAnon);
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost:8080/conversations/abc");
        await auth.HandleUnauthorizedAsync();
        nav.Uri.ShouldContain("/auth");
        nav.Uri.ShouldContain("returnUrl=");
    }

    [Fact]
    public async Task Guard_No_Redirect_On_Public_Route_Or_None_Provider()
    {
        var auth = BuildAuth(SessionOidcAnon);
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost:8080/share/token123");
        await auth.HandleUnauthorizedAsync();
        nav.Uri.ShouldContain("/share/token123");

        var authNone = BuildAuth(SessionNone);
        nav.NavigateTo("http://localhost:8080/conversations/abc");
        await authNone.HandleUnauthorizedAsync();
        nav.Uri.ShouldContain("/conversations/abc");
    }

    [Fact]
    public void DiAuth_Initializes_On_First_Resolve()
    {
        _sessionJson = SessionOidcUser;
        var auth = DiAuth();
        auth.IsAuthenticated.ShouldBeTrue();
        auth.RequiresAuth.ShouldBeTrue();
    }
}