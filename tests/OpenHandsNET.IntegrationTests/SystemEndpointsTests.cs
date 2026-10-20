using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenHandsNET.IntegrationTests;

/// <summary>
/// Smoke tests for the SPEC-01 system endpoints (§5 API Contract).
/// </summary>
public sealed class SystemEndpointsTests : IClassFixture<SystemEndpointsTests.AppFactory>
{
    private readonly HttpClient _client;

    public SystemEndpointsTests(AppFactory factory)
    {
        _client = factory.CreateClient();
    }

    // Covers AC §5: GET /api/v1/health returns 200 Healthy.
    [Fact]
    public async Task Health_Returns_Healthy()
    {
        var response = await _client.GetAsync("/api/v1/health");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe("Healthy");
    }

    // Covers AC §5: GET /api/v1/ready checks database connectivity.
    [Fact]
    public async Task Ready_Returns_Healthy_When_Database_Up()
    {
        var response = await _client.GetAsync("/api/v1/ready");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // Covers AC §5: GET /api/v1/config exposes version + deployment mode.
    [Fact]
    public async Task Config_Returns_Version_And_DeploymentMode()
    {
        var config = await _client.GetFromJsonAsync<
            System.Text.Json.JsonElement>("/api/v1/config");
        config.GetProperty("version").GetString().ShouldNotBeNullOrEmpty();
        config.GetProperty("deployment_mode").GetString().ShouldBe("oss");
    }

    // Covers RF-003: OPENHANDSNET_* env overrides bind into options.
    [Fact]
    public async Task Config_Reflects_Env_Override()
    {
        await using var factory = new AppFactory
        {
            Extra = new Dictionary<string, string?>
            {
                ["DeploymentMode"] = "self_hosted",
            },
        };
        var client = factory.CreateClient();
        var config = await client.GetFromJsonAsync<
            System.Text.Json.JsonElement>("/api/v1/config");
        config.GetProperty("deployment_mode").GetString().ShouldBe("self_hosted");
        config.GetProperty("features").GetProperty("multi_tenancy")
            .GetBoolean().ShouldBeTrue();
    }

    // Provider kubernetes não tem daemon docker no pod — /api/v1/readiness
    // reporta docker como n/a e não degrada para 503 (k8s-e2e rollout).
    [Fact]
    public async Task Readiness_Does_Not_Require_Docker_When_Kubernetes()
    {
        // O engine k8s resolve eager no startup (warm pool) e exige um
        // kubeconfig parseável fora de cluster.
        var kubeconfig = Path.Combine(
            Path.GetTempPath(), $"ohn-kubeconfig-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(kubeconfig, """
            apiVersion: v1
            kind: Config
            clusters:
            - name: test
              cluster:
                server: https://127.0.0.1:6443
            contexts:
            - name: test
              context:
                cluster: test
                user: test
            current-context: test
            users:
            - name: test
              user:
                token: test
            """);
        await using var factory = new AppFactory
        {
            Extra = new Dictionary<string, string?>
            {
                ["Sandbox:Provider"] = "kubernetes",
                ["Sandbox:Kubernetes:KubeconfigPath"] = kubeconfig,
            },
        };
        var client = factory.CreateClient();
        var res = await client.GetAsync("/api/v1/readiness");
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await res.Content
            .ReadFromJsonAsync<System.Text.Json.JsonElement>();
        json.GetProperty("status").GetString().ShouldBe("ready");
        json.GetProperty("checks").GetProperty("docker").GetString()
            .ShouldBe("n/a (provider=kubernetes)");
    }

    /// <summary>Factory that points the app at a throwaway SQLite file.</summary>
    public class AppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"openhandsnet-test-{Guid.NewGuid():N}.db");
        /// <summary>Extra configuration settings merged into the app configuration.</summary>
        public Dictionary<string, string?>? Extra { get; init; }

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            // UseSetting (host config) overrides appsettings in minimal-hosting apps;
            // ConfigureAppConfiguration sources added here would run BEFORE the app
            // loads its own appsettings and lose precedence.
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath}");
            builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
            if (Extra is not null)
            {
                foreach (var kv in Extra)
                {
                    builder.UseSetting(kv.Key, kv.Value);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
    }
}