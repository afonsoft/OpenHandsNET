using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenHandsNET.Infrastructure.Conversations;
using OpenHandsNET.Infrastructure.Persistence;
using OpenHandsNET.Infrastructure.Sandbox;

namespace OpenHandsNET.IntegrationTests;

/// <summary>
/// SPEC-14 sandbox hardening over a real daemon: profile-driven limits,
/// egress network isolation, conversation→org→global resolution and the
/// warm-pool claim path.
/// </summary>
public sealed class Spec14SandboxTests : IAsyncLifetime
{
    private const string Image = "bash:5";
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-spec14-{Guid.NewGuid():N}.db");
    private readonly string _wsRoot = Path.Combine(
        Path.GetTempPath(), $"ohn-ws14-{Guid.NewGuid():N}");
    private readonly string _owner = $"test-spec14-{Guid.NewGuid():N}"[..24];

    private AppDbContext _db = null!;
    private IDockerClient _docker = null!;
    private bool _dockerUp;

    public async Task InitializeAsync()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .UseSnakeCaseNamingConvention().Options);
        await _db.Database.MigrateAsync();
        try
        {
            _docker = DockerClientFactory.Create();
            await _docker.System.PingAsync();
            _dockerUp = true;
        }
        catch { return; }
    }

    public async Task DisposeAsync()
    {
        _db?.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (Directory.Exists(_wsRoot)) Directory.Delete(_wsRoot, recursive: true);
        await Task.CompletedTask;
    }

    private DockerSandboxService MakeService(
        SandboxOptions? overrides = null, WarmPoolService? pool = null)
    {
        var opts = Options.Create(overrides ?? new SandboxOptions
        {
            WorkspaceRoot = _wsRoot, InstanceOwner = _owner,
        });
        var provisioner = new WorkspaceProvisioner(opts,
            NullLogger<WorkspaceProvisioner>.Instance);
        var egress = new DockerNetworkPolicy(_docker, opts,
            NullLogger<DockerNetworkPolicy>.Instance);
        pool ??= new WarmPoolService(_docker, opts, provisioner,
            NewScopeFactory(),
            NullLogger<WarmPoolService>.Instance);
        return new DockerSandboxService(_db, _docker, provisioner,
            egress, pool, opts, NullLogger<DockerSandboxService>.Instance);
    }

    /// <summary>Real per-scope DbContexts — the provider owns their disposal.</summary>
    private IServiceScopeFactory NewScopeFactory() =>
        new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o
                .UseSqlite($"Data Source={_dbPath}")
                .UseSnakeCaseNamingConvention())
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

    private async Task<Guid> SeedConversationAsync(
        string? profile = null, Guid? orgId = null)
    {
        var conv = new ConversationRecord
        {
            Id = Guid.NewGuid(), OrgId = orgId, SandboxProfile = profile,
            ExecutionStatus = "Starting",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db.Conversations.Add(conv);
        await _db.SaveChangesAsync();
        return conv.Id;
    }

    [Fact]
    public async Task Conversation_Profile_Small_Applies_Small_Limits()
    {
        if (!_dockerUp) return;
        var convId = await SeedConversationAsync(profile: "small");
        var svc = MakeService();
        var sb = await svc.StartAsync(Guid.Empty, convId, CancellationToken.None);
        try
        {
            var inspect = await _docker.Containers.InspectContainerAsync(sb.ContainerId!);
            inspect.HostConfig!.Memory.ShouldBe(2048L * 1024 * 1024);
            inspect.HostConfig.NanoCPUs.ShouldBe(1_000_000_000L);
            inspect.HostConfig.PidsLimit.ShouldBe(256);
        }
        finally { await svc.DeleteAsync(sb.Id, CancellationToken.None); }
    }

    [Fact]
    public async Task Org_Settings_Profile_Used_When_Conversation_Unset()
    {
        if (!_dockerUp) return;
        var org = new OrganizationRecord
        {
            Id = Guid.NewGuid(), Name = "BigOrg", Slug = "big",
            SettingsJson = """{"sandbox_profile":"large"}""",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.Organizations.Add(org);
        var convId = await SeedConversationAsync(profile: null, orgId: org.Id);
        await _db.SaveChangesAsync();

        var svc = MakeService();
        var sb = await svc.StartAsync(Guid.Empty, convId, CancellationToken.None);
        try
        {
            var inspect = await _docker.Containers.InspectContainerAsync(sb.ContainerId!);
            inspect.HostConfig!.Memory.ShouldBe(8192L * 1024 * 1024);
            inspect.HostConfig.PidsLimit.ShouldBe(1024);
        }
        finally { await svc.DeleteAsync(sb.Id, CancellationToken.None); }
    }

    [Fact]
    public async Task Conversation_Profile_Beats_Org_And_Global()
    {
        if (!_dockerUp) return;
        var org = new OrganizationRecord
        {
            Id = Guid.NewGuid(), Name = "Org2", Slug = "org2",
            SettingsJson = """{"sandbox_profile":"large"}""",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.Organizations.Add(org);
        // conversation says small → wins over org large + global medium
        var convId = await SeedConversationAsync(profile: "small", orgId: org.Id);
        await _db.SaveChangesAsync();

        var svc = MakeService(new SandboxOptions
        {
            WorkspaceRoot = _wsRoot, InstanceOwner = _owner, Profile = "medium",
        });
        var sb = await svc.StartAsync(Guid.Empty, convId, CancellationToken.None);
        try
        {
            var inspect = await _docker.Containers.InspectContainerAsync(sb.ContainerId!);
            inspect.HostConfig!.Memory.ShouldBe(2048L * 1024 * 1024);
        }
        finally { await svc.DeleteAsync(sb.Id, CancellationToken.None); }
    }

    [Fact]
    public async Task Egress_Deny_Attaches_Internal_Network()
    {
        if (!_dockerUp) return;
        var convId = await SeedConversationAsync();
        var svc = MakeService(new SandboxOptions
        {
            WorkspaceRoot = _wsRoot, InstanceOwner = _owner,
            EgressMode = "deny", DefaultImage = Image,
        });
        // spec default image must exist locally; use bash:5 via custom spec
        var spec = new OpenHandsNET.Domain.Sandbox.SandboxSpec(
            Guid.NewGuid(), "egress-test", Image, MemoryMb: 64,
            MountWorkspace: false);
        await svc.UpsertSpecAsync(spec, CancellationToken.None);

        var sb = await svc.StartAsync(spec.Id, convId, CancellationToken.None);
        try
        {
            var inspect = await _docker.Containers.InspectContainerAsync(sb.ContainerId!);
            inspect.NetworkSettings!.Networks!.Keys.ShouldContain(
                DockerNetworkPolicy.InternalNetworkName);
            inspect.Config!.Env!.ShouldContain("EGRESS_MODE=deny");
        }
        finally { await svc.DeleteAsync(sb.Id, CancellationToken.None); }
    }

    [Fact]
    public async Task Warm_Pool_Claim_Attaches_Running_Container()
    {
        if (!_dockerUp) return;
        var opts = new SandboxOptions
        {
            WorkspaceRoot = _wsRoot, InstanceOwner = _owner,
            WarmPoolSize = 1, DefaultImage = Image,
        };
        var optWrap = Options.Create(opts);
        var provisioner = new WorkspaceProvisioner(optWrap,
            NullLogger<WorkspaceProvisioner>.Instance);
        var pool = new WarmPoolService(_docker, optWrap, provisioner,
            NewScopeFactory(),
            NullLogger<WarmPoolService>.Instance);

        // Drive the pool manually — ExecuteAsync's initial fill, minus the loop.
        await pool.StartAsync(CancellationToken.None);
        try
        {
            // wait until a free warm container exists (pull may take a moment)
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (await pool.FreeCountAsync(CancellationToken.None) < 1)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("warm pool never filled");
                }
                await Task.Delay(1000);
            }

            var convId = await SeedConversationAsync();
            var svc = new DockerSandboxService(_db, _docker, provisioner,
                new DockerNetworkPolicy(_docker, optWrap,
                    NullLogger<DockerNetworkPolicy>.Instance),
                pool, optWrap, NullLogger<DockerSandboxService>.Instance);
            var sb = await svc.StartAsync(
                Guid.Empty, convId, CancellationToken.None);
            try
            {
                sb.Pool.ShouldBe("warm");
                sb.ContainerId.ShouldNotBeNull();
                var inspect = await _docker.Containers
                    .InspectContainerAsync(sb.ContainerId!);
                inspect.State!.Running.ShouldBeTrue();
                inspect.Mounts.Any(m =>
                    m.Destination == "/workspace").ShouldBeTrue();
                // The pre-mounted warm dir was renamed to the conversation
                // path — writes inside the container must land there.
                var executor = new DockerSandboxExecutor(_docker);
                var w = await executor.ExecAsync(sb,
                    "echo warm-ok > /workspace/marker.txt",
                    TimeSpan.FromSeconds(15), CancellationToken.None);
                w.ExitCode.ShouldBe(0);
                File.ReadAllText(Path.Combine(
                    _wsRoot, convId.ToString("N"), "marker.txt"))
                    .Trim().ShouldBe("warm-ok");
            }
            finally { await svc.DeleteAsync(sb.Id, CancellationToken.None); }
        }
        finally
        {
            await pool.StopAsync(CancellationToken.None);
        }
    }
}
