using Microsoft.EntityFrameworkCore;
using OpenHandsNET.Application.Skills;
using OpenHandsNET.Domain.Skills;
using OpenHandsNET.Infrastructure.Persistence;
using OpenHandsNET.Infrastructure.Skills;

namespace OpenHandsNET.UnitTests.Skills;

/// <summary>SPEC-11 RF-001/002: CRUD, validação e trigger matching.</summary>
public sealed class SkillServiceTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-skills-{Guid.NewGuid():N}.db");
    private AppDbContext _db = null!;
    private SkillService _svc = null!;

    public async Task InitializeAsync()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .UseSnakeCaseNamingConvention().Options);
        await _db.Database.MigrateAsync();
        _svc = new SkillService(_db);
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        File.Delete(_dbPath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Create_Get_RoundTrip()
    {
        var s = await _svc.CreateAsync(new Skill
        {
            Id = Guid.NewGuid(), Name = "my-skill", OwnerId = "u1",
            Description = "d", ContentMd = "# hi",
            Triggers = [new SkillTrigger(SkillTriggerType.Keyword, "dotnet")],
        }, CancellationToken.None);

        var got = await _svc.GetAsync(s.Id, "u1", CancellationToken.None);
        got.ShouldNotBeNull();
        got!.Name.ShouldBe("my-skill");
        got.Triggers.ShouldHaveSingleItem().Type.ShouldBe(SkillTriggerType.Keyword);
    }

    [Fact]
    public async Task Create_Rejects_NonSlug_Name()
    {
        await Should.ThrowAsync<InvalidOperationException>(() =>
            _svc.CreateAsync(new Skill
            {
                Id = Guid.NewGuid(), Name = "Not A Slug!", ContentMd = "x",
            }, CancellationToken.None));
    }

    [Fact]
    public async Task Keyword_Matches_Whole_Word_CaseInsensitive()
    {
        var conv = Guid.NewGuid();
        await SeedAsync("kw", SkillTriggerType.Keyword, "git");

        var hit = await _svc.ResolveForConversationAsync(
            conv, "please commit with git", null, null, null,
            CancellationToken.None);
        hit.ShouldContain(s => s.Name == "kw");

        var miss = await _svc.ResolveForConversationAsync(
            conv, "digit substring should not match", null, null, null,
            CancellationToken.None);
        miss.ShouldNotContain(s => s.Name == "kw");
    }

    [Fact]
    public async Task Always_And_Task_Triggers()
    {
        var conv = Guid.NewGuid();
        await SeedAsync("always-one", SkillTriggerType.Always);
        await _svc.CreateAsync(new Skill
        {
            Id = Guid.NewGuid(), Name = "task-one", ContentMd = "x",
            Triggers = [new SkillTrigger(SkillTriggerType.Task, "bugfix")],
        }, CancellationToken.None);

        var resolved = await _svc.ResolveForConversationAsync(
            conv, null, "bugfix", null, null, CancellationToken.None);
        resolved.Select(s => s.Name).ShouldBe(["always-one", "task-one"],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Ordering_Builtin_Then_Org_Then_User_And_Dedup()
    {
        var conv = Guid.NewGuid();
        await SeedAsync("user-skill", SkillTriggerType.Always, null, "user", "u1");
        await SeedAsync("org-skill", SkillTriggerType.Always, null, "org");
        await SeedAsync("builtin-skill", SkillTriggerType.Always, null, "builtin");

        var resolved = await _svc.ResolveForConversationAsync(
            conv, null, null, "u1", null, CancellationToken.None);
        resolved.Select(s => s.Name).ShouldBe(
            ["builtin-skill", "org-skill", "user-skill"]);
    }

    [Fact]
    public async Task Org_Skill_Invisible_To_Other_Org()
    {
        var conv = Guid.NewGuid();
        await _svc.CreateAsync(new Skill
        {
            Id = Guid.NewGuid(), Name = "org-secret", OrgId = "orgA",
            ContentMd = "x",
            Triggers = [new SkillTrigger(SkillTriggerType.Always)],
        }, CancellationToken.None);

        var orgA = await _svc.ResolveForConversationAsync(
            conv, null, null, null, "orgA", CancellationToken.None);
        orgA.ShouldContain(s => s.Name == "org-secret");

        var orgB = await _svc.ResolveForConversationAsync(
            conv, null, null, null, "orgB", CancellationToken.None);
        orgB.ShouldNotContain(s => s.Name == "org-secret");
    }

    [Fact]
    public async Task Pinned_Skills_Resolve_Regardless_Of_Trigger()
    {
        var conv = Guid.NewGuid();
        var s = await _svc.CreateAsync(new Skill
        {
            Id = Guid.NewGuid(), Name = "pinned", ContentMd = "x",
            Triggers = [new SkillTrigger(SkillTriggerType.Keyword, "never-match")],
        }, CancellationToken.None);
        await _svc.PinAsync(conv, [s.Id], CancellationToken.None);

        var resolved = await _svc.ResolveForConversationAsync(
            conv, "no keywords here", null, null, null, CancellationToken.None);
        resolved.ShouldContain(x => x.Name == "pinned");
    }

    [Fact]
    public async Task Builtin_Is_ReadOnly()
    {
        await BuiltinSkillSeeder.SeedAsync(_db,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            CancellationToken.None);
        var builtin = (await _svc.ListAsync(null, null, CancellationToken.None))
            .First(s => s.Source == SkillSource.Builtin);

        (await _svc.UpdateAsync(builtin.Id, null,
            new UpdateSkillRequest("hacked", null, null, null),
            CancellationToken.None)).ShouldBeNull();
        (await _svc.DeleteAsync(builtin.Id, null, CancellationToken.None))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task Seeder_Is_Idempotent()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        await BuiltinSkillSeeder.SeedAsync(_db, logger, CancellationToken.None);
        await BuiltinSkillSeeder.SeedAsync(_db, logger, CancellationToken.None);
        var names = (await _svc.ListAsync(null, null, CancellationToken.None))
            .Select(s => s.Name).ToList();
        names.ShouldBe(names.Distinct().ToList());
        names.ShouldContain("dotnet-conventions");
    }

    private async Task SeedAsync(string name, SkillTriggerType type,
        string? value = null, string source = "user", string? owner = null)
    {
        _db.Skills.Add(new SkillRecord
        {
            Id = Guid.NewGuid(), Name = name, OwnerId = owner,
            Content = "x", Source = source,
            TriggersJson = $$"""[{"type":"{{type.ToString().ToLowerInvariant()}}","value":{{(value is null ? "null" : $"\"{value}\"")}}}]""",
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();
    }
}
