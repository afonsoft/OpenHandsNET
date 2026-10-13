using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenHandsNET.Domain.Configuration;
using OpenHandsNET.Infrastructure.Eventing;

namespace OpenHandsNET.Infrastructure.Persistence;

/// <summary>Multi-tenancy switch (SPEC-10): org filter só ativa quando ligado.</summary>
public sealed class MultiTenancyOptions
{
    /// <summary>Enables org-scoped global query filters (SPEC-08).</summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// Application database context (SQLite by default, PostgreSQL for enterprise).
/// Snake_case names via EFCore.NamingConventions; <see cref="PostgresDbContext"/>
/// exists solely to carry the PostgreSQL migration set.
/// </summary>
public class AppDbContext : DbContext, IDataProtectionKeyContext
{
    private readonly bool _multiTenancy;

    /// <summary>Creates the context bound to DI-provided tenancy options.</summary>
    public AppDbContext(
        DbContextOptions<AppDbContext> options,
        IOptions<MultiTenancyOptions>? multiTenancy = null) : base(options) =>
        _multiTenancy = multiTenancy?.Value.Enabled == true;

    /// <summary>Provider-specific subclass ctor (design-time/runtime).</summary>
    protected AppDbContext(DbContextOptions options) : base(options)
    {
    }

    /// <summary>
    /// Org applied to org-scoped queries this session (set per request scope
    /// when multi-tenancy is enabled — SPEC-08 wires the accessor).
    /// </summary>
    public Guid? CurrentOrgId { get; set; }

    /// <summary>Key-value application settings.</summary>
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    /// <summary>Append-only conversation event stream.</summary>
    public DbSet<EventRecord> Events => Set<EventRecord>();

    /// <summary>Sandbox containers bound to conversations.</summary>
    public DbSet<OpenHandsNET.Infrastructure.Sandbox.SandboxRecord> Sandboxes =>
        Set<OpenHandsNET.Infrastructure.Sandbox.SandboxRecord>();

    /// <summary>App conversations.</summary>
    public DbSet<OpenHandsNET.Infrastructure.Conversations.ConversationRecord> Conversations =>
        Set<OpenHandsNET.Infrastructure.Conversations.ConversationRecord>();

    /// <summary>Conversation start tasks.</summary>
    public DbSet<OpenHandsNET.Infrastructure.Conversations.StartTaskRecord> StartTasks =>
        Set<OpenHandsNET.Infrastructure.Conversations.StartTaskRecord>();

    /// <summary>Sandbox spec templates.</summary>
    public DbSet<OpenHandsNET.Infrastructure.Sandbox.SandboxSpecRecord> SandboxSpecs =>
        Set<OpenHandsNET.Infrastructure.Sandbox.SandboxSpecRecord>();

    /// <summary>Queued messages while the agent runs.</summary>
    public DbSet<PendingMessageRecord> PendingMessages => Set<PendingMessageRecord>();

    /// <summary>Encrypted secrets (ciphertext at rest).</summary>
    public DbSet<SecretRecord> Secrets => Set<SecretRecord>();

    /// <summary>MCP server registrations.</summary>
    public DbSet<McpServerRecord> McpServers => Set<McpServerRecord>();

    /// <summary>Skill documents.</summary>
    public DbSet<SkillRecord> Skills => Set<SkillRecord>();

    /// <summary>Outbound webhook registrations.</summary>
    public DbSet<WebhookRegistrationRecord> WebhookRegistrations =>
        Set<WebhookRegistrationRecord>();

    /// <summary>Users.</summary>
    public DbSet<UserRecord> Users => Set<UserRecord>();

    /// <summary>API keys (hash at rest).</summary>
    public DbSet<ApiKeyRecord> ApiKeys => Set<ApiKeyRecord>();

    /// <summary>Organizations.</summary>
    public DbSet<OrganizationRecord> Organizations => Set<OrganizationRecord>();

    /// <summary>Org memberships.</summary>
    public DbSet<OrgMemberRecord> OrgMembers => Set<OrgMemberRecord>();

    /// <summary>Org invitations.</summary>
    public DbSet<OrgInvitationRecord> OrgInvitations => Set<OrgInvitationRecord>();

    /// <summary>Append-only audit trail.</summary>
    public DbSet<AuditEventRecord> AuditEvents => Set<AuditEventRecord>();

    /// <summary>Billing plans.</summary>
    public DbSet<PlanRecord> Plans => Set<PlanRecord>();

    /// <summary>Org subscriptions.</summary>
    public DbSet<SubscriptionRecord> Subscriptions => Set<SubscriptionRecord>();

    /// <summary>Metered usage rows.</summary>
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();

    /// <summary>ASP.NET DataProtection keys (PersistKeysToDbContext).</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Org filter (SPEC-10 RF-001): só ativo com multi-tenancy ligado.
        modelBuilder.Entity<OpenHandsNET.Infrastructure.Conversations.ConversationRecord>()
            .HasQueryFilter(r =>
                !_multiTenancy || CurrentOrgId == null || r.OrgId == CurrentOrgId);
        modelBuilder.Entity<ApiKeyRecord>()
            .HasQueryFilter(r =>
                !_multiTenancy || CurrentOrgId == null || r.OrgId == CurrentOrgId);
    }
}

/// <summary>
/// PostgreSQL-bound context — same model, separate migration set under
/// <c>Persistence/Migrations/Postgres</c> (SPEC-10 RF-003).
/// </summary>
public sealed class PostgresDbContext(
    DbContextOptions<PostgresDbContext> options) : AppDbContext(options);
