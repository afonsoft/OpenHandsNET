using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using OpenHandsNET.Infrastructure.Identity.Provisioning;
using OpenHandsNET.Infrastructure.Persistence;

using Shouldly;

namespace OpenHandsNET.IntegrationTests.Identity;

/// <summary>
/// SPEC-08 RF-007: provisioning failure after broker creation must run the
/// compensating rollback — broker user deleted + local rows removed.
/// </summary>
public sealed class ProvisioningRollbackTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-rollback-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly FailAfterCreateBroker _broker = new();

    public ProvisioningRollbackTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath}");
            builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
            builder.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}");
            builder.UseSetting("Auth:Provider", "none");
            builder.UseSetting("Auth:UserProvisioningEnabled", "true");
            builder.ConfigureServices(services =>
                services.Replace(ServiceDescriptor
                    .Singleton<IBrokerUserAdmin>(_broker)));
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    /// <summary>Broker that succeeds on create but breaks the API-key step afterwards.</summary>
    private sealed class FailAfterCreateBroker : IBrokerUserAdmin
    {
        public int CreatedCount;
        public int DeletedCount;
        public string? LastDeleted;

        public Task<string> CreateUserAsync(
            string email, string? name, string? password,
            CancellationToken cancellationToken = default)
        {
            CreatedCount++;
            return Task.FromResult("broker-fail-" + email);
        }

        public Task DeleteUserAsync(
            string brokerUserId, CancellationToken cancellationToken = default)
        {
            DeletedCount++;
            LastDeleted = brokerUserId;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Provision_Failure_Rolls_Back_Broker_And_Local_Rows()
    {
        var http = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await http.PostAsJsonAsync("/api/v1/auth/dev-login",
            new { email = "root@example.com" });
        login.StatusCode.ShouldBe(HttpStatusCode.OK);

        var orgs = await http.GetFromJsonAsync<JsonElement>("/api/v1/organizations");
        var orgId = orgs![0].GetProperty("id").GetGuid();

        // Force failure AFTER the local user exists: a second org member row
        // with the same PK can't happen — instead fail the api-key step by
        // removing the org first so the FK/lookup breaks on a missing org.
        // Simpler deterministic failure: delete the org's membership row for
        // the would-be provisioned user is not possible pre-creation, so we
        // drop the org record right before provisioning is exercised: the
        // service loads the org first, so instead we corrupt quota columns.
        // Deterministic approach: make the user INSERT collide by pre-seeding
        // a user row with the same email (unique index) — the SaveChanges
        // after broker creation throws → rollback path executes.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(new UserRecord
            {
                Id = Guid.NewGuid(),
                Email = "conflict@example.com",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var res = await http.PostAsJsonAsync(
            "/api/v1/organizations/provision-user", new
            {
                email = "conflict@example.com",
                name = "Conflict",
                org_id = orgId,
                role = "member",
                generate_password = false,
            });
        // Provisioning threw → 500; compensations must have run.
        res.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        _broker.CreatedCount.ShouldBe(1);
        _broker.DeletedCount.ShouldBe(1);
        _broker.LastDeleted.ShouldBe("broker-fail-conflict@example.com");

        // Local state: only the pre-seeded user remains (no extra member row).
        await using var verify = _factory.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = await verifyDb.Users
            .Where(u => u.Email == "conflict@example.com").ToListAsync();
        users.Count.ShouldBe(1);
        (await verifyDb.OrgMembers
            .AnyAsync(m => m.UserId == users[0].Id
                && m.OrgId == orgId)).ShouldBeFalse();
    }
}