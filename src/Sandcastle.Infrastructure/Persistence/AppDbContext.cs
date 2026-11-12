using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Sandcastle.Domain.Configuration;
using Sandcastle.Infrastructure.Eventing;

namespace Sandcastle.Infrastructure.Persistence;

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

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.ConfigureWarnings(w =>
            w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
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
    public DbSet<Sandcastle.Infrastructure.Sandbox.SandboxRecord> Sandboxes =>
        Set<Sandcastle.Infrastructure.Sandbox.SandboxRecord>();

    /// <summary>App conversations.</summary>
    public DbSet<Sandcastle.Infrastructure.Conversations.ConversationRecord> Conversations =>
        Set<Sandcastle.Infrastructure.Conversations.ConversationRecord>();

    /// <summary>Conversation start tasks.</summary>
    public DbSet<Sandcastle.Infrastructure.Conversations.StartTaskRecord> StartTasks =>
        Set<Sandcastle.Infrastructure.Conversations.StartTaskRecord>();

    /// <summary>Sandbox spec templates.</summary>
    public DbSet<Sandcastle.Infrastructure.Sandbox.SandboxSpecRecord> SandboxSpecs =>
        Set<Sandcastle.Infrastructure.Sandbox.SandboxSpecRecord>();

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

    /// <summary>Service users (SPEC-57).</summary>
    public DbSet<ServiceUserRecord> ServiceUsers => Set<ServiceUserRecord>();
    /// <summary>SPEC-63: fila de sugestões de conhecimento.</summary>
    public DbSet<Sandcastle.Infrastructure.Knowledge.KnowledgeSuggestionRecord> KnowledgeSuggestions => Set<Sandcastle.Infrastructure.Knowledge.KnowledgeSuggestionRecord>();
    public DbSet<ScimTokenRecord> ScimTokens => Set<ScimTokenRecord>();
    public DbSet<IdpGroupMappingRecord> IdpGroupMappings => Set<IdpGroupMappingRecord>();
    public DbSet<UsageTierRecord> UsageTiers => Set<UsageTierRecord>();
    public DbSet<UserUsageOverrideRecord> UserUsageOverrides => Set<UserUsageOverrideRecord>();
    public DbSet<IpAccessRuleRecord> IpAccessRules => Set<IpAccessRuleRecord>();

    /// <summary>Org-scoped memory entries (SPEC-30).</summary>
    public DbSet<MemoryRecord> MemoryRecords => Set<MemoryRecord>();

    /// <summary>SPEC-64: browser auth profiles (state ciphertext-only).</summary>
    public DbSet<Sandcastle.Infrastructure.BrowserProfiles.BrowserProfileRecord>
        BrowserProfiles => Set<Sandcastle.Infrastructure.BrowserProfiles.BrowserProfileRecord>();

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

    /// <summary>SPEC-128: notas de contexto por conversa (DB-backed).</summary>
    public DbSet<ConversationContextNoteRecord> ContextNotes =>
        Set<ConversationContextNoteRecord>();

    /// <summary>SPEC-134: BYOK — provider keys por usuário.</summary>
    public DbSet<UserLlmKeyRecord> UserLlmKeys => Set<UserLlmKeyRecord>();

    /// <summary>SPEC-128: memória key/value granular por conversa.</summary>
    public DbSet<ConversationMemoryRecord> ConversationMemories =>
        Set<ConversationMemoryRecord>();

    /// <summary>Org subscriptions.</summary>
    public DbSet<SubscriptionRecord> Subscriptions => Set<SubscriptionRecord>();

    /// <summary>Metered usage rows.</summary>
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();

    /// <summary>Device-flow authorization sessions (SPEC-08 RF-009).</summary>
    public DbSet<DeviceCodeRecord> DeviceCodes => Set<DeviceCodeRecord>();

    /// <summary>Instance feature flags.</summary>
    public DbSet<FeatureFlagRecord> FeatureFlags => Set<FeatureFlagRecord>();

    /// <summary>User schedules that fire conversations (SPEC-47).</summary>
    public DbSet<Sandcastle.Infrastructure.Automations.AutomationRecord>
        Automations => Set<Sandcastle.Infrastructure.Automations.AutomationRecord>();

    /// <summary>Recorded automation executions.</summary>
    public DbSet<Sandcastle.Infrastructure.Automations.AutomationRunRecord>
        AutomationRuns => Set<Sandcastle.Infrastructure.Automations.AutomationRunRecord>();

    /// <summary>SPEC-48: org security policies.</summary>
    public DbSet<Sandcastle.Infrastructure.Security.SecurityPolicyRecord>
        SecurityPolicies => Set<Sandcastle.Infrastructure.Security.SecurityPolicyRecord>();

    /// <summary>Anonymous webhook triggers (token hash only).</summary>
    public DbSet<Sandcastle.Infrastructure.Automations.AutomationHookRecord>
        AutomationHooks => Set<Sandcastle.Infrastructure.Automations.AutomationHookRecord>();

    /// <summary>Public share links — token hash only (SPEC-56).</summary>
    public DbSet<Sandcastle.Infrastructure.Sharing.ConversationShareRecord>
        ConversationShares =>
            Set<Sandcastle.Infrastructure.Sharing.ConversationShareRecord>();

    /// <summary>Org SCM connections — secrets by-ref (SPEC-52).</summary>
    public DbSet<Sandcastle.Infrastructure.Scm.ScmConnectionRecord>
        ScmConnections =>
            Set<Sandcastle.Infrastructure.Scm.ScmConnectionRecord>();

    /// <summary>Processed webhook deliveries — idempotency (SPEC-52).</summary>
    public DbSet<Sandcastle.Infrastructure.Scm.WebhookDeliveryRecord>
        WebhookDeliveries =>
            Set<Sandcastle.Infrastructure.Scm.WebhookDeliveryRecord>();

    /// <summary>Auto-review de PR — runs (SPEC-140).</summary>
    public DbSet<Sandcastle.Infrastructure.Scm.ReviewRunRecord>
        ReviewRuns =>
            Set<Sandcastle.Infrastructure.Scm.ReviewRunRecord>();

    /// <summary>Auto-review de PR — findings (SPEC-140).</summary>
    public DbSet<Sandcastle.Infrastructure.Scm.ReviewFindingRecord>
        ReviewFindings =>
            Set<Sandcastle.Infrastructure.Scm.ReviewFindingRecord>();

    /// <summary>CI autofix — runs (SPEC-141).</summary>
    public DbSet<Sandcastle.Infrastructure.Scm.AutofixRunRecord>
        AutofixRuns =>
            Set<Sandcastle.Infrastructure.Scm.AutofixRunRecord>();

    /// <summary>CI autofix — tentativas por run (SPEC-141).</summary>
    public DbSet<Sandcastle.Infrastructure.Scm.AutofixAttemptRecord>
        AutofixAttempts =>
            Set<Sandcastle.Infrastructure.Scm.AutofixAttemptRecord>();

    /// <summary>QA agent — runs (SPEC-142).</summary>
    public DbSet<Sandcastle.Infrastructure.Scm.QaRunRecord>
        QaRuns =>
            Set<Sandcastle.Infrastructure.Scm.QaRunRecord>();

    /// <summary>QA agent — passos executados por run (SPEC-142).</summary>
    public DbSet<Sandcastle.Infrastructure.Scm.QaStepRecord>
        QaSteps =>
            Set<Sandcastle.Infrastructure.Scm.QaStepRecord>();

    /// <summary>Slack workspaces conectados — secrets by-ref (SPEC-54).</summary>
    public DbSet<Sandcastle.Infrastructure.ChatOps.ChatConnectionRecord>
        ChatConnections =>
            Set<Sandcastle.Infrastructure.ChatOps.ChatConnectionRecord>();

    /// <summary>Thread → conversa (SPEC-54 RF-002).</summary>
    public DbSet<Sandcastle.Infrastructure.ChatOps.ChatThreadBindingRecord>
        ChatThreadBindings =>
            Set<Sandcastle.Infrastructure.ChatOps.ChatThreadBindingRecord>();

    /// <summary>Canal → repo/auto-triage (SPEC-54 RF-004/RF-005).</summary>
    public DbSet<Sandcastle.Infrastructure.ChatOps.ChatChannelConfigRecord>
        ChatChannelConfigs =>
            Set<Sandcastle.Infrastructure.ChatOps.ChatChannelConfigRecord>();

    /// <summary>Conexões Linear/Jira — tokens by-ref (SPEC-55).</summary>
    public DbSet<Sandcastle.Infrastructure.Tickets.TicketConnectionRecord>
        TicketConnections =>
            Set<Sandcastle.Infrastructure.Tickets.TicketConnectionRecord>();

    /// <summary>Projeto → repo/branch (SPEC-55 RF-003).</summary>
    public DbSet<Sandcastle.Infrastructure.Tickets.TicketProjectConfigRecord>
        TicketProjectConfigs =>
            Set<Sandcastle.Infrastructure.Tickets.TicketProjectConfigRecord>();

    /// <summary>SPEC-60 RF-001: Índices de código por repositório.</summary>
    public DbSet<RepoIndexRecord> RepoIndexes => Set<RepoIndexRecord>();

    /// <summary>SPEC-145: wiki generation runs.</summary>
    public DbSet<WikiRunRecord> WikiRuns => Set<WikiRunRecord>();

    /// <summary>SPEC-145: generated wiki pages.</summary>
    public DbSet<WikiPageRecord> WikiPages => Set<WikiPageRecord>();

    /// <summary>SPEC-60 RF-001: Unidades de código (chunks/symbols) indexadas.</summary>
    public DbSet<CodeUnitRecord> CodeUnits => Set<CodeUnitRecord>();

    /// <summary>SPEC-143: code scans — coordinator fan-out runs.</summary>
    public DbSet<Sandcastle.Infrastructure.Scans.ScanRecord>
        Scans => Set<Sandcastle.Infrastructure.Scans.ScanRecord>();

    /// <summary>SPEC-143: code scans — triaged findings.</summary>
    public DbSet<Sandcastle.Infrastructure.Scans.ScanFindingRecord>
        ScanFindings => Set<Sandcastle.Infrastructure.Scans.ScanFindingRecord>();

    /// <summary>SPEC-144: batch refactors — approved plans.</summary>
    public DbSet<Sandcastle.Infrastructure.Refactors.RefactorPlanRecord>
        RefactorPlans => Set<Sandcastle.Infrastructure.Refactors.RefactorPlanRecord>();

    /// <summary>SPEC-144: batch refactors — per-plan ordered batches.</summary>
    public DbSet<Sandcastle.Infrastructure.Refactors.RefactorBatchRecord>
        RefactorBatches => Set<Sandcastle.Infrastructure.Refactors.RefactorBatchRecord>();

    /// <summary>SPEC-61 RF-001: Diagnósticos pós-sessão de conversas.</summary>
    public DbSet<SessionInsightRecord> SessionInsights => Set<SessionInsightRecord>();

    /// <summary>SPEC-61 RF-001/RF-002: Relatórios agregados de insights.</summary>
    public DbSet<InsightReportRecord> InsightReports => Set<InsightReportRecord>();

    /// <summary>ASP.NET DataProtection keys (PersistKeysToDbContext).</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    /// <summary>
    /// Espelha CreatedAt/UpdatedAt em colunas *Ticks — SQLite não ordena
    /// <see cref="DateTimeOffset"/>; os espelhos são sincronizados aqui para
    /// que serviços continuem escrevendo apenas os campos DateTimeOffset.
    /// </summary>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
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
                case Sandcastle.Infrastructure.Automations.AutomationRecord a:
                    a.NextRunAtTicks = a.NextRunAt?.UtcTicks;
                    break;
                case Sandcastle.Infrastructure.Sharing.ConversationShareRecord s:
                    s.CreatedAtTicks = s.CreatedAt.UtcTicks;
                    s.ExpiresAtTicks = s.ExpiresAt?.UtcTicks;
                    break;
                case Sandcastle.Infrastructure.Scm.ReviewRunRecord r:
                    r.CreatedAtTicks = r.CreatedAt.UtcTicks;
                    break;
                case Sandcastle.Infrastructure.Scm.AutofixRunRecord a:
                    a.CreatedAtTicks = a.CreatedAt.UtcTicks;
                    break;
                case Sandcastle.Infrastructure.Scm.QaRunRecord q:
                    q.CreatedAtTicks = q.CreatedAt.UtcTicks;
                    break;
                case RepoIndexRecord i:
                    i.CreatedAtTicks = i.CreatedAt.UtcTicks;
                    i.UpdatedAtTicks = i.UpdatedAt.UtcTicks;
                    break;
                case WikiRunRecord w:
                    w.CreatedAtTicks = w.CreatedAt.UtcTicks;
                    w.FinishedAtTicks = w.FinishedAt?.UtcTicks;
                    break;
                case WikiPageRecord p:
                    p.CreatedAtTicks = p.CreatedAt.UtcTicks;
                    p.UpdatedAtTicks = p.UpdatedAt.UtcTicks;
                    break;
                case Sandcastle.Infrastructure.Scans.ScanRecord s:
                    s.CreatedAtTicks = s.CreatedAt.UtcTicks;
                    break;
                case Sandcastle.Infrastructure.Scans.ScanFindingRecord f:
                    f.CreatedAtTicks = f.CreatedAt.UtcTicks;
                    break;
                case Sandcastle.Infrastructure.Refactors.RefactorPlanRecord r:
                    r.CreatedAtTicks = r.CreatedAt.UtcTicks;
                    break;
            }
        }
        return base.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Org filter (SPEC-10 RF-001): só ativo com multi-tenancy ligado.
        modelBuilder.Entity<Sandcastle.Infrastructure.Conversations.ConversationRecord>()
            .HasQueryFilter(r =>
                !_multiTenancy || CurrentOrgId == null || r.OrgId == CurrentOrgId);
        modelBuilder.Entity<ApiKeyRecord>()
            .HasQueryFilter(r =>
                !_multiTenancy || CurrentOrgId == null || r.OrgId == CurrentOrgId);
        modelBuilder.Entity<Sandcastle.Infrastructure.Automations.AutomationRecord>()
            .HasQueryFilter(r =>
                !_multiTenancy || CurrentOrgId == null || r.OrgId == CurrentOrgId);
        modelBuilder.Entity<Sandcastle.Infrastructure.Scm.ScmConnectionRecord>()
            .HasQueryFilter(r =>
                !_multiTenancy || CurrentOrgId == null || r.OrgId == CurrentOrgId);
        modelBuilder.Entity<Sandcastle.Infrastructure.Scm.ReviewRunRecord>()
            .HasQueryFilter(r =>
                !_multiTenancy || CurrentOrgId == null || r.OrgId == CurrentOrgId);
        modelBuilder.Entity<Sandcastle.Infrastructure.Scm.ReviewFindingRecord>()
            .HasQueryFilter(r =>
                !_multiTenancy || CurrentOrgId == null || r.OrgId == CurrentOrgId);
        modelBuilder.Entity<Sandcastle.Infrastructure.Scm.AutofixRunRecord>()
            .HasQueryFilter(r =>
                !_multiTenancy || CurrentOrgId == null || r.OrgId == CurrentOrgId);
        modelBuilder.Entity<Sandcastle.Infrastructure.Scm.AutofixAttemptRecord>()
            .HasQueryFilter(r =>
                !_multiTenancy || CurrentOrgId == null || r.OrgId == CurrentOrgId);
        modelBuilder.Entity<Sandcastle.Infrastructure.Scm.QaRunRecord>()
            .HasQueryFilter(r =>
                !_multiTenancy || CurrentOrgId == null || r.OrgId == CurrentOrgId);
        modelBuilder.Entity<Sandcastle.Infrastructure.Scm.QaStepRecord>()
            .HasQueryFilter(r =>
                !_multiTenancy || CurrentOrgId == null || r.OrgId == CurrentOrgId);
        modelBuilder.Entity<Sandcastle.Infrastructure.ChatOps.ChatConnectionRecord>()
            .HasQueryFilter(r =>
                !_multiTenancy || CurrentOrgId == null || r.OrgId == CurrentOrgId);
        modelBuilder.Entity<Sandcastle.Infrastructure.Tickets.TicketConnectionRecord>()
            .HasQueryFilter(r =>
                !_multiTenancy || CurrentOrgId == null || r.OrgId == CurrentOrgId);
        modelBuilder.Entity<ServiceUserRecord>()
            .HasQueryFilter(r =>
                !_multiTenancy || CurrentOrgId == null || r.OrgId == CurrentOrgId);
        modelBuilder.Entity<Sandcastle.Infrastructure.Knowledge.KnowledgeSuggestionRecord>()
            .HasQueryFilter(r =>
                !_multiTenancy || CurrentOrgId == null || r.OrgId == CurrentOrgId);
        modelBuilder.Entity<ScimTokenRecord>()
            .HasQueryFilter(r =>
                !_multiTenancy || CurrentOrgId == null || r.OrgId == CurrentOrgId);
        modelBuilder.Entity<IdpGroupMappingRecord>()
            .HasQueryFilter(r =>
                !_multiTenancy || CurrentOrgId == null || r.OrgId == CurrentOrgId);
        modelBuilder.Entity<UsageTierRecord>()
            .HasQueryFilter(r =>
                !_multiTenancy || CurrentOrgId == null || r.OrgId == CurrentOrgId);
        modelBuilder.Entity<UserUsageOverrideRecord>()
            .HasQueryFilter(r =>
                !_multiTenancy || CurrentOrgId == null || r.OrgId == CurrentOrgId);
        modelBuilder.Entity<ApiKeyRecord>()
            .HasIndex(k => k.ServiceUserId);
        modelBuilder.Entity<ScimTokenRecord>()
            .HasIndex(k => k.OrgId);
        modelBuilder.Entity<IdpGroupMappingRecord>()
            .HasIndex(k => new { k.OrgId, k.IdpGroup });
        modelBuilder.Entity<UserUsageOverrideRecord>()
            .HasIndex(k => new { k.OrgId, k.UserId });
        modelBuilder.Entity<IpAccessRuleRecord>()
            .HasIndex(k => k.Scope);
        modelBuilder.Entity<Sandcastle.Infrastructure.BrowserProfiles.BrowserProfileRecord>()
            .HasQueryFilter(r =>
                !_multiTenancy || CurrentOrgId == null || r.OrgId == CurrentOrgId);
        modelBuilder.Entity<Sandcastle.Infrastructure.BrowserProfiles.BrowserProfileRecord>()
            .HasIndex(p => new { p.OwnerId, p.Domain }).IsUnique();
        modelBuilder.Entity<Sandcastle.Infrastructure.Scans.ScanRecord>()
            .HasQueryFilter(r =>
                !_multiTenancy || CurrentOrgId == null || r.OrgId == CurrentOrgId);
        modelBuilder.Entity<Sandcastle.Infrastructure.Scans.ScanFindingRecord>()
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