using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OpenHandsNET.Client.Pages;
using OpenHandsNET.Client.Services;
using Shouldly;

namespace OpenHandsNET.Client.Tests;

/// <summary>
/// SPEC-47 RF-004: página <c>/automations</c> — tabela + toggle +
/// run-now + formulário de criação; detalhe com histórico e hook.
/// </summary>
public class Spec47AutomationsPageTests : TestContext
{
    public Spec47AutomationsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        L10nStub.Register(this);
        Services.AddSingleton(new ToastService());
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> fn)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(fn(request));
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8,
                "application/json"),
        };

    private OpenHandsNetApiClient Api(
        Func<HttpRequestMessage, HttpResponseMessage> fn)
    {
        var api = new OpenHandsNetApiClient(new HttpClient(
            new StubHandler(fn))
        {
            BaseAddress = new Uri("http://localhost:8080"),
        });
        Services.AddSingleton(api);
        return api;
    }

    private const string AutomationJson = """
        {"id":"11111111-1111-1111-1111-111111111111","org_id":null,
         "created_by_user_id":"u","name":"nightly","prompt":"run",
         "llm_model":null,"selected_repository":null,
         "schedule_kind":"interval","schedule_time_utc":null,
         "interval_minutes":60,"weekday":null,"enabled":true,
         "next_run_at":"2026-10-18T09:00:00Z","last_run_at":null,
         "created_at":"2026-10-17T00:00:00Z","updated_at":"2026-10-17T00:00:00Z"}
        """;

    [Fact]
    public void List_Renders_Rows_With_Schedule_And_NextRun()
    {
        Api(_ => Json($"[{AutomationJson}]"));
        var cut = RenderComponent<AutomationsPage>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("nightly");
            cut.Markup.ShouldContain("a cada 60 min");
            cut.Markup.ShouldContain("Executar agora");
        });
    }

    [Fact]
    public void Toggle_Patches_Enabled()
    {
        var patched = false;
        Api(req => req.Method == HttpMethod.Patch
            ? (patched = true, Json(AutomationJson)).Item2
            : Json($"[{AutomationJson}]"));
        var cut = RenderComponent<AutomationsPage>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("nightly"));
        cut.Find("input[type=checkbox]").Change(false);
        patched.ShouldBeTrue();
    }

    [Fact]
    public void Detail_Renders_Runs_Table()
    {
        var runs = """
            [{"id":"22222222-2222-2222-2222-222222222222",
              "automation_id":"11111111-1111-1111-1111-111111111111",
              "started_at":"2026-10-17T09:00:00Z","finished_at":null,
              "status":"succeeded",
              "conversation_id":"33333333-3333-3333-3333-333333333333",
              "error":null}]
            """;
        Api(req => req.RequestUri!.AbsolutePath.Contains("/runs")
            ? Json(runs) : Json(AutomationJson));
        var cut = RenderComponent<AutomationDetailPage>(p =>
            p.Add(x => x.Id,
                Guid.Parse("11111111-1111-1111-1111-111111111111")));
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("succeeded");
            cut.Markup.ShouldContain("/conversations/33333333");
        });
    }
}
