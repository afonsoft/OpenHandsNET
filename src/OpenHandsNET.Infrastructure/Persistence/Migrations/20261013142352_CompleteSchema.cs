using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenHandsNET.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompleteSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_sandboxes",
                table: "sandboxes");

            migrationBuilder.DropPrimaryKey(
                name: "PK_sandbox_specs",
                table: "sandbox_specs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_events",
                table: "events");

            migrationBuilder.DropPrimaryKey(
                name: "PK_conversations",
                table: "conversations");

            migrationBuilder.DropPrimaryKey(
                name: "PK_conversation_start_tasks",
                table: "conversation_start_tasks");

            migrationBuilder.DropPrimaryKey(
                name: "PK_app_settings",
                table: "app_settings");

            migrationBuilder.RenameColumn(
                name: "Status",
                table: "sandboxes",
                newName: "status");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "sandboxes",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "SpecId",
                table: "sandboxes",
                newName: "spec_id");

            migrationBuilder.RenameColumn(
                name: "LastError",
                table: "sandboxes",
                newName: "last_error");

            migrationBuilder.RenameColumn(
                name: "ExpiresAt",
                table: "sandboxes",
                newName: "expires_at");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "sandboxes",
                newName: "created_at");

            migrationBuilder.RenameColumn(
                name: "ConversationId",
                table: "sandboxes",
                newName: "conversation_id");

            migrationBuilder.RenameColumn(
                name: "ContainerId",
                table: "sandboxes",
                newName: "container_id");

            migrationBuilder.RenameColumn(
                name: "AgentUrl",
                table: "sandboxes",
                newName: "agent_url");

            migrationBuilder.RenameIndex(
                name: "IX_sandboxes_Status",
                table: "sandboxes",
                newName: "ix_sandboxes_status");

            migrationBuilder.RenameIndex(
                name: "IX_sandboxes_ConversationId",
                table: "sandboxes",
                newName: "ix_sandboxes_conversation_id");

            migrationBuilder.RenameColumn(
                name: "Name",
                table: "sandbox_specs",
                newName: "name");

            migrationBuilder.RenameColumn(
                name: "Image",
                table: "sandbox_specs",
                newName: "image");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "sandbox_specs",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "TimeoutMinutes",
                table: "sandbox_specs",
                newName: "timeout_minutes");

            migrationBuilder.RenameColumn(
                name: "MountWorkspace",
                table: "sandbox_specs",
                newName: "mount_workspace");

            migrationBuilder.RenameColumn(
                name: "MemoryMb",
                table: "sandbox_specs",
                newName: "memory_mb");

            migrationBuilder.RenameColumn(
                name: "EnvJson",
                table: "sandbox_specs",
                newName: "env_json");

            migrationBuilder.RenameColumn(
                name: "CpuLimit",
                table: "sandbox_specs",
                newName: "cpu_limit");

            migrationBuilder.RenameColumn(
                name: "Source",
                table: "events",
                newName: "source");

            migrationBuilder.RenameColumn(
                name: "Payload",
                table: "events",
                newName: "payload");

            migrationBuilder.RenameColumn(
                name: "Kind",
                table: "events",
                newName: "kind");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "events",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "TimestampTicks",
                table: "events",
                newName: "timestamp_ticks");

            migrationBuilder.RenameColumn(
                name: "ParentId",
                table: "events",
                newName: "parent_id");

            migrationBuilder.RenameColumn(
                name: "ConversationId",
                table: "events",
                newName: "conversation_id");

            migrationBuilder.RenameIndex(
                name: "IX_events_ConversationId_TimestampTicks",
                table: "events",
                newName: "ix_events_conversation_id_timestamp_ticks");

            migrationBuilder.RenameIndex(
                name: "IX_events_ConversationId_Kind",
                table: "events",
                newName: "ix_events_conversation_id_kind");

            migrationBuilder.RenameColumn(
                name: "Title",
                table: "conversations",
                newName: "title");

            migrationBuilder.RenameColumn(
                name: "Pinned",
                table: "conversations",
                newName: "pinned");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "conversations",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                table: "conversations",
                newName: "updated_at");

            migrationBuilder.RenameColumn(
                name: "TagsJson",
                table: "conversations",
                newName: "tags_json");

            migrationBuilder.RenameColumn(
                name: "SelectedRepository",
                table: "conversations",
                newName: "selected_repository");

            migrationBuilder.RenameColumn(
                name: "SandboxId",
                table: "conversations",
                newName: "sandbox_id");

            migrationBuilder.RenameColumn(
                name: "PendingKickoff",
                table: "conversations",
                newName: "pending_kickoff");

            migrationBuilder.RenameColumn(
                name: "LlmModel",
                table: "conversations",
                newName: "llm_model");

            migrationBuilder.RenameColumn(
                name: "ExecutionStatus",
                table: "conversations",
                newName: "execution_status");

            migrationBuilder.RenameColumn(
                name: "CreatedByUserId",
                table: "conversations",
                newName: "created_by_user_id");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "conversations",
                newName: "created_at");

            migrationBuilder.RenameColumn(
                name: "ConfirmationMode",
                table: "conversations",
                newName: "confirmation_mode");

            migrationBuilder.RenameColumn(
                name: "AgentType",
                table: "conversations",
                newName: "agent_type");

            migrationBuilder.RenameIndex(
                name: "IX_conversations_ExecutionStatus",
                table: "conversations",
                newName: "ix_conversations_execution_status");

            migrationBuilder.RenameIndex(
                name: "IX_conversations_CreatedAt",
                table: "conversations",
                newName: "ix_conversations_created_at");

            migrationBuilder.RenameColumn(
                name: "Status",
                table: "conversation_start_tasks",
                newName: "status");

            migrationBuilder.RenameColumn(
                name: "Detail",
                table: "conversation_start_tasks",
                newName: "detail");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "conversation_start_tasks",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                table: "conversation_start_tasks",
                newName: "updated_at");

            migrationBuilder.RenameColumn(
                name: "SandboxId",
                table: "conversation_start_tasks",
                newName: "sandbox_id");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "conversation_start_tasks",
                newName: "created_at");

            migrationBuilder.RenameColumn(
                name: "AppConversationId",
                table: "conversation_start_tasks",
                newName: "app_conversation_id");

            migrationBuilder.RenameIndex(
                name: "IX_conversation_start_tasks_Status",
                table: "conversation_start_tasks",
                newName: "ix_conversation_start_tasks_status");

            migrationBuilder.RenameColumn(
                name: "Value",
                table: "app_settings",
                newName: "value");

            migrationBuilder.RenameColumn(
                name: "Key",
                table: "app_settings",
                newName: "key");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "app_settings",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                table: "app_settings",
                newName: "updated_at");

            migrationBuilder.RenameIndex(
                name: "IX_app_settings_Key",
                table: "app_settings",
                newName: "ix_app_settings_key");

            migrationBuilder.AddColumn<Guid>(
                name: "org_id",
                table: "conversations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "pk_sandboxes",
                table: "sandboxes",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_sandbox_specs",
                table: "sandbox_specs",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_events",
                table: "events",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_conversations",
                table: "conversations",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_conversation_start_tasks",
                table: "conversation_start_tasks",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_app_settings",
                table: "app_settings",
                column: "id");

            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    prefix = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    key_hash = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: true),
                    scopes_json = table.Column<string>(type: "TEXT", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    user_id = table.Column<string>(type: "TEXT", nullable: true),
                    action = table.Column<string>(type: "TEXT", nullable: false),
                    payload_json = table.Column<string>(type: "TEXT", nullable: true),
                    timestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    friendly_name = table.Column<string>(type: "TEXT", nullable: true),
                    xml = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_protection_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mcp_servers",
                columns: table => new
                {
                    owner_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    config_json = table.Column<string>(type: "TEXT", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_servers", x => new { x.owner_id, x.name });
                });

            migrationBuilder.CreateTable(
                name: "org_invitations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    token = table.Column<string>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    email = table.Column<string>(type: "TEXT", nullable: false),
                    role = table.Column<string>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_org_invitations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "org_members",
                columns: table => new
                {
                    org_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    role = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_org_members", x => new { x.org_id, x.user_id });
                });

            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    slug = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    plan_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organizations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pending_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    conversation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    role = table.Column<string>(type: "TEXT", nullable: false),
                    content_json = table.Column<string>(type: "TEXT", nullable: false),
                    run = table.Column<bool>(type: "INTEGER", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pending_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "plans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    limits_json = table.Column<string>(type: "TEXT", nullable: true),
                    monthly_price_usd = table.Column<decimal>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "secrets",
                columns: table => new
                {
                    scope = table.Column<string>(type: "TEXT", nullable: false),
                    owner_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    ciphertext = table.Column<string>(type: "TEXT", nullable: false),
                    nonce = table.Column<string>(type: "TEXT", nullable: true),
                    meta_json = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_secrets", x => new { x.scope, x.owner_id, x.name });
                });

            migrationBuilder.CreateTable(
                name: "skills",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    owner_id = table.Column<string>(type: "TEXT", nullable: true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    content = table.Column<string>(type: "TEXT", nullable: false),
                    triggers_json = table.Column<string>(type: "TEXT", nullable: true),
                    source = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_skills", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    plan_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscriptions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "usage_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    period = table.Column<string>(type: "TEXT", nullable: false),
                    tokens_used = table.Column<long>(type: "INTEGER", nullable: false),
                    cost_usd = table.Column<decimal>(type: "TEXT", nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    email = table.Column<string>(type: "TEXT", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", nullable: true),
                    roles_json = table.Column<string>(type: "TEXT", nullable: true),
                    current_org_id = table.Column<string>(type: "TEXT", nullable: true),
                    disabled_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_registrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    owner_id = table.Column<string>(type: "TEXT", nullable: true),
                    conversation_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    url = table.Column<string>(type: "TEXT", nullable: false),
                    secret_ciphertext = table.Column<string>(type: "TEXT", nullable: true),
                    kinds_json = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_registrations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_org_id",
                table: "api_keys",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_prefix",
                table: "api_keys",
                column: "prefix");

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_user_id",
                table: "api_keys",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_action",
                table: "audit_events",
                column: "action");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_org_id_timestamp",
                table: "audit_events",
                columns: new[] { "org_id", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_user_id_timestamp",
                table: "audit_events",
                columns: new[] { "user_id", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_org_invitations_org_id_email",
                table: "org_invitations",
                columns: new[] { "org_id", "email" });

            migrationBuilder.CreateIndex(
                name: "ix_org_invitations_token",
                table: "org_invitations",
                column: "token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_org_members_user_id",
                table: "org_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_organizations_slug",
                table: "organizations",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pending_messages_conversation_id_created_at",
                table: "pending_messages",
                columns: new[] { "conversation_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_skills_owner_id_name",
                table: "skills",
                columns: new[] { "owner_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_org_id",
                table: "subscriptions",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "ix_usage_records_org_id_period",
                table: "usage_records",
                columns: new[] { "org_id", "period" });

            migrationBuilder.CreateIndex(
                name: "ix_users_email",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_webhook_registrations_conversation_id",
                table: "webhook_registrations",
                column: "conversation_id");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_registrations_owner_id",
                table: "webhook_registrations",
                column: "owner_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "data_protection_keys");

            migrationBuilder.DropTable(
                name: "mcp_servers");

            migrationBuilder.DropTable(
                name: "org_invitations");

            migrationBuilder.DropTable(
                name: "org_members");

            migrationBuilder.DropTable(
                name: "organizations");

            migrationBuilder.DropTable(
                name: "pending_messages");

            migrationBuilder.DropTable(
                name: "plans");

            migrationBuilder.DropTable(
                name: "secrets");

            migrationBuilder.DropTable(
                name: "skills");

            migrationBuilder.DropTable(
                name: "subscriptions");

            migrationBuilder.DropTable(
                name: "usage_records");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "webhook_registrations");

            migrationBuilder.DropPrimaryKey(
                name: "pk_sandboxes",
                table: "sandboxes");

            migrationBuilder.DropPrimaryKey(
                name: "pk_sandbox_specs",
                table: "sandbox_specs");

            migrationBuilder.DropPrimaryKey(
                name: "pk_events",
                table: "events");

            migrationBuilder.DropPrimaryKey(
                name: "pk_conversations",
                table: "conversations");

            migrationBuilder.DropPrimaryKey(
                name: "pk_conversation_start_tasks",
                table: "conversation_start_tasks");

            migrationBuilder.DropPrimaryKey(
                name: "pk_app_settings",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "org_id",
                table: "conversations");

            migrationBuilder.RenameColumn(
                name: "status",
                table: "sandboxes",
                newName: "Status");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "sandboxes",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "spec_id",
                table: "sandboxes",
                newName: "SpecId");

            migrationBuilder.RenameColumn(
                name: "last_error",
                table: "sandboxes",
                newName: "LastError");

            migrationBuilder.RenameColumn(
                name: "expires_at",
                table: "sandboxes",
                newName: "ExpiresAt");

            migrationBuilder.RenameColumn(
                name: "created_at",
                table: "sandboxes",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "conversation_id",
                table: "sandboxes",
                newName: "ConversationId");

            migrationBuilder.RenameColumn(
                name: "container_id",
                table: "sandboxes",
                newName: "ContainerId");

            migrationBuilder.RenameColumn(
                name: "agent_url",
                table: "sandboxes",
                newName: "AgentUrl");

            migrationBuilder.RenameIndex(
                name: "ix_sandboxes_status",
                table: "sandboxes",
                newName: "IX_sandboxes_Status");

            migrationBuilder.RenameIndex(
                name: "ix_sandboxes_conversation_id",
                table: "sandboxes",
                newName: "IX_sandboxes_ConversationId");

            migrationBuilder.RenameColumn(
                name: "name",
                table: "sandbox_specs",
                newName: "Name");

            migrationBuilder.RenameColumn(
                name: "image",
                table: "sandbox_specs",
                newName: "Image");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "sandbox_specs",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "timeout_minutes",
                table: "sandbox_specs",
                newName: "TimeoutMinutes");

            migrationBuilder.RenameColumn(
                name: "mount_workspace",
                table: "sandbox_specs",
                newName: "MountWorkspace");

            migrationBuilder.RenameColumn(
                name: "memory_mb",
                table: "sandbox_specs",
                newName: "MemoryMb");

            migrationBuilder.RenameColumn(
                name: "env_json",
                table: "sandbox_specs",
                newName: "EnvJson");

            migrationBuilder.RenameColumn(
                name: "cpu_limit",
                table: "sandbox_specs",
                newName: "CpuLimit");

            migrationBuilder.RenameColumn(
                name: "source",
                table: "events",
                newName: "Source");

            migrationBuilder.RenameColumn(
                name: "payload",
                table: "events",
                newName: "Payload");

            migrationBuilder.RenameColumn(
                name: "kind",
                table: "events",
                newName: "Kind");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "events",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "timestamp_ticks",
                table: "events",
                newName: "TimestampTicks");

            migrationBuilder.RenameColumn(
                name: "parent_id",
                table: "events",
                newName: "ParentId");

            migrationBuilder.RenameColumn(
                name: "conversation_id",
                table: "events",
                newName: "ConversationId");

            migrationBuilder.RenameIndex(
                name: "ix_events_conversation_id_timestamp_ticks",
                table: "events",
                newName: "IX_events_ConversationId_TimestampTicks");

            migrationBuilder.RenameIndex(
                name: "ix_events_conversation_id_kind",
                table: "events",
                newName: "IX_events_ConversationId_Kind");

            migrationBuilder.RenameColumn(
                name: "title",
                table: "conversations",
                newName: "Title");

            migrationBuilder.RenameColumn(
                name: "pinned",
                table: "conversations",
                newName: "Pinned");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "conversations",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "updated_at",
                table: "conversations",
                newName: "UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "tags_json",
                table: "conversations",
                newName: "TagsJson");

            migrationBuilder.RenameColumn(
                name: "selected_repository",
                table: "conversations",
                newName: "SelectedRepository");

            migrationBuilder.RenameColumn(
                name: "sandbox_id",
                table: "conversations",
                newName: "SandboxId");

            migrationBuilder.RenameColumn(
                name: "pending_kickoff",
                table: "conversations",
                newName: "PendingKickoff");

            migrationBuilder.RenameColumn(
                name: "llm_model",
                table: "conversations",
                newName: "LlmModel");

            migrationBuilder.RenameColumn(
                name: "execution_status",
                table: "conversations",
                newName: "ExecutionStatus");

            migrationBuilder.RenameColumn(
                name: "created_by_user_id",
                table: "conversations",
                newName: "CreatedByUserId");

            migrationBuilder.RenameColumn(
                name: "created_at",
                table: "conversations",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "confirmation_mode",
                table: "conversations",
                newName: "ConfirmationMode");

            migrationBuilder.RenameColumn(
                name: "agent_type",
                table: "conversations",
                newName: "AgentType");

            migrationBuilder.RenameIndex(
                name: "ix_conversations_execution_status",
                table: "conversations",
                newName: "IX_conversations_ExecutionStatus");

            migrationBuilder.RenameIndex(
                name: "ix_conversations_created_at",
                table: "conversations",
                newName: "IX_conversations_CreatedAt");

            migrationBuilder.RenameColumn(
                name: "status",
                table: "conversation_start_tasks",
                newName: "Status");

            migrationBuilder.RenameColumn(
                name: "detail",
                table: "conversation_start_tasks",
                newName: "Detail");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "conversation_start_tasks",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "updated_at",
                table: "conversation_start_tasks",
                newName: "UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "sandbox_id",
                table: "conversation_start_tasks",
                newName: "SandboxId");

            migrationBuilder.RenameColumn(
                name: "created_at",
                table: "conversation_start_tasks",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "app_conversation_id",
                table: "conversation_start_tasks",
                newName: "AppConversationId");

            migrationBuilder.RenameIndex(
                name: "ix_conversation_start_tasks_status",
                table: "conversation_start_tasks",
                newName: "IX_conversation_start_tasks_Status");

            migrationBuilder.RenameColumn(
                name: "value",
                table: "app_settings",
                newName: "Value");

            migrationBuilder.RenameColumn(
                name: "key",
                table: "app_settings",
                newName: "Key");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "app_settings",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "updated_at",
                table: "app_settings",
                newName: "UpdatedAt");

            migrationBuilder.RenameIndex(
                name: "ix_app_settings_key",
                table: "app_settings",
                newName: "IX_app_settings_Key");

            migrationBuilder.AddPrimaryKey(
                name: "PK_sandboxes",
                table: "sandboxes",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_sandbox_specs",
                table: "sandbox_specs",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_events",
                table: "events",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_conversations",
                table: "conversations",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_conversation_start_tasks",
                table: "conversation_start_tasks",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_app_settings",
                table: "app_settings",
                column: "Id");
        }
    }
}
