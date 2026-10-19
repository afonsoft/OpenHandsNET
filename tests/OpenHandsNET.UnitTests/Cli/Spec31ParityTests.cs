using System.Net;
using System.Text;
using System.Text.Json;

using OpenHandsNET.Cli.Commands;
using OpenHandsNET.Cli.Services;

using Shouldly;

namespace OpenHandsNET.UnitTests.Cli;

/// <summary>
/// SPEC-31 AC: keymap tui.json, export JSONL paginado + redact,
/// doctor --preflight probes, API client dos comandos novos.
/// </summary>
public sealed class Spec31ParityTests
{
    // ---------- TuiKeymap ----------

    [Fact]
    public async Task Keymap_Loads_Aliases_From_Tui_Json()
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"tui-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path,
            """{"aliases":{"/q":"/exit","/s":"/status"}}""");
        var km = await TuiKeymap.LoadAsync(path);
        km.Resolve("/q").ShouldBe("/exit");
        km.Resolve("/s").ShouldBe("/status");
        km.Resolve("/new").ShouldBe("/new");
        km.Aliases.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Keymap_Invalid_Or_Missing_Is_No_Op()
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"tui-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "{not json");
        (await TuiKeymap.LoadAsync(path)).Aliases.ShouldBeEmpty();
        (await TuiKeymap.LoadAsync(path + ".missing"))
            .Aliases.ShouldBeEmpty();
    }

    // ---------- ExportCommand.Redact ----------

    [Fact]
    public void Export_Redact_Masks_Secret_Placeholders()
    {
        ExportCommand.Redact("token={{secret:GH_TOKEN}} done")
            .ShouldBe("token=*** done");
        ExportCommand.Redact("plain").ShouldBe("plain");
    }

    // ---------- CloudApiClient ----------

    [Fact]
    public async Task ExportEvents_Pages_Through_Cursor()
    {
        var handler = new PagingHandler();
        var api = new CloudApiClient(
            new HttpClient(handler), "http://x");
        var ids = new List<string>();
        await foreach (var e in api.ExportEventsAsync("c1"))
        {
            ids.Add(e.GetProperty("id").GetString()!);
        }

        ids.ShouldBe(["e1", "e2", "e3"]);
        handler.Requests.ShouldBe(2); // 2 páginas
    }

    private sealed class PagingHandler : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var page2 = request.RequestUri!.Query.Contains("page_id=abc");
            var body = page2
                ? """{"items":[{"id":"e3","kind":"MessageEvent"}],"next_page_id":null}"""
                : """{"items":[{"id":"e1","kind":"MessageEvent"},{"id":"e2","kind":"MessageEvent"}],"next_page_id":"abc"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8,
                    "application/json"),
            });
        }
    }

    // ---------- DoctorService --preflight ----------

    [Fact]
    public async Task Preflight_Runs_Install_Probes()
    {
        var svc = new DoctorService(new HttpClient(new NullHandler()));
        var ws = Path.Combine(Path.GetTempPath(),
            $"ohn-pre-{Guid.NewGuid():N}");
        var report = await svc.RunPreflightAsync(
            "http://x", ws, CancellationToken.None);
        var names = report.Probes.Select(p => p.Name).ToList();
        names.ShouldContain("dns_public_domain");
        names.ShouldContain("egress_registry-1.docker.io");
        names.ShouldContain("egress_ghcr.io");
        names.ShouldContain("docker_daemon");
        names.ShouldContain("cgroup_v2");
        names.ShouldContain("disk_50gb");
        names.ShouldContain("port_8080");
        names.ShouldContain("port_30000");
        // DNS sem OPENHANDSNET_PUBLIC_DOMAIN → skip saudável.
        report.Probes.First(p => p.Name == "dns_public_domain")
            .Ok.ShouldBeTrue();
    }

    private sealed class NullHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"status":"ready","checks":{}}""", Encoding.UTF8,
                    "application/json"),
            });
    }
}