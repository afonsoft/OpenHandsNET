using Docker.DotNet;
using Docker.DotNet.Models;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Git;
using OpenHandsNET.Infrastructure.Git;
using OpenHandsNET.Infrastructure.Persistence;
using OpenHandsNET.Infrastructure.Sandbox;
using OpenHandsNET.Sandboxing;

using Shouldly;

namespace OpenHandsNET.IntegrationTests.Git;

/// <summary>
/// GitService contra container real com git (SPEC-12 RF-001/002/003).
/// A imagem de teste é <c>bash:5 + git</c> (openhandsnet/git-test, construída
/// on-demand no init). Skip sem daemon.
/// </summary>
public sealed class GitServiceTests : IAsyncLifetime
{
    private const string Image = "openhandsnet/git-test:latest";
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-git-{Guid.NewGuid():N}.db");
    private readonly string _wsRoot = Path.Combine(
        Path.GetTempPath(), $"ohn-gitws-{Guid.NewGuid():N}");
    private AppDbContext _db = null!;
    private IDockerClient _docker = null!;
    private SandboxService _service = null!;
    private DockerSandboxExecutor _executor = null!;
    private GitService _git = null!;
    private bool _dockerUp;
    private readonly string _owner = $"test-{Guid.NewGuid():N}";
    private readonly List<OpenHandsNET.Domain.Sandbox.Sandbox> _sandboxes = [];

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
        catch
        {
            return;
        }

        await EnsureGitImageAsync();
        var opts = Options.Create(new SandboxOptions
        {
            WorkspaceRoot = _wsRoot,
            InstanceOwner = _owner,
        });
        var provisioner = new WorkspaceProvisioner(opts,
            NullLogger<WorkspaceProvisioner>.Instance);
        var egress = new DockerNetworkPolicy(_docker, opts,
            NullLogger<DockerNetworkPolicy>.Instance);
        var engine = new DockerSandboxEngine(_docker, provisioner, egress,
            opts, NullLogger<DockerSandboxEngine>.Instance);
        _service = new SandboxService(_db, engine, opts,
            NullLogger<SandboxService>.Instance);
        _executor = new DockerSandboxExecutor(_docker);
        var dp = new ServiceCollection().AddDataProtection().Services
            .BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>();
        _git = new GitService(_executor,
            new GitCredentialProvider(_db, dp,
                NullLogger<GitCredentialProvider>.Instance),
            Options.Create(new GitOptions()),
            NullLogger<GitService>.Instance);
    }

    public async Task DisposeAsync()
    {
        foreach (var sb in _sandboxes)
        {
            try
            {
                await _service.DeleteAsync(sb.Id, CancellationToken.None);
            }
            catch
            {
            }
        }

        _db?.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        try
        {
            if (Directory.Exists(_wsRoot))
            {
                Directory.Delete(_wsRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Arquivos criados como root dentro do container podem não ser
            // removíveis pelo usuário do teste — limpeza best-effort.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task<OpenHandsNET.Domain.Sandbox.Sandbox> StartAsync()
    {
        var spec = new OpenHandsNET.Domain.Sandbox.SandboxSpec(
            Guid.NewGuid(), "git-test", Image, MemoryMb: 256, CpuLimit: 0.5,
            TimeoutMinutes: 30);
        await _service.UpsertSpecAsync(spec, CancellationToken.None);
        var sb = await _service.StartAsync(
            spec.Id, Guid.NewGuid(), CancellationToken.None);
        _sandboxes.Add(sb);
        return sb;
    }

    /// <summary>Cria um repo "origin" dentro do container e retorna file:// url.</summary>
    private async Task<string> SeedOriginRepoAsync(
        OpenHandsNET.Domain.Sandbox.Sandbox sb)
    {
        var r = await _executor.ExecAsync(sb,
            "set -e; git init --bare -b main /tmp/origin >/dev/null; " +
            "git clone /tmp/origin /tmp/work >/dev/null 2>&1; " +
            "cd /tmp/work && " +
            "git -c user.name=t -c user.email=t@t commit --allow-empty " +
            "-m init >/dev/null && " +
            "echo seed > seed.txt && git add . && " +
            "git -c user.name=t -c user.email=t@t commit -m seed >/dev/null && " +
            "git push /tmp/origin HEAD:main >/dev/null",
            TimeSpan.FromSeconds(60), CancellationToken.None);
        r.ExitCode.ShouldBe(0, r.StdErr);
        return "file:///tmp/origin";
    }

    [Fact]
    public async Task Clone_Changes_Diff_Log_Commit_RoundTrip()
    {
        if (!_dockerUp)
        {
            return;
        }

        var sb = await StartAsync();
        var origin = await SeedOriginRepoAsync(sb);

        await _git.CloneAsync(sb, origin, "openhands/abc12345",
            null, CancellationToken.None);
        (await _git.HasRepositoryAsync(sb, CancellationToken.None))
            .ShouldBeTrue();

        var changes0 = await _git.GetChangesAsync(sb, CancellationToken.None);
        changes0.Branch.ShouldBe("openhands/abc12345");

        // Agente modifica arquivo → aparece em changes + diff.
        // SPEC-78 RF-001: nome único — workspace reutilizado não pode
        // apresentar o untracked já commitado/consumido.
        var novo = $"novo-{Guid.NewGuid():N}.txt";
        await _executor.ExecAsync(sb,
            $"echo novo >> /workspace/repo/seed.txt && echo x > /workspace/repo/{novo}",
            TimeSpan.FromSeconds(15), CancellationToken.None);
        var changes = await _git.GetChangesAsync(sb, CancellationToken.None);
        changes.Modified.ShouldContain(f => f.Path == "seed.txt");
        changes.Untracked.ShouldContain(f => f.Path == novo);

        var diff = await _git.GetDiffAsync(sb, null, CancellationToken.None);
        diff.ShouldNotBeNull();
        diff.Files.ShouldContain(f => f.Path == "seed.txt");
        diff.Additions.ShouldBeGreaterThan(0);

        // Commit como autor do agente → aparece no log.
        var commit = await _git.CommitAsync(
            sb, "agente commit", null, CancellationToken.None);
        commit.Success.ShouldBeTrue(commit.Error);
        commit.Hash.ShouldNotBeNull();

        var log = await _git.GetLogAsync(sb, 50, CancellationToken.None);
        log.ShouldContain(e => e.Author == "openhandsnet-agent"
            && e.Message == "agente commit");
    }

    [Fact]
    public async Task NoRepository_HasRepository_False_And_Diff_Null()
    {
        if (!_dockerUp)
        {
            return;
        }

        var sb = await StartAsync();
        (await _git.HasRepositoryAsync(sb, CancellationToken.None))
            .ShouldBeFalse();
        (await _git.GetDiffAsync(sb, null, CancellationToken.None))
            .ShouldBeNull();
    }

    [Fact]
    public async Task Clone_Failure_Throws_Descriptive_Error()
    {
        if (!_dockerUp)
        {
            return;
        }

        var sb = await StartAsync();
        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            _git.CloneAsync(sb, "file:///tmp/does-not-exist", "b", null,
                CancellationToken.None));
        ex.Message.ShouldContain("git clone failed");
    }

    /// <summary>Builds <c>bash:5 + git</c> once (cached across runs).</summary>
    private async Task EnsureGitImageAsync()
    {
        try
        {
            await _docker.Images.InspectImageAsync(Image);
            return;
        }
        catch (DockerImageNotFoundException)
        {
        }

        var dir = Path.Combine(Path.GetTempPath(), "ohn-git-test-img");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "Dockerfile"),
            "FROM bash:5\nRUN apk add --no-cache git\n");
        var psi = new System.Diagnostics.ProcessStartInfo("docker")
        {
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(Image);
        psi.ArgumentList.Add(dir);
        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.WaitForExitAsync();
        p.ExitCode.ShouldBe(0, "docker build git-test image");
    }
}