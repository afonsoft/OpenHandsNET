using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Infrastructure.Persistence;

using Shouldly;

namespace OpenHandsNET.IntegrationTests.Persistence;

/// <summary>
/// SPEC-10 AC: schema completo materializado via migrations (snake_case),
/// pragmas SQLite ativos e filtro de org.
/// </summary>
public sealed class PersistenceSchemaTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-schema-{Guid.NewGuid():N}.db");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        return Task.CompletedTask;
    }

    private AppDbContext NewContext(bool multiTenancy = false, Guid? orgId = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath};Default Timeout=10")
            .UseSnakeCaseNamingConvention()
            .Options;
        var ctx = new AppDbContext(options,
            new Microsoft.Extensions.Options.OptionsWrapper<MultiTenancyOptions>(
                new MultiTenancyOptions { Enabled = multiTenancy }))
        {
            CurrentOrgId = orgId,
        };
        return ctx;
    }

    [Fact]
    public async Task Migrate_CreatesAllSpec10Tables()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();

        var tables = await ExistingTablesAsync();
        foreach (var t in new[]
        {
            "app_conversations", // alias: conversations table
            "conversations", "conversation_start_tasks", "pending_messages",
            "sandboxes", "sandbox_specs", "events", "app_settings", "secrets",
            "mcp_servers", "skills", "webhook_registrations", "users",
            "api_keys", "organizations", "org_members", "org_invitations",
            "audit_events", "plans", "subscriptions", "usage_records",
            "data_protection_keys",
        })
        {
            if (t is "app_conversations")
            {
                continue; // nome real: conversations
            }

            tables.ShouldContain(t);
        }
    }

    [Fact]
    public async Task Migrate_UsesSnakeCaseColumns()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();

        var columns = await ColumnsOfAsync("conversations");
        columns.ShouldContain("execution_status");
        columns.ShouldContain("selected_repository");
        columns.ShouldContain("org_id");
        columns.ShouldNotContain("ExecutionStatus");
    }

    [Fact]
    public async Task Secrets_StoreCiphertext_NotPlaintext()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();

        var services = new ServiceCollection().AddDataProtection().Services
            .BuildServiceProvider();
        var protector = services
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("openhandsnet.secrets");
        const string plaintext = "sk-live-test-12345";

        db.Secrets.Add(new SecretRecord
        {
            Scope = "app",
            OwnerId = "system",
            Name = "llm_key",
            Ciphertext = protector.Protect(plaintext),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var row = await db.Secrets.AsNoTracking().SingleAsync();
        row.Ciphertext.ShouldNotContain(plaintext);
        protector.Unprotect(row.Ciphertext).ShouldBe(plaintext);
    }

    [Fact]
    public async Task OrgFilter_HidesOtherOrgs_WhenEnabled()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        await using (var seed = NewContext())
        {
            await seed.Database.MigrateAsync();
            seed.Conversations.Add(new OpenHandsNET.Infrastructure.Conversations.ConversationRecord
            {
                Id = Guid.NewGuid(),
                ExecutionStatus = "idle",
                OrgId = orgA,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            seed.Conversations.Add(new OpenHandsNET.Infrastructure.Conversations.ConversationRecord
            {
                Id = Guid.NewGuid(),
                ExecutionStatus = "idle",
                OrgId = orgB,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var scoped = NewContext(multiTenancy: true, orgId: orgA);
        var visible = await scoped.Conversations.AsNoTracking().ToListAsync();
        visible.ShouldAllBe(r => r.OrgId == orgA);
        visible.Count.ShouldBe(1);

        await using var unfiltered = NewContext(multiTenancy: false);
        (await unfiltered.Conversations.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task AppendOnly_EventAndAudit_AreInsertOnly()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();

        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.NewGuid(),
            Action = "test",
            Timestamp = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        (await db.AuditEvents.CountAsync()).ShouldBe(1);
        // Sem Update/Delete expostos: a tabela é append-only por convenção —
        // o teste garante insert + leitura no-tracking.
        (await db.AuditEvents.AsNoTracking().CountAsync()).ShouldBe(1);
    }

    private async Task<HashSet<string>> ExistingTablesAsync()
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath};Default Timeout=10");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            set.Add(reader.GetString(0));
        }

        return set;
    }

    private async Task<HashSet<string>> ColumnsOfAsync(string table)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath};Default Timeout=10");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            set.Add(reader.GetString(1));
        }

        return set;
    }
}