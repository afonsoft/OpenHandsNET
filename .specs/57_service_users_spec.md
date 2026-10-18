# SPEC — 57 · Service Users + Scoped Keys + Impersonation

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `service-users` |
| Type | `Feature / Security + API` |
| Stack | `.NET 10 · ASP.NET auth · EF Core` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Delivered` |
| Ticket | `TBD` |
| Depends on | `08` (api keys/orgs/RBAC), `20` (usage) |
| Reference | competitive-analysis §3.5 (G6); docs.devin.ai
  `/api-reference/overview` — `cog_*` service users, roles,
  `create_as_user_id`, PATs |
| Touches | `src/OpenHandsNET.{Application,Infrastructure,Api}`, `cli/`, `tests/` |

## 1. User Story

**As a** admin de org
**I want** criar service users com chaves `svc_*` e escopos RBAC
próprios, e criar sessões em nome de usuários (`create_as_user_id`)
**So that** automações de CI/CD e integrações externas consomem a API
de forma auditável e least-privilege — paridade com a API v3 do Devin.
Hoje `sk-*` carrega a identidade do dono: sem escopos, sem distinção
de ator, sem impersonation.

## 2. Scope (RF)

### RF-001 — Service user entity

- `service_users`: `Id, OrgId, Name, RoleId (role custom da org),
  CreatedBy, DisabledAt`. Identidade separada de `users` — não faz
  login; só consome API.
- Admin endpoints: `POST/GET/PATCH/DELETE
  /api/v1/orgs/{org}/service-users` + `POST .../{id}/keys` (emite
  `svc_{random}` — formato idêntico ao `sk-*`: prefixo visível, hash
  SHA-256, expiração opcional).
- Chave `svc_*` resolve para `ApiKeyResolution` estendido:
  `{ ActorKind=ServiceUser, ServiceUserId, OrgId, RoleId }` — reusa
  `ApiKeyService`/`ApiKeyAuthenticationHandler`.

### RF-002 — Escopos RBAC na chave

- `ApiKeyRecord` + `ScopesJson` (`["conversations:write",
  "conversations:read", "sandboxes:read", "automations:write",
  "admin:usage", "sessions:impersonate", ...]`).
- `IScopedAuthorizationHandler`: endpoint declara scope via
  metadata (`RequireScope("conversations:write")`); chave pessoal
  `sk-*` tem scopes do papel do usuário; `svc_*` tem interseção
  role∩scopes-da-chave.
- 403 genérico (`{"error":"insufficient_scope"}`) sem vazar escopos.

### RF-003 — Impersonation (`create_as_user_id`)

- `POST /api/v1/app-conversations` aceita `create_as_user_id` —
  permitido só com scope `sessions:impersonate` + alvo é membro da
  mesma org. Conversa criada com `CreatedBy=alvo`,
  `CreatedVia=service_user:{id}`.
- Usage/quota atribuída ao **alvo** (SPEC-20); audit registra ambos
  os atores.

### RF-004 — Superfície admin/audit

- Admin UI `Service Users`: list/create/disable/rotate keys,
  role picker, tabela de scopes.
- Audit events: `service_user.created`, `service_user.key_issued`,
  `session.impersonated`, `auth.scope_denied`.
- `GET /api/v1/keys/current` estendido: `{actor_kind, scopes, role}`.

### RF-005 — Migração compat

- `sk-*` existentes: `ActorKind=User`, scopes = defaults do papel —
  zero breaking change.
- Rate limit passa a chavear por ator (user vs service user
  separados).

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| GET/POST | `/api/v1/orgs/{org}/service-users` | admin |
| PATCH/DELETE | `…/service-users/{id}` | rename/role/disable |
| POST | `…/service-users/{id}/keys` | `{name, scopes[], expires_at?}` → `{key: svc_…}` 1× |
| DELETE | `…/service-users/{id}/keys/{keyId}` | |
| GET | `/api/v1/keys/current` | `{actor_kind, scopes, org_id}` |

Erros: `400` escopo desconhecido · `403` insufficient_scope /
impersonate negado · `404`.

## 4. Data model

`service_users`, `api_keys` + `ServiceUserId?` + `ScopesJson`,
conversa + `CreatedVia`. Migrations SQLite+Postgres.

## 5. NFR / Security

- `svc_*` serializa mascarado (`svc_…abcd`); hash só SHA-256.
- Disable de service user revoga sessões futuras imediatamente (cache
  de resolução com invalidação via `IDistributedCache`).
- Impersonation nunca eleva: alvo continua limitado pelas próprias
  permissões e quotas.
- Audit obrigatório nos dois lados (ator + alvo).

## 6. Task Plan

1. Entidades + migrations + `svc_*` issue/resolve + `ActorKind`.
2. `RequireScope` + handler + sweep de endpoints (grupos por domínio).
3. `create_as_user_id` no start-task + audit.
4. Admin UI + CLI (`service-users` commands) + docs.

## 7. Deliverables

Service users com chaves escopadas; endpoints protegidos por scope;
impersonation auditada; admin UI; compat total com `sk-*`.

## 8. Referências

- `migration-plan/research/competitive-analysis-devin-openhands-cloud.md`.
- `.specs/08_enterprise_security_rbac.md`, `.specs/20_plan_enforcement_billing_spec.md`.
- docs.devin.ai `/api-reference/overview`, `/api-reference/authentication`.

## 9. Guardrails

- Scopes são allowlist estática em código (não free-form).
- `sk-*` de usuário nunca recebe `sessions:impersonate` por default —
  opt-in por role.
- Nenhum endpoint novo fora do golden OpenAPI sem snapshot refresh.

## 10. DoD

- `svc_*` com `conversations:read` não cria conversa (403) e lê (200).
- Impersonation registra `session.impersonated` com os dois atores e
  cobra quota do alvo.
- `sk-*` antigo continua funcionando (teste de regressão).
- Admin UI cria/desativa; suite verde; golden OpenAPI atualizado.


## 9. Entrega — evidência (2026-10-18)

- `Application/Identity/ServiceUserContracts.cs` — `ActorKind`,
  `ApiScopes` (catálogo), `IServiceUserService` + DTOs.
- `ServiceUserRecord` + `ApiKeyRecord.ServiceUserId` +
  `ConversationRecord.CreatedVia` (migrations SQLite + Postgres).
- `ServiceUserService` — CRUD + emissão `svc_*` (interseção
  role∩scopes validada na emissão; delete revoga chaves).
- `ApiKeyService.ValidateAsync` — aceita `sk-`/`svc_`; svc resolve
  ActorKind/ServiceUserId/Role/Scopes; DisabledAt revoga na hora.
- `ApiKeyAuthenticationHandler` — claims `actor_kind`, `svc_role`,
  `svc_name`, `scope`; `HttpCurrentUserAccessor` — AuthUser sintético
  p/ service users.
- `PermissionHandler` — role do svc vem do registro (não OrgMembers).
- `ScopeRequirement` + `ScopeHandler` + provider `scope:` +
  `.RequireScope()` ext — 403 `insufficient_scope` + audit
  `auth.scope_denied`. Aplicado em automation (10), secrets (3) e
  service-users (7) endpoints.
- Impersonation: `create_as_user_id` no POST `/app-conversations` —
  scope `sessions:impersonate` + membership da org + `CreatedVia` +
  audit `session.impersonated`.
- `GET /api/v1/keys/current` → `{actor_kind, scopes, role, org_id}`.
- Admin UI `/admin/service-users` (list/create/disable/issue key) +
  CLI `service-users list|create|key`; i18n en/pt;
  `docs/features/service-users.md`.
- Testes: `Spec57ServiceUsersTests` 7/7 (CRUD, role inválida, emissão
  svc_* + resolução, scope desconhecido, disable revoga, delete
  remove chaves, sk-* mantém ActorKind=User).
