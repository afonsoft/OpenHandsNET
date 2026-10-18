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

    /// <summary>BYOR LiteLLM virtual keys (SPEC-50 — ciphertext at rest).</summary>
    public DbSet<ByorKeyRecord> ByorKeys => Set<ByorKeyRecord>();

    /// <summary>MCP server registrations.</summary>
    public DbSet<McpServerRecord> McpServers => Set<McpServerRecord>();

    /// <summary>Skill documents.</summary>
    public DbSet<SkillRecord> Skills => Set<SkillRecord>();

    /// <summary>Skills resolved into conversations (join).</summary>
    public DbSet<ConversationSkillRecord> ConversationSkills =>
        Set<ConversationSkillRecord>();

    /// <summary>Lifecycle hook configurations.</summary>
    public DbSet<HookConfigRecord> HookConfigs => Set<HookConfigRecord>();

    /// <summary>Outbound webhook registrations.</summary>
    public DbSet<WebhookRegistrationRecord> WebhookRegistrations =>
        Set<WebhookRegistrationRecord>();

    /// <summary>Users.</summary>
    public DbSet<UserRecord> Users => Set<UserRecord>();

    /// <summary>API keys (hash at rest).</summary>
    public DbSet<ApiKeyRecord> ApiKeys => Set<ApiKeyRecord>();

    /// <summary>Org-scoped memory entries (SPEC-30).</summary>
    public DbSet<MemoryRecord> MemoryRecords => Set<MemoryRecord>();

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

    /// <summary>Device-flow authorization sessions (SPEC-08 RF-009).</summary>
    public DbSet<DeviceCodeRecord> DeviceCodes => Set<DeviceCodeRecord>();

    /// <summary>Instance feature flags.</summary>
    public DbSet<FeatureFlagRecord> FeatureFlags => Set<FeatureFlagRecord>();

    /// <summary>User schedules that fire conversations (SPEC-47).</summary>
    public DbSet<OpenHandsNET.Infrastructure.Automations.AutomationRecord>
        Automations => Set<OpenHandsNET.Infrastructure.Automations.AutomationRecord>();

    /// <summary>Recorded automation executions.</summary>
    public DbSet<OpenHandsNET.Infrastructure.Automations.AutomationRunRecord>
        AutomationRuns => Set<OpenHandsNET.Infrastructure.Automations.AutomationRunRecord>();

    /// <summary>SPEC-48: org security policies.</summary>
    public DbSet<OpenHandsNET.Infrastructure.Security.SecurityPolicyRecord>
        SecurityPolicies => Set<OpenHandsNET.Infrastructure.Security.SecurityPolicyRecord>();

    /// <summary>Anonymous webhook triggers (token hash only).</summary>
    public DbSet<OpenHandsNET.Infrastructure.Automations.AutomationHookRecord>
        AutomationHooks => Set<OpenHandsNET.Infrastructure.Automations.AutomationHookRecord>();

    /// <summary>ASP.NET DataProtection keys (PersistKeysToDbContext).</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    /// <summary>
    /// Espelha CreatedAt/UpdatedAt em colunas *Ticks — SQLite não ordena
    /// <see cref="DateTimeOffset"/>; os espelhos são sincronizados aqui para
    /// que serviços continuem escrevendo apenas os campos DateTimeOffset.
    /// </summary>
    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            switch (entry.Entity)
            {
                case Conversations.ConversationRecord c:
                    c.CreatedAtTicks = c.CreatedAt.UtcTicks;
                    c.UpdatedAtTicks = c.UpdatedAt.UtcTicks;
                    break;
                case Conversations.StartTaskRecord t:
                    t.CreatedAtTicks = t.CreatedAt.UtcTicks;
                    t.UpdatedAtTicks = t.UpdatedAt.UtcTicks;
                    break;
                case Sandbox.SandboxRecord s:
                    s.CreatedAtTicks = s.CreatedAt.UtcTicks;
                    if (entry.State == EntityState.Added && s.UpdatedAt == default)
                    {
                        s.UpdatedAt = s.CreatedAt;
                    }
                    else if (entry.State == EntityState.Modified)
                    {
                        s.UpdatedAt = DateTimeOffset.UtcNow;
                    }

                    s.UpdatedAtTicks = s.UpdatedAt.UtcTicks;
                    s.ExpiresAtTicks = s.ExpiresAt?.UtcTicks;
                    break;
                case OrgInvitationRecord i:
                    i.ExpiresAtTicks = i.ExpiresAt.UtcTicks;
                    break;
                case DeviceCodeRecord d:
                    d.ExpiresAtTicks = d.ExpiresAt.UtcTicks;
                    break;
                case AuditEventRecord a:
                    a.TimestampTicks = a.Timestamp.UtcTicks;
                    break;
                case OpenHandsNET.Infrastructure.Automations.AutomationRecord a:
                    a.NextRunAtTicks = a.NextRunAt?.UtcTicks;
                    break;
            }
        }
        return base.SaveChangesAsync(ct);
    }

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
        modelBuilder.Entity<OpenHandsNET.Infrastructure.Automations.AutomationRecord>()
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
