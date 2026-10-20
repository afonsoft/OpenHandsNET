using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Infrastructure.ChatOps;
using OpenHandsNET.Infrastructure.Persistence;
using OpenHandsNET.Infrastructure.Tickets;

using Shouldly;

namespace OpenHandsNET.IntegrationTests.Api;

/// <summary>
/// SPEC-79: rotas <c>/orgs/{org_id}/...</c> devem escopoar o recurso
/// pelo <c>org_id</c> — caller autenticado em org A não opera <c>id</c>
/// de org B (404, sem vazar existência). O recurso de B é semeado
/// direto no DbContext porque o bootstrap só dá org ao primeiro user.
/// </summary>
public sealed class Spec79OrgScopedResourcesTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-spec79-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public Spec79OrgScopedResourcesTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Database:Provider", "Sqlite");
                b.UseSetting("Database:ConnectionString",
                    $"Data Source={_dbPath}");
                b.UseSetting("Observability:OtlpEndpoint", string.Empty);
                b.UseSetting("Sandbox:InstanceOwner",
                    $"test-{Guid.NewGuid():N}");
                b.UseSetting("Auth:Provider", "none");
                b.UseSetting("Auth:UserProvisioningEnabled", "true");
                b.UseSetting("MultiTenancy:Enabled", "true");
            });
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (File.Exists(_dbPath)) { File.Delete(_dbPath); }
    }

    private async Task<(HttpClient http, string orgId)> LoginAsync(
        string email)
    {
        var http = _factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                HandleCookies = true,
            });
        var res = await http.PostAsJsonAsync("/api/v1/auth/dev-login",
            new { email, displayName = email.Split('@')[0] });
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        var auth = await http.PostAsJsonAsync(
            "/api/v1/authenticate", new { });
        var doc = await auth.Content.ReadFromJsonAsync<JsonElement>();
        return (http,
            doc.GetProperty("user").GetProperty("current_org_id")
                .GetString()!);
    }

    private async Task<(Guid orgB, Guid chatB, Guid ticketB)> SeedOrgBAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orgB = new OrganizationRecord
        {
            Id = Guid.NewGuid(),
            Slug = $"org-b-{Guid.NewGuid():N}",
            Name = "Org B",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var chatB = new ChatConnectionRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgB.Id,
            Kind = "slack",
            TeamId = $"T{Guid.NewGuid():N}",
            BotTokenSecretRef = "sec/tok",
            SigningSecretRef = "sec/sig",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var ticketB = new TicketConnectionRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgB.Id,
            Kind = "linear",
            ApiUrl = "https://api.linear.app/graphql",
            TokenSecretRef = "sec/tok",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Organizations.Add(orgB);
        db.ChatConnections.Add(chatB);
        db.TicketConnections.Add(ticketB);
        await db.SaveChangesAsync();
        return (orgB.Id, chatB.Id, ticketB.Id);
    }

    [Fact]
    public async Task Chat_Delete_And_Channels_Scoped_By_Org()
    {
        var (httpA, orgA) = await LoginAsync("org79-a@example.com");
        var (orgB, connB, _) = await SeedOrgBAsync();

        // Cross-org: A autenticada em orgA (perm ok na rota orgA) mas o
        // recurso connB é de orgB → 404 (antes: 204 — apagava!).
        (await httpA.DeleteAsync(
                $"/api/v1/orgs/{orgA}/chat-connections/{connB}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await httpA.GetAsync(
                $"/api/v1/orgs/{orgA}/chat-connections/{connB}/channels"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Recurso de B intacto (verificado no banco).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider
                .GetRequiredService<AppDbContext>();
            db.ChatConnections.ShouldContain(
                c => c.Id == connB && c.OrgId == orgB);
        }
    }

    [Fact]
    public async Task Ticket_Delete_And_Projects_Scoped_By_Org()
    {
        var (httpA, orgA) = await LoginAsync("org79-c@example.com");
        var (orgB, _, connB) = await SeedOrgBAsync();

        (await httpA.DeleteAsync(
                $"/api/v1/orgs/{orgA}/ticket-connections/{connB}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await httpA.GetAsync(
                $"/api/v1/orgs/{orgA}/ticket-connections/{connB}/projects"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await httpA.PostAsJsonAsync(
                $"/api/v1/orgs/{orgA}/ticket-connections/{connB}/projects",
                new
                {
                    projectKey = "ENG",
                    selectedRepository = "org/repo",
                    selectedBranch = (string?)null,
                }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider
                .GetRequiredService<AppDbContext>();
            db.TicketConnections.ShouldContain(
                c => c.Id == connB && c.OrgId == orgB);
        }
    }
}