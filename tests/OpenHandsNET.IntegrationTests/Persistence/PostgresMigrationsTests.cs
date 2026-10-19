using Docker.DotNet;
using Docker.DotNet.Models;

using Microsoft.EntityFrameworkCore;

using OpenHandsNET.Infrastructure.Persistence;
using OpenHandsNET.Infrastructure.Sandbox;
using OpenHandsNET.Sandboxing;

using Shouldly;

namespace OpenHandsNET.IntegrationTests.Persistence;

/// <summary>
/// SPEC-10 AC: migrações PostgreSQL aplicam do zero num container real
/// (Testcontainers não é usado: incompatível com Docker.DotNet.Enhanced —
/// o Postgres é levantado via DockerClient direto). Skip sem daemon.
/// </summary>
public sealed class PostgresMigrationsTests : IAsyncLifetime
{
    private const string Image = "postgres:17-alpine";
    private IDockerClient? _client;
    private string? _containerId;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        if (!DockerAvailable())
        {
            return;
        }

        _client = DockerClientFactory.Create();
        var password = "openhandsnet-test";
        await _client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = Image },
            new AuthConfig(), new Progress<JSONMessage>());
        var created = await _client.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Image = Image,
                Env = [$"POSTGRES_PASSWORD={password}", "POSTGRES_DB=openhandsnet"],
                HostConfig = new HostConfig
                {
                    PublishAllPorts = true,
                    AutoRemove = false,
                },
            });
        _containerId = created.ID;
        await _client.Containers.StartContainerAsync(
            _containerId, new ContainerStartParameters());

        var inspect = await _client.Containers.InspectContainerAsync(_containerId);
        var ports = inspect.NetworkSettings?.Ports
            ?? throw new InvalidOperationException("no published ports");
        var port = ports["5432/tcp"][0].HostPort;
        _connectionString =
            $"Host=localhost;Port={port};Database=openhandsnet;" +
            $"Username=postgres;Password={password}";

        // Espera o postgres aceitar conexões.
        for (var i = 0; i < 60; i++)
        {
            try
            {
                await using var db = NewContext();
                await db.Database.GetDbConnection().OpenAsync();
                return;
            }
            catch
            {
                await Task.Delay(1000);
            }
        }

        throw new InvalidOperationException("postgres container did not become ready");
    }

    public async Task DisposeAsync()
    {
        if (_client is not null && _containerId is not null)
        {
            try
            {
                await _client.Containers.RemoveContainerAsync(
                    _containerId, new ContainerRemoveParameters { Force = true });
            }
            catch (DockerApiException)
            {
            }
        }

        _client?.Dispose();
    }

    [Fact]
    public async Task Postgres_Migrations_ApplyFullSchema()
    {
        if (_client is null)
        {
            return;
        }

        await using var db = NewContext();
        await db.Database.MigrateAsync();

        var tables = await db.Database.SqlQuery<string>(
            $"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'")
            .ToListAsync();
        foreach (var t in new[]
        {
            "conversations", "events", "secrets", "users", "api_keys",
            "organizations", "org_members", "audit_events",
            "data_protection_keys", "pending_messages",
        })
        {
            tables.ShouldContain(t);
        }

        var org = new OrganizationRecord
        {
            Id = Guid.NewGuid(),
            Slug = "acme",
            Name = "Acme",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Organizations.Add(org);
        await db.SaveChangesAsync();
        (await db.Organizations.AsNoTracking().SingleAsync(o => o.Slug == "acme"))
            .Name.ShouldBe("Acme");
    }

    private PostgresDbContext NewContext() => new(
        new DbContextOptionsBuilder<PostgresDbContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options);

    private static bool DockerAvailable()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("docker", "info")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                })!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}