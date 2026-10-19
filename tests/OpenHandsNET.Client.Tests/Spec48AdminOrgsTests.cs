using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Client.Pages.Admin;
using OpenHandsNET.Client.Services;

using Shouldly;

namespace OpenHandsNET.Client.Tests;

/// <summary>
/// SPEC-48 RF-003 — página /admin/orgs: picker de org + card de
/// security policy (GET popula campos; PUT envia overrides; reset).
/// </summary>
public class Spec48AdminOrgsTests : TestContext
{
    public Spec48AdminOrgsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        L10nStub.Register(this);
        Services.AddSingleton<ToastService>();
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public List<(string Method, string Url, string? Body)> Requests
        { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null
                ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method.Method,
                request.RequestUri!.ToString(), body));
            return responder(request);
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

    private static HttpResponseMessage Responder(HttpRequestMessage r)
    {
        var url = r.RequestUri!.ToString();
        if (url.Contains("/admin/super-admins/me"))
        {
            return Json(new { is_super_admin = true });
        }

        if (url.EndsWith("/api/v1/organizations"))
        {
            return Json(new[]
            {
                new { id = "11111111-1111-1111-1111-111111111111",
                      name = "Acme" },
            });
        }

        if (url.Contains("/security-policy"))
        {
            return Json(new
            {
                preset = "balanced",
                analyzer_override = (string?)null,
                ask_mcp_tools = false,
                min_confirm_risk = "high",
                resume_strict = (bool?)null,
            });
        }

        return Json(new { });
    }

    private StubHandler Wire()
    {
        var handler = new StubHandler(Responder);
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8080"),
        };
        var api = new AdminApiClient(http);
        Services.AddSingleton(api);
        Services.AddSingleton(new AdminState(api));
        return handler;
    }

    [Fact]
    public void OrgsPage_Loads_Policy_For_Selected_Org()
    {
        Wire();
        var cut = RenderComponent<AdminOrgsPage>();
        cut.WaitForAssertion(() =>
            cut.Markup.ShouldContain("Acme"), TimeSpan.FromSeconds(5));

        cut.Find("select").Change(
            "11111111-1111-1111-1111-111111111111");
        cut.WaitForAssertion(() =>
            cut.Markup.ShouldContain("Política de segurança"),
            TimeSpan.FromSeconds(5));
        // preset veio do GET → select marcado em balanced
        cut.Find("#sp-preset").GetAttribute("value")
            .ShouldBe("balanced");
    }

    [Fact]
    public void Save_Puts_Policy_With_Overrides()
    {
        var handler = Wire();
        var cut = RenderComponent<AdminOrgsPage>();
        cut.WaitForAssertion(() =>
            cut.Markup.ShouldContain("Acme"), TimeSpan.FromSeconds(5));
        cut.Find("select").Change(
            "11111111-1111-1111-1111-111111111111");
        cut.WaitForAssertion(() =>
            cut.Markup.ShouldContain("Política de segurança"),
            TimeSpan.FromSeconds(5));

        cut.Find("#sp-preset").Change("strict");
        cut.Find("#sp-risk").Change("medium");
        cut.Find("button.btn-primary").Click();

        cut.WaitForAssertion(() =>
            handler.Requests.ShouldContain(r =>
                r.Method == "PUT"
                && r.Url.Contains("/security-policy")
                && r.Body != null
                && r.Body.Contains("\"strict\"")
                && r.Body.Contains("\"medium\"")),
            TimeSpan.FromSeconds(5));
    }
}