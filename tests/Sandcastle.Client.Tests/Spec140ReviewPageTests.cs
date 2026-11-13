using System.Net;
using System.Text;
using System.Text.Json;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Client.Pages;
using Sandcastle.Client.Services;

using Shouldly;

namespace Sandcastle.Client.Tests;

/// <summary>
/// #435: páginas <c>/reviews</c> e <c>/reviews/{id}</c> — browser de
/// findings do auto-review de PR (SPEC-140 deferral): listagem com
/// filtros server-side, detalhe agrupado por arquivo, triagem PATCH
/// single/bulk, stale marcado visualmente.
/// </summary>
public class Spec140ReviewPageTests : BunitContext
{
    private static readonly Guid RunId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    public Spec140ReviewPageTests()
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

    private SandcastleApiClient Api(
        Func<HttpRequestMessage, HttpResponseMessage> fn)
    {
        var api = new SandcastleApiClient(new HttpClient(
            new StubHandler(fn))
        {
            BaseAddress = new Uri("http://localhost:8080"),
        });
        Services.AddSingleton(api);
        return api;
    }

    private static string RunJson(string status = "completed",
        int findings = 2) => $$"""
        {"id":"{{RunId}}","org_id":null,
         "connection_id":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
         "repository":"octo/hello","pr_number":42,
         "head_sha":"0123456789abcdef","diff_hash":null,
         "status":"{{status}}","partial":false,"trigger":"webhook",
         "requested_by_user_id":null,"error":null,
         "finding_count":{{findings}},
         "created_at":"2026-10-17T09:00:00Z","finished_at":null}
        """;

    private static string FindingJson(Guid id, string path, int? line,
        string severity, string kind, string status,
        string? commentUrl = null) => $$"""
        {"id":"{{id}}","review_run_id":"{{RunId}}",
         "repository":"octo/hello","pr_number":42,
         "kind":"{{kind}}","severity":"{{severity}}","confidence":0.9,
         "path":"{{path}}","line":{{(line?.ToString() ?? "null")}},
         "title":"t-{{kind}}","body":"body {{kind}}",
         "suggested_patch":null,"status":"{{status}}",
         "comment_url":{{(commentUrl is null ? "null" : $"\"{commentUrl}\"")}},
         "created_at":"2026-10-17T09:01:00Z"}
        """;

    private static string DetailJson(params string[] findings) => $$"""
        {"run":{{RunJson()}},
         "findings":[{{string.Join(",", findings)}}]}
        """;

    [Fact]
    public void List_Renders_Runs_With_Repo_Pr_And_Status()
    {
        Api(_ => Json(
            $$"""{"items":[{{RunJson()}}],"next_page_id":null}"""));
        var cut = Render<ReviewsPage>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("octo/hello");
            cut.Markup.ShouldContain("#42");
            cut.Markup.ShouldContain("01234567");
            cut.Markup.ShouldContain("completed");
            cut.Markup.ShouldContain($"/reviews/{RunId}");
        });
    }

    [Fact]
    public void List_Filters_Reach_The_Api_Query()
    {
        var requested = new List<string>();
        Api(req =>
        {
            requested.Add(req.RequestUri!.ToString());
            return Json("""{"items":[],"next_page_id":null}""");
        });
        var cut = Render<ReviewsPage>();
        cut.WaitForAssertion(() =>
            requested.ShouldContain(u => u.Contains("/scm/reviews")));

        cut.Find("input[aria-label='Filtro de repositório']")
            .Input("octo/hello");
        cut.Find("select[aria-label='Filtro de status']").Change("failed");
        cut.Find(".rev-toolbar button").Click();

        cut.WaitForAssertion(() => requested.ShouldContain(u =>
            u.Contains("repository=octo%2Fhello")
            && u.Contains("status=failed")));
    }

    [Fact]
    public void Detail_Groups_Findings_By_File_With_Severity_And_Stale()
    {
        var open = FindingJson(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "src/Foo.cs", 12, "critical", "bug", "open",
            "https://github.com/octo/hello/pull/42#pullrequestreview-1");
        var stale = FindingJson(
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            "src/Bar.cs", 7, "nit", "style", "stale");
        Api(_ => Json(DetailJson(open, stale)));
        var cut = Render<ReviewDetailPage>(p => p.Add(x => x.Id, RunId));
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("src/Foo.cs");
            cut.Markup.ShouldContain("src/Bar.cs");
            cut.Markup.ShouldContain("src/Foo.cs:L12");
            cut.Markup.ShouldContain("🔴");
            cut.Markup.ShouldContain("rev-finding stale");
            cut.Markup.ShouldContain("pull/42#pullrequestreview-1");
        });
    }

    [Fact]
    public void Detail_Dismiss_Patches_Finding_And_Updates_Badge()
    {
        var fid = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var patched = false;
        Api(req => req.Method == HttpMethod.Patch
            ? (patched = true, Json(FindingJson(
                fid, "src/Foo.cs", 12, "critical", "bug",
                "dismissed"))).Item2
            : Json(DetailJson(FindingJson(
                fid, "src/Foo.cs", 12, "critical", "bug", "open"))));
        var cut = Render<ReviewDetailPage>(p => p.Add(x => x.Id, RunId));
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Descartar"));

        cut.FindAll(".rev-finding-actions button")
            .First(b => b.TextContent.Contains("Descartar")).Click();

        cut.WaitForAssertion(() =>
        {
            patched.ShouldBeTrue();
            cut.Markup.ShouldContain("dismissed");
        });
    }

    [Fact]
    public void Detail_StaleOnly_Toggle_Narrows_To_Stale_Findings()
    {
        var open = FindingJson(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "src/Foo.cs", 12, "critical", "bug", "open");
        var stale = FindingJson(
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            "src/Bar.cs", 7, "nit", "style", "stale");
        Api(_ => Json(DetailJson(open, stale)));
        var cut = Render<ReviewDetailPage>(p => p.Add(x => x.Id, RunId));
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("src/Foo.cs"));

        cut.Find(".rev-stale input[type=checkbox]").Change(true);

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldNotContain("src/Foo.cs:L12");
            cut.Markup.ShouldContain("src/Bar.cs");
        });
    }

    [Fact]
    public void Detail_Bulk_Dismiss_Patches_Each_Selected_Finding()
    {
        var f1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var f2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var patches = 0;
        Api(req =>
        {
            if (req.Method == HttpMethod.Patch)
            {
                patches++;
                var fid = req.RequestUri!.AbsolutePath.Split('/').Last();
                return Json(FindingJson(Guid.Parse(fid),
                    "src/Foo.cs", 1, "nit", "style", "dismissed"));
            }
            return Json(DetailJson(
                FindingJson(f1, "src/Foo.cs", 1, "nit", "style", "open"),
                FindingJson(f2, "src/Foo.cs", 2, "nit", "style", "open")));
        });
        var cut = Render<ReviewDetailPage>(p => p.Add(x => x.Id, RunId));
        cut.WaitForAssertion(() =>
            cut.FindAll(".rev-finding-head input[type=checkbox]")
                .Count.ShouldBe(2));

        // Re-query a cada Change — o toggle re-renderiza e o handler id
        // dos elementos capturados expira (bUnit).
        cut.FindAll(".rev-finding-head input[type=checkbox]")[0]
            .Change(true);
        cut.FindAll(".rev-finding-head input[type=checkbox]")[1]
            .Change(true);
        cut.FindAll(".rev-toolbar button")
            .First(b => b.TextContent.Contains("Descartar selecionados"))
            .Click();

        cut.WaitForAssertion(() => patches.ShouldBe(2));
    }
}