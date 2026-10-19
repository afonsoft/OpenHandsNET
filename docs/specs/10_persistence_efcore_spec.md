# SPEC — 10 · Persistência EF Core (Schema & Migrations)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `persistence-efcore` |
| Type | `Feature / Infra` |
| Stack | `.NET 10 · EF Core 10 · SQLite · Npgsql` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed` (PR #20) |
| Ticket | [#11](https://github.com/afonsoft/OpenHandsNET/issues/11) (Epic [#1](https://github.com/afonsoft/OpenHandsNET/issues/1)) |
| Depends on | `01_architecture_overview` |
| Feeds | todas as SPECs de domínio (02–13 persistem via estas tabelas) |

## 1. User Story

**As a** mantenedor do OpenHandsNET
**I want** um `DbContext` único com schema completo, migrations por provider e
repositories tipados
**So that** cada SPEC de domínio persista de forma consistente — SQLite zero-dep
para local, PostgreSQL para enterprise — sem cada feature reinventar storage.

**Problem context:** upstream usa SQLAlchemy+PostgreSQL (enterprise) e
arquivos/JSON no SDK (`local_storage.py`, persist dir). OpenHandsNET consolida:
EF Core único, entidades como **records de domínio** (SPEC 02) persistidas em
tabelas com coluna JSON de payload — append-only para eventos, CRUD normal para
entidades de controle.

## 2. Scope

**In scope:**
- `OpenHandsNetDbContext` (único) + `IEntityTypeConfiguration` por domínio.
- Tabelas: `app_conversations`, `start_tasks`, `pending_messages`, `sandboxes`,
  `sandbox_specs`, `events`, `settings`, `secrets`, `mcp_servers`, `skills`,
  `webhook_registrations`, `users`, `api_keys`, `organizations`, `org_members`,
  `org_invitations`, `audit_events`, `plans`, `subscriptions`, `usage_records`,
  `data_protection_keys` (ASP.NET DP).
- Provider switch: `Database__Provider=Sqlite|PostgreSql` +
  `Database__ConnectionString` (SQLite default `data/openhandsnet.db`, WAL).
- `DatabaseMigrator` (hosted service): `MigrateAsync` no boot + baseline de
  bases legadas (`__EFMigrationsHistory` seed) — padrão open-webui-net.
- Migrations separadas por provider (`Migrations/Sqlite/`, `Migrations/Postgres/`).
- Convenções: PKs `TEXT`/`uuid` ULID string; `created_at`/`updated_at` UTC
  (`DateTimeOffset`); colunas JSON (`jsonb` PG / `TEXT` SQLite via value
  conversion); snake_case table/column names (`EFCore.NamingConventions`).
- Global query filter `OrgId` (nullable) — só aplicado quando
  `MultiTenancy__Enabled=true` (SPEC 08).
- `IRepository<T>`/`*Store` impls em `Infrastructure/Persistence/Stores/` que
  implementam as interfaces de `Application`.

**Out of scope:** EF migrations auto-geradas em produção (só `MigrateAsync`);
Redis/cache distribuído; sharding/read-replicas; Event Sourcing formal
(o append-only `events` já cobre); backups automáticos (docs only).

## 3. Technical Context

**Upstream (referência de modelo):**
- `storage/` no enterprise (SQLAlchemy models: conversation, sandbox, org,
  api_key, quota, billing) — conceitos Polyform.
- SDK `local_storage.py` (persist dir JSON) → inspira `workspaces/` no host.
- Upstream `schema_version` + golden fixtures de settings compat
  (`sdk/AGENTS.md` §persisted settings) → adotar mesma disciplina.

**Where:**
- `Infrastructure/Persistence/{OpenHandsNetDbContext,DatabaseMigrator}.cs`
- `Infrastructure/Persistence/Configurations/*.cs` (um por entidade)
- `Infrastructure/Persistence/Stores/*Store.cs`
- `Domain/` já contém entidades de SPEC 02/03/08/09 — esta SPEC só as mapeia.

## 4. Requirements

### RF-001: Entidades e tabelas

| Tabela | PK | Índices-chave | Notas |
|---|---|---|---|
| `app_conversations` | `id` | `(owner_id, created_at)`, `(org_id)` | `title`, `sandbox_status`, `execution_status`, `selected_repository`, `agent_type`, `llm_model`, `confirmation_mode`, `archived_at`, `tags` JSON, `pinned` |
| `start_tasks` | `id` | `(status, created_at)` | `app_conversation_id` nullable, `sandbox_id`, `error`, payload request JSON |
| `pending_messages` | `id` | `(conversation_id, created_at)` | `role`, `content` JSON, `run`, `processed_at` |
| `sandboxes` | `id` | `(conversation_id)`, `(status)` | `spec_id`, `container_id`, `agent_url`, `last_error` |
| `sandbox_specs` | `id` | `(name)` | `image`, `memory_mb`, `cpu_limit`, `timeout_minutes`, `env` JSON |
| `events` | `id` | `(conversation_id, timestamp)`, `(conversation_id, kind)`, `(parent_id)` | `conversation_id`, `source`, `kind`, `payload` JSON — **append-only** |
| `settings` | `(user_id, key)` | — | `value` JSON, `updated_at` |
| `secrets` | `(scope, owner_id, name)` | — | `ciphertext`, `nonce`, `meta` JSON — nunca plaintext |
| `mcp_servers` | `(owner_id, name)` | — | config JSON (SPEC 07), `enabled` |
| `skills` | `id` | `(owner_id, name)` | `content` md, `triggers` JSON, `source` |
| `webhook_registrations` | `id` | `(owner_id)`, `(conversation_id)` | `url`, `secret` cipher, `kinds` JSON |
| `users` | `id` | `(email unique)` | `display_name`, `roles` JSON, `disabled_at` |
| `api_keys` | `id` | `(prefix)`, `(user_id)` | `key_hash`, `name`, `scopes` JSON, `expires_at`, `last_used_at` |
| `organizations` | `id` | `(slug unique)` | `name`, `plan_id` |
| `org_members` | `(org_id, user_id)` | `(user_id)` | `role` |
| `org_invitations` | `id` | `(token unique)`, `(org_id, email)` | `role`, `expires_at`, `accepted_at` |
| `audit_events` | `id` | `(org_id, ts)`, `(user_id, ts)`, `(action)` | payload JSON — **append-only** |
| `plans`/`subscriptions`/`usage_records` | `id` | `(org_id, period)` | contratos billing (SPEC 08) |
| `data_protection_keys` | `id` | — | ASP.NET DP `PersistKeysToDbContext` |

### RF-002: DbContext + convenções
- `UseSnakeCaseNamingConvention()`; ULIDs como `string` com comparação ordinal;
  `DateTimeOffset` UTC sempre.
- JSON columns via `OwnsOne`/conversion `JsonSerializer` com o mesmo
  `JsonSerializerOptions` do wire contract (snake_case) — reutilizar o de SPEC 04.
- `events.payload` armazena o evento serializado completo (kind + fields);
  colunas de índice (`kind`, `source`, `parent_id`, `timestamp`) desnormalizadas
  para query sem JSON-extract.

### RF-003: Provider switch + migrator
- `AddDbContext` escolhe provider por `DatabaseOptions.Provider`; `MigrationsAssembly`
  por provider (`Migrations_Sqlite`, `Migrations_Postgres` — dois assemblies ou
  pastas com `--output-dir` + `--namespace`, decisão: **um assembly, duas pastas**).
- `DatabaseMigrator` roda `MigrateAsync()` antes de aceitar tráfego (hosted
  service bloqueante no boot, `IStartupFilter`/`await` em Program).
- `sqlite3` pragma: `journal_mode=WAL`, `busy_timeout=5000`, `foreign_keys=ON`.

### RF-004: Stores
- Cada `I*Service`/`*Store` de Application com impl EF em
  `Infrastructure/Persistence/Stores/`; repositories finos (sem lógica de domínio).
- `EventStore` respeita append-only (sem `Update`/`Remove` na entity).
- Paginação cursored (`page_id` = base64 de `(timestamp,id)`) compartilhada em
  `CursorPaginator` helper.

**Invariants:** nenhuma FK para `events` (append-only standalone); org filter
só ativo com multi-tenancy; nunca `EnsureCreated` (sempre migrations);
`AsNoTracking` em reads de listagem/search.

## 5. API Contract

Nenhum endpoint novo. Config exposta via `/api/v1/config` → `database.provider`
(somente o nome, nunca a connection string).

## 6. Critérios de Aceite

- [ ] `dotnet ef migrations add` gera migrations válidas para **ambos** providers.
- [ ] Boot com SQLite cria todas as tabelas; `PRAGMA journal_mode` = WAL.
- [ ] Boot com `Database__Provider=PostgreSql` (Testcontainers) aplica migrations.
- [ ] Round-trip de um `Event` complexo persiste e reconstroi payload intacto.
- [ ] Query filter `OrgId` com multi-tenancy on esconde dados de outra org.
- [ ] `DatabaseMigrator` é idempotente (re-run sem efeito) e bloqueia boot em falha.

## 7. Task Plan

1. DbContext + naming + config base + `DatabaseOptions` (verify: `ef migrations add` Sqlite).
2. Configurations de todas as tabelas + índices (verify: `dotnet ef migrations script` inspeção).
3. `DatabaseMigrator` + pragmas SQLite + provider switch (verify: boot tests).
4. Stores por domínio + `CursorPaginator` (verify: integration tests).
5. Migrations Postgres + Testcontainers suite (verify: CI job com service).
6. DataProtection `PersistKeysToDbContext` (verify: restart mantém keys).

## 8. Deliverables — arquivos a criar/modificar

```text
src/OpenHandsNET.Infrastructure/Persistence/OpenHandsNetDbContext.cs
src/OpenHandsNET.Infrastructure/Persistence/DatabaseMigrator.cs
src/OpenHandsNET.Infrastructure/Persistence/DatabaseOptions.cs      # Provider+ConnString
src/OpenHandsNET.Infrastructure/Persistence/CursorPaginator.cs      # page_id base64(ts,id)
src/OpenHandsNET.Infrastructure/Persistence/Configurations/
  {AppConversation,StartTask,PendingMessage,Sandbox,SandboxSpec,Event,Settings,
   Secret,McpServer,Skill,WebhookRegistration,User,ApiKey,Organization,OrgMember,
   OrgInvitation,AuditEvent,Plan,Subscription,UsageRecord}Configuration.cs
src/OpenHandsNET.Infrastructure/Persistence/Stores/
  {ConversationStore,StartTaskStore,SandboxStore,EventStore,SettingsStore,
   SecretStore,SkillStore,IdentityStores}.cs
src/OpenHandsNET.Infrastructure/Migrations/Sqlite/…                 # geradas
src/OpenHandsNET.Infrastructure/Migrations/Postgres/…               # geradas
tests/OpenHandsNET.IntegrationTests/Persistence/{EventStoreTests,
  CursorPaginationTests,OrgFilterTests,PostgresMigrationTests}.cs
```

## 9. Referências e links

### Internas (repo)
- `docs/specs/02_event_stream_spec.md` (tabela `events`) · `03` (sandboxes) ·
  `04` (paginação `page_id`) · `07` (mcp_servers) · `08` (identity/audit/orgs) ·
  `11` (skills) · `13` (DataProtection).
- `migration-plan/research/stack-research.md` — decisão SQLite+PG aprovada.

### Upstream — conceitos
- Storage models (Polyform): https://github.com/OpenHands/enterprise/tree/693589e/storage/
- `local_storage.py` (SDK, MIT): https://github.com/OpenHands/software-agent-sdk/blob/acca181/openhands-agent-server/openhands/agent_server/local_storage.py
- Política de persisted settings + golden fixtures:
  https://github.com/OpenHands/software-agent-sdk/blob/acca181/openhands-sdk/openhands/sdk/AGENTS.md

### Bibliotecas / framework
- EF Core: https://learn.microsoft.com/ef/core/
- Migrations: https://learn.microsoft.com/ef/core/managing-schemas/migrations/
- SQLite provider: https://learn.microsoft.com/ef/core/providers/sqlite/
- Npgsql EF: https://www.npgsql.org/efcore/
- Naming conventions snake_case: https://github.com/efcore/EFCore.NamingConventions
- Global query filters: https://learn.microsoft.com/ef/core/querying/filters
- Value conversions (JSON): https://learn.microsoft.com/ef/core/modeling/value-conversions
- `dotnet ef` CLI: https://learn.microsoft.com/ef/core/cli/dotnet
- DataProtection → EF: https://learn.microsoft.com/aspnet/core/security/data-protection/configuration/overview (`PersistKeysToDbContext`)
- Testcontainers Postgres: https://dotnet.testcontainers.org/modules/postgres/

## 10. Guardrails de execução (Devin)

- `EnsureCreated`/`EnsureDeleted` **proibidos** em código de produção (migrations only).
- `events` e `audit_events` append-only: sem `Update`/`Delete` — code review deve rejeitar.
- Connection string nunca logada/serializada (`/config` expõe só `provider`).
- Reads de listagem sempre `AsNoTracking`; writes com `SaveChangesAsync` único.
- Pré-requisito Devin: `dotnet tool install --global dotnet-ef` para gerar migrations;
  Postgres de teste via Testcontainers (Docker) — pular `[Category("Docker")]` se sem daemon.
- Migrations commitadas como código-fonte (nunca gerar em runtime fora do Migrator).

## 11. Definition of Done

- [ ] Schema completo §RF-001 materializado nos dois providers.
- [ ] `dotnet ef migrations script` idempotente entre migrations.
- [ ] Boot SQLite/Postgres aplicam migrations via `DatabaseMigrator`.
- [ ] Org filter + cursor pagination + append-only provados por testes.
- [ ] `dotnet test` verde; SPEC `Done`; issue fechada pelo PR.
