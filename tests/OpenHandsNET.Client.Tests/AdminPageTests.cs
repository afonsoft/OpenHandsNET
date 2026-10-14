using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OpenHandsNET.Client.Pages.Admin;
using OpenHandsNET.Client.Services;

namespace OpenHandsNET.Client.Tests;

/// <summary>
/// SPEC-21 — AdminState gate + páginas admin com HTTP stub (bUnit).
/// </summary>
public class AdminPageTests : TestContext
{
    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public List<(string Method, string Url)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method.Method, request.RequestUri!.ToString()));
            return Task.FromResult(responder(request));
        }
    }

    private static HttpResponseMessage Json(object payload) => new(
        HttpStatusCode.OK)
    {
        Content = JsonContent.Create(payload,
            options: new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            }),
    };

    /// <summary>Stub típico: super-admin + listas vazias + counts.</summary>
    private static HttpResponseMessage DefaultResponder(HttpRequestMessage r)
    {
        var url = r.RequestUri!.ToString();
        if (url.Contains("/admin/super-admins/me"))
        {
            return Json(new { is_super_admin = true });
        }

        if (url.Contains("/admin/users/count"))
        {
            return Json(new { count = 2 });
        }

        if (url.Contains("/admin/users"))
        {
            return Json(new[]
            {
                new { user_id = "u1", email = "a@x.io", display_name = "A" },
                new { user_id = "u2", email = "b@x.io", display_name = "B" },
            });
        }

        if (url.Contains("/admin/plans") || url.Contains("/organizations"))
        {
            return Json(Array.Empty<object>());
        }

        if (url.Contains("/admin/feature-flags"))
        {
            return Json(new[]
            {
                new { name = "beta_ui", enabled = true,
                      description = "beta" },
            });
        }

        if (url.Contains("/admin/audit"))
        {
            return Json(new { items = Array.Empty<object>(), total = 0 });
        }

        if (url.Contains("/admin/super-admins"))
        {
            return Json(new[] { new { user_id = "u1", email = "a@x.io" } });
        }

        return Json(new { });
    }

    private (AdminState State, AdminApiClient Api, StubHandler Handler) Wire(
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        var handler = new StubHandler(responder ?? DefaultResponder);
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8080"),
        };
        var api = new AdminApiClient(http);
        var state = new AdminState(api);
        Services.AddSingleton(api);
        Services.AddSingleton(state);
        return (state, api, handler);
    }

    [Fact]
    public async Task AdminState_Resolves_True_For_SuperAdmin()
    {
        var (state, _, _) = Wire();
        (await state.EnsureLoadedAsync()).ShouldBeTrue();
        (await state.EnsureLoadedAsync()).ShouldBeTrue(); // cache
    }

    [Fact]
    public async Task AdminState_False_For_Regular_User()
    {
        var (state, _, _) = Wire(_ => Json(new { is_super_admin = false }));
        (await state.EnsureLoadedAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task AdminState_False_On_Http_Error()
    {
        var (state, _, _) = Wire(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden));
        (await state.EnsureLoadedAsync()).ShouldBeFalse();
    }

    [Fact]
    public void NonAdmin_Is_Redirected_Away()
    {
        Wire(_ => Json(new { is_super_admin = false }));
        var nav = Services.GetRequiredService<NavigationManager>()
            as Bunit.TestDoubles.FakeNavigationManager;
        RenderComponent<AdminUsersPage>();
        nav!.Uri.ShouldBe("http://localhost/");
    }

    [Fact]
    public void UsersPage_Renders_Rows_For_Admin()
    {
        Wire();
        var cut = RenderComponent<AdminUsersPage>();
        cut.WaitForAssertion(() =>
            cut.Markup.ShouldContain("a@x.io"), TimeSpan.FromSeconds(5));
        cut.Markup.ShouldContain("b@x.io");
        cut.Markup.ShouldContain("(2 total)");
    }

    [Fact]
    public void AdminsPage_Grant_Posts_Email()
    {
        var (_, _, handler) = Wire();
        var cut = RenderComponent<AdminAdminsPage>();
        cut.WaitForAssertion(() =>
            cut.Markup.ShouldContain("a@x.io"), TimeSpan.FromSeconds(5));
        cut.Find("input").Input("new@x.io");
        cut.Find("button.primary").Click();
        cut.WaitForAssertion(() =>
            handler.Requests.ShouldContain(r =>
                r.Method == "POST"
                && r.Url.Contains("/admin/super-admins")),
                TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void FlagsPage_Renders_Toggle_State()
    {
        Wire();
        var cut = RenderComponent<AdminFlagsPage>();
        cut.WaitForAssertion(() =>
            cut.Markup.ShouldContain("beta_ui"), TimeSpan.FromSeconds(5));
        cut.Markup.ShouldContain(">on<");
    }

    [Fact]
    public void Index_Shows_Counts_And_Bundle_Link()
    {
        Wire();
        var cut = RenderComponent<AdminIndexPage>();
        cut.WaitForAssertion(() =>
            cut.Markup.ShouldContain("support bundle"),
            TimeSpan.FromSeconds(5));
        cut.Markup.ShouldContain("/api/v1/support-bundle");
    }

    [Fact]
    public void Denied_Page_Shows_Friendly_Error()
    {
        Wire(r => r.RequestUri!.ToString().Contains("super-admins/me")
            ? Json(new { is_super_admin = true })
            : new HttpResponseMessage(HttpStatusCode.Forbidden));
        var cut = RenderComponent<AdminUsersPage>();
        cut.WaitForAssertion(() =>
            cut.Markup.ShouldContain("403"), TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("foo.cs", "csharp")]
    [InlineData("app.ts", "typescript")]
    [InlineData("x.json", "json")]
    [InlineData("README.md", "markdown")]
    [InlineData("Dockerfile", "dockerfile")]
    [InlineData("weird.xyz", "plaintext")]
    public void Monaco_Language_Mapping(string path, string expected) =>
        MonacoInterop.LanguageFor(path).ShouldBe(expected);
}
