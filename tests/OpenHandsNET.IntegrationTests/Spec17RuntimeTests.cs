using Docker.DotNet;
using Docker.DotNet.Models;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using OpenHandsNET.Infrastructure.Conversations;
using OpenHandsNET.Infrastructure.Persistence;
using OpenHandsNET.Infrastructure.Sandbox;
using OpenHandsNET.Sandboxing;

namespace OpenHandsNET.IntegrationTests;

/// <summary>
/// SPEC-17 over a real docker daemon: agent-image startup env (session key,
/// deferred init, no sleep-infinity), protected session key at rest, dormant
/// warm pool per image, and blueprint execution inside the container.
/// <c>bash:5</c> stands in for the agent image via <c>AgentImagePrefixes</c> —
/// assertions are on create-time config/labels, so the container not actually
/// running an agent is irrelevant.
/// </summary>
public sealed class Spec17RuntimeTests : IAsyncLifetime
{
    private const string Image = "bash:5";
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-spec17-{Guid.NewGuid():N}.db");
    private readonly string _wsRoot = Path.Combine(
        Path.GetTempPath(), $"ohn-ws17-{Guid.NewGuid():N}");
    private readonly string _owner = $"test-spec17-{Guid.NewGuid():N}"[..24];
    private readonly EphemeralDataProtectionProvider _dp = new();

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
            await PullAsync(Image);
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

    private async Task PullAsync(string image)
    {
        try
        {
            await _docker.Images.CreateImageAsync(new ImagesCreateParameters
            {
                FromImage = image.Split(':')[0],
                Tag = image.Split(':')[1],
            }, null, new Progress<JSONMessage>());
        }
        catch (DockerApiException) { /* já existe */ }
    }

