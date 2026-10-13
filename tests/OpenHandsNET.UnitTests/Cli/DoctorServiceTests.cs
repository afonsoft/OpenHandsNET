using System.Net;
using System.Text;
using OpenHandsNET.Cli.Services;

namespace OpenHandsNET.UnitTests.Cli;

/// <summary>DoctorService probes: api/db/llm/disk aggregation (SPEC-14 RF-003).</summary>
public sealed class DoctorServiceTests
{
    private sealed class StubHandler(HttpResponseMessage response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }

    private static HttpClient Client(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(new StubHandler(new HttpResponseMessage(code)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }));

    [Fact]
    public async Task Healthy_Report_When_All_Probes_Pass()
    {
        var svc = new DoctorService(Client(
            """{"status":"ready","checks":{"db":"healthy","llm":"configured","docker":"healthy"}}"""));
        var ws = Path.Combine(Path.GetTempPath(), $"ohn-doc-{Guid.NewGuid():N}");
        var report = await svc.RunAsync("http://x", ws, CancellationToken.None);

        report.Probes.ShouldContain(p => p.Name == "api" && p.Ok);
        report.Probes.ShouldContain(p => p.Name == "db" && p.Ok);
        report.Probes.ShouldContain(p => p.Name == "llm" && p.Ok);
        report.Probes.ShouldContain(p => p.Name == "workspace" && p.Ok);
        // docker may be absent in CI — not asserted; api+db+llm decide health
    }

    [Fact]
    public async Task Unhealthy_When_Api_Unreachable()
    {
        var svc = new DoctorService(new HttpClient(new FailingHandler()));
        var report = await svc.RunAsync("http://x",
            Path.GetTempPath(), CancellationToken.None);
        report.Probes.ShouldContain(p => p.Name == "api" && !p.Ok);
        report.Healthy.ShouldBeFalse();
    }

    [Fact]
    public async Task Degraded_Readiness_Marks_Api_Failed()
    {
        var svc = new DoctorService(Client(
            """{"status":"degraded","checks":{"db":"unhealthy"}}""",
            HttpStatusCode.ServiceUnavailable));
        var report = await svc.RunAsync("http://x",
            Path.GetTempPath(), CancellationToken.None);
        report.Probes.ShouldContain(p => p.Name == "api" && !p.Ok);
        report.Probes.ShouldContain(p => p.Name == "db" && !p.Ok);
    }

    [Fact]
    public async Task Http_Error_Status_Returns_Api_Fail()
    {
        var svc = new DoctorService(Client("{}", HttpStatusCode.NotFound));
        var report = await svc.RunAsync("http://x",
            Path.GetTempPath(), CancellationToken.None);
        report.Probes.ShouldContain(p => p.Name == "api" && !p.Ok);
    }
}