    private IServiceScopeFactory NewScopeFactory() =>
        new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o
                .UseSqlite($"Data Source={_dbPath}")
                .UseSnakeCaseNamingConvention())
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

    private SandboxService MakeService(
        SandboxOptions? overrides = null, WarmPoolService? pool = null,
        SandboxAgentClient? agent = null)
    {
        var opts = Options.Create(overrides ?? new SandboxOptions
        {
            WorkspaceRoot = _wsRoot,
            InstanceOwner = _owner,
            DefaultImage = Image,
        });
        var provisioner = new WorkspaceProvisioner(opts,
            NullLogger<WorkspaceProvisioner>.Instance);
        var egress = new DockerNetworkPolicy(_docker, opts,
            NullLogger<DockerNetworkPolicy>.Instance);
        var engine = new DockerSandboxEngine(_docker, provisioner, egress,
            opts, NullLogger<DockerSandboxEngine>.Instance);
        pool ??= new WarmPoolService(engine, opts,
            new OpenHandsNET.Application.Coordination.NullDistributedLock(), NullLogger<WarmPoolService>.Instance);
        return new SandboxService(_db, engine, opts,
            NullLogger<SandboxService>.Instance,
            dp: _dp, agentClient: agent);
    }

    private async Task<Guid> SeedConversationAsync()
    {
        var conv = new ConversationRecord
        {
            Id = Guid.NewGuid(),
            ExecutionStatus = "Starting",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db.Conversations.Add(conv);
        await _db.SaveChangesAsync();
        return conv.Id;
    }

    [Fact]
    public async Task Agent_Image_Start_Sets_Agent_Env_And_No_Sleep_Cmd()
    {
        if (!_dockerUp) return;
        var convId = await SeedConversationAsync();
        var svc = MakeService(new SandboxOptions
        {
            WorkspaceRoot = _wsRoot,
            InstanceOwner = _owner,
            AgentImagePrefixes = "bash",
        });
        var spec = new Domain.Sandbox.SandboxSpec(
            Guid.NewGuid(), "agent-img", Image, MemoryMb: 64,
            MountWorkspace: false);
        await svc.UpsertSpecAsync(spec, CancellationToken.None);

        var sb = await svc.StartAsync(spec.Id, convId, CancellationToken.None);
        try
        {
            var inspect = await _docker.Containers
                .InspectContainerAsync(sb.ContainerId!);
            // Agente é o entrypoint — sem sleep infinity.
            (inspect.Config!.Cmd is null || !inspect.Config.Cmd
                .SequenceEqual(new[] { "sleep", "infinity" })).ShouldBeTrue();
            inspect.Config.Env!.ShouldContain(e => e.StartsWith("OHN_SESSION_KEY="));
            inspect.Config.Env.ShouldContain("OHN_DEFERRED_INIT=false");
            inspect.Config.Env.ShouldContain("OHN_WORKSPACE=/workspace");

            // Session key protegida at-rest — nunca plaintext.
            var rec = await _db.Sandboxes.FindAsync(sb.Id);
            rec!.SessionKeyProtected.ShouldNotBeNull();
            var envKey = inspect.Config.Env
                .First(e => e.StartsWith("OHN_SESSION_KEY=")).Split('=', 2)[1];
            rec.SessionKeyProtected.ShouldNotBe(envKey);
            _dp.CreateProtector("openhandsnet.sandbox.session")
                .Unprotect(rec.SessionKeyProtected).ShouldBe(envKey);
        }
        finally { await svc.DeleteAsync(sb.Id, CancellationToken.None); }
    }

    [Fact]
    public async Task Legacy_Image_Keeps_Sleep_Cmd_And_No_Agent_Env()
    {
        if (!_dockerUp) return;
        var convId = await SeedConversationAsync();
        var svc = MakeService();
        var spec = new Domain.Sandbox.SandboxSpec(
            Guid.NewGuid(), "legacy-img", Image, MemoryMb: 64,
            MountWorkspace: false);
        await svc.UpsertSpecAsync(spec, CancellationToken.None);

        var sb = await svc.StartAsync(spec.Id, convId, CancellationToken.None);
        try
        {
            var inspect = await _docker.Containers
                .InspectContainerAsync(sb.ContainerId!);
            inspect.Config!.Cmd.ShouldBe(new[] { "sleep", "infinity" });
            (inspect.Config.Env ?? []).ShouldNotContain(
                e => e.StartsWith("OHN_SESSION_KEY="));
            (await _db.Sandboxes.FindAsync(sb.Id))!
                .SessionKeyProtected.ShouldBeNull();
        }
        finally { await svc.DeleteAsync(sb.Id, CancellationToken.None); }
    }

    [Fact]
    public async Task Warm_Pool_Agent_Image_Creates_Dormant_Container()
    {
        if (!_dockerUp) return;
        var opts = new SandboxOptions
        {
            WorkspaceRoot = _wsRoot,
            InstanceOwner = _owner,
            AgentImagePrefixes = "bash",
            WarmPoolImages = $"{Image}:1",
            DefaultImage = Image,
        };
        var optWrap = Options.Create(opts);
        var provisioner = new WorkspaceProvisioner(optWrap,
            NullLogger<WorkspaceProvisioner>.Instance);
        var engine = new DockerSandboxEngine(_docker, provisioner,
            new DockerNetworkPolicy(_docker, optWrap,
                NullLogger<DockerNetworkPolicy>.Instance),
            optWrap, NullLogger<DockerSandboxEngine>.Instance);
        var pool = new WarmPoolService(engine, optWrap,
            new OpenHandsNET.Application.Coordination.NullDistributedLock(), NullLogger<WarmPoolService>.Instance);

        await pool.StartAsync(CancellationToken.None);
        string? claimedId = null;
        try
        {
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (await pool.FreeCountAsync(Image, CancellationToken.None) < 1)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("warm pool never filled");
                }
                await Task.Delay(1000);
            }

            // dormant labels + env no container warm
            var warm = (await _docker.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["label"] = new Dictionary<string, bool>
                        {
                            [$"openhandsnet.owner={_owner}"] = true,
                            ["openhandsnet.pool=warm"] = true,
                        },
                    },
                })).Single(c => c.Labels["openhandsnet.pool_key"] == Image);
            var inspect = await _docker.Containers
                .InspectContainerAsync(warm.ID);
            inspect.Config!.Labels.ShouldContainKey("openhandsnet.init_key");
            inspect.Config.Env!.ShouldContain("OHN_DEFERRED_INIT=true");
            (inspect.Config.Cmd is null || !inspect.Config.Cmd
                .SequenceEqual(new[] { "sleep", "infinity" })).ShouldBeTrue();

            // claim → dormant activation tenta /api/init (falha: bash não é
            // agente de verdade) → segue sem AgentUrl mas claimed.
            var convId = await SeedConversationAsync();
            var agent = new SandboxAgentClient(new SimpleHttpFactory(),
                NullLogger<SandboxAgentClient>.Instance);
            var svc = MakeService(opts, pool, agent);
            var sb = await svc.StartAsync(
                Guid.Empty, convId, CancellationToken.None);
            claimedId = sb.ContainerId;
            sb.Pool.ShouldBe("warm");
            sb.PoolKey.ShouldBe(Image);
        }
        finally
        {
            await pool.StopAsync(CancellationToken.None);
            if (claimedId is not null)
            {
                try
                {
                    await _docker.Containers.RemoveContainerAsync(claimedId,
                        new ContainerRemoveParameters { Force = true });
                }
                catch (DockerApiException) { }
            }
        }
    }

    [Fact]
    public async Task Blueprint_SetupScript_Runs_And_Records_Ok()
    {
        if (!_dockerUp) return;
        var convId = await SeedConversationAsync();
        // repo fake no workspace host (bind-mount do container).
        var repoDir = Path.Combine(_wsRoot, convId.ToString("N"), "repo");
        Directory.CreateDirectory(Path.Combine(repoDir, ".openhands"));
        File.WriteAllText(Path.Combine(repoDir, ".openhands", "setup.sh"),
            "echo blueprint-ran > /workspace/repo/.marker\n");

        var opts = new SandboxOptions
        {
            WorkspaceRoot = _wsRoot,
            InstanceOwner = _owner,
            DefaultImage = Image,
        };
        var svc = MakeService(opts);
        var sb = await svc.StartAsync(Guid.Empty, convId, CancellationToken.None);
        try
        {
            var blueprint = new BlueprintService(
                new WorkspaceProvisioner(Options.Create(opts),
                    NullLogger<WorkspaceProvisioner>.Instance),
                Options.Create(opts), _db,
                NullLogger<BlueprintService>.Instance);
            var spec = await blueprint.ResolveAsync(convId, null, CancellationToken.None);
            spec.SetupScript.ShouldBe(".openhands/setup.sh");

            var executor = new DockerSandboxExecutor(_docker);
            await blueprint.RunAsync(sb, convId, spec, executor,
                CancellationToken.None);

            var rec = await _db.Sandboxes.FindAsync(sb.Id);
            rec!.BlueprintStatus.ShouldBe("ok");
            rec.BlueprintLogTail!.ShouldContain("setup.sh");
            File.Exists(Path.Combine(repoDir, ".marker")).ShouldBeTrue();
        }
        finally { await svc.DeleteAsync(sb.Id, CancellationToken.None); }
    }

    [Fact]
    public async Task Blueprint_Failing_Script_Records_Failed_Tail()
    {
        if (!_dockerUp) return;
        var convId = await SeedConversationAsync();
        var repoDir = Path.Combine(_wsRoot, convId.ToString("N"), "repo");
        Directory.CreateDirectory(Path.Combine(repoDir, ".openhands"));
        File.WriteAllText(Path.Combine(repoDir, ".openhands", "setup.sh"),
            "echo oops >&2\nexit 3\n");

        var opts = new SandboxOptions
        {
            WorkspaceRoot = _wsRoot,
            InstanceOwner = _owner,
            DefaultImage = Image,
        };
        var svc = MakeService(opts);
        var sb = await svc.StartAsync(Guid.Empty, convId, CancellationToken.None);
        try
        {
            var blueprint = new BlueprintService(
                new WorkspaceProvisioner(Options.Create(opts),
                    NullLogger<WorkspaceProvisioner>.Instance),
                Options.Create(opts), _db,
                NullLogger<BlueprintService>.Instance);
            await blueprint.RunAsync(sb, convId,
                await blueprint.ResolveAsync(convId, null, CancellationToken.None),
                new DockerSandboxExecutor(_docker), CancellationToken.None);

            var rec = await _db.Sandboxes.FindAsync(sb.Id);
            rec!.BlueprintStatus.ShouldBe("failed");
            rec.BlueprintLogTail!.ShouldContain("oops");
        }
        finally { await svc.DeleteAsync(sb.Id, CancellationToken.None); }
    }

    /// <summary><see cref="IHttpClientFactory"/> trivial — new HttpClient por create.</summary>
    private sealed class SimpleHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}