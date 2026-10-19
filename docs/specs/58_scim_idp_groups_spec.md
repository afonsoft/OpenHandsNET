# SPEC — 58 · SCIM 2.0 + IdP Group → Org/Role Mapping + Usage Tiers

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `scim-idp-groups` |
| Type | `Feature / Enterprise security` |
| Stack | `.NET 10 · OIDC claims · EF Core` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed` |
| Ticket | `TBD` |
| Depends on | `08` (OIDC/orgs/RBAC), `20` (quotas) |
| Reference | competitive-analysis §3.7 (G7/G21); docs.devin.ai
  `/enterprise/security-access/{scim,idp-groups}`, `/enterprise/features/usage-policies` |
| Touches | `src/OpenHandsNET.{Application,Infrastructure,Api,Client}`, `tests/` |

## 1. User Story

**As a** admin enterprise
**I want** provisionamento SCIM 2.0 (create/update/deprovision de
usuários e grupos) e mapeamento de grupos do IdP para orgs/roles e
tiers de quota
**So that** o ciclo de vida de acesso fica no IdP — gate clássico de
procurement que o Devin tem (SCIM + IdP groups) e nós não. Hoje usuários
entram por primeiro login OIDC e roles são atribuídas manualmente.

## 2. Scope (RF)

### RF-001 — SCIM 2.0 service provider

- Endpoints `/scim/v2/{Users,Groups}` (subset RFC 7643/7644):
  - `GET /Users?filter=userName eq "…"`, `GET/POST /Users`,
    `PATCH /Users/{id}` (`active:false` → deprovision),
    `DELETE /Users/{id}` (soft → desativa).
  - `GET/POST/PUT/DELETE /Groups` com `members`.
  - `GET /ServiceProviderConfig`, `/ResourceTypes`, `/Schemas`.
- Auth: bearer token próprio `scim_*` (por org, gerado em settings —
  mesmo padrão `sk-*`: hash + prefixo).
- Respostas `application/scim+json`; erro SCIM padrão
  `{schemas:[error], status, detail}`.

### RF-002 — IdP group mapping (claims)

- `idp_group_mappings`: `Id, OrgId, IdpGroupId|Name, Role, UsageTierId?`.
- No login OIDC (ou refresh), claim `groups`/`roles` do token é
  avaliado: match → membership + role aplicados **idempotentemente**;
  remoção do grupo no IdP → acesso revisto no próximo login
  (`IdpSync:EnforceOnLogin=true` default — remove memberships que só
  existiam via mapping).
- Conflito mapping direto vs manual: IdP vence para memberships
  originadas de mapping (flag `source=idp` na membership).

### RF-003 — Usage tiers por grupo

- `usage_tiers`: `Id, OrgId, Name, LimitsJson` — mesmos limites do
  plano (conversas/dia, runs concorrentes, tokens/mês, sandbox-s/mês).
- Ordem de resolução do limite efetivo do usuário:
  override manual > tier via IdP mapping > tier default da org.
- `POST /api/v1/users/{id}/usage-override` (admin) com `expires_at?` —
  cobre o "request more quota" do Devin.

### RF-004 — Admin UI + audit

- Settings `Identity`: SCIM endpoint + token (1×), tabela de mappings
  grupo→role/tier, tiers CRUD.
- Audit: `scim.user_provisioned`, `scim.user_deprovisioned`,
  `idp.group_matched`, `idp.membership_revoked`, `usage.tier_applied`.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| * | `/scim/v2/{Users,Groups,ServiceProviderConfig,ResourceTypes,Schemas}` | bearer `scim_*`; `application/scim+json` |
| GET/POST/DELETE | `/api/v1/orgs/{org}/idp-mappings` | admin |
| GET/POST/PATCH/DELETE | `/api/v1/orgs/{org}/usage-tiers` | admin |
| POST | `/api/v1/scim/tokens` | emite `scim_*` 1× |

Erros SCIM: `400` invalidFilter/syntax · `401` · `404` · `409`
uniqueness (userName).

## 4. Data model

`scim_tokens` (org, hash), `idp_group_mappings`, `usage_tiers`,
`org_memberships` + `Source` (`manual|idp`), `user_usage_overrides`.
Migrations SQLite+Postgres.

## 5. NFR / Security

- `scim_*` scope único (SCIM only) — não chama `/api/v1`.
- SCIM users criados `active` só ganham acesso após primeiro OIDC
  login validado (pre-provision ok).
- Deprovision desativa sessões em aberto? Não — apenas bloqueia novos
  logins/runs (documentado; kill switch é o `DisabledAt` do user).
- PII (emails) em audit somente como hash/id.

## 6. Task Plan

1. `scim_tokens` + middleware bearer + Users CRUD ↔ `users`/`memberships`.
2. Groups CRUD ↔ `idp_group_mappings` + membros.
3. Login-time enforcement (claims→mapping→membership).
4. Usage tiers + resolução de limite + overrides.
5. Admin UI + docs (`docs/deployment/identity.md`).

## 7. Deliverables

SCIM subset compatível com Okta/Entra provisioning; login OIDC aplica
grupo→org/role/tier; overrides por usuário; audit completo.

## 8. Referências

- RFC 7643/7644; docs.devin.ai SCIM/IdP-groups/usage-policies.
- `docs/specs/08_enterprise_security_rbac.md`, `docs/specs/20_plan_enforcement_billing_spec.md`.

## 9. Guardrails

- Subset minimalista: sem bulk ops, sem sort avançado — documentar o
  suportado em `ServiceProviderConfig`.
- Sem dependência de SCIM library nova se não houver uma madura ≥7d —
  implementar o subset manualmente (modelo simples).
- Não mexer em `AuthEndpoints` públicos além do hook de login.

## 10. DoD

- `POST /scim/v2/Users` cria user pre-provisioned; `PATCH active=false`
  bloqueia login.
- Login OIDC com `groups:["eng"]` aplica mapping → org+role+tier;
  remoção do grupo revoga no próximo login.
- Efetivo = override > idp tier > org default (teste).
- IdP `filter` inválido → 400 SCIM. Suite verde; golden atualizado.


## 9. Entrega — evidência (2026-10-18)

- Records: `ScimTokenRecord`, `IdpGroupMappingRecord`, `UsageTierRecord`,
  `UserUsageOverrideRecord`; `OrgMemberRecord` + `Source`
  (`manual|idp`) + `UsageTierId`. Migrations SQLite + Postgres.
- `IdentityAdminService` — tokens `scim_*` (prefixo+SHA-256, 1×
  plaintext, revoke), CRUD mappings/tiers, overrides, IdP sync.
- SCIM subset em `/scim/v2`: ServiceProviderConfig/ResourceTypes/
  Schemas; Users GET(filter)/POST/PATCH/DELETE; Groups GET/POST/PUT/
  DELETE (displayName→mapping, members→memberships idp-sourced);
  `application/scim+json` + erro SCIM padrão (invalidFilter/invalid
  Syntax/uniqueness).
- Login-time enforcement no `POST /api/v1/authenticate`: claims
  `groups`/`group`/WS-groups → `ApplyOnLoginAsync` (idempotente,
  best-effort); revoga memberships `source=idp` órfãs.
- `GetEffectiveLimitsAsync(org,user)` no `PlanEnforcementService`:
  override > tier da membership > plano; checks ganharam sobrecarga
  com `userId` — create passa `ownerUserId`, run-start passa
  `CreatedByUserId` (quota do ator/alvo na impersonation).
- Admin UI `/admin/identity` (tokens/mappings/tiers) + i18n en/pt;
  admin API `/orgs/{o}/scim-tokens|idp-mappings|usage-tiers` +
  `/users/{id}/usage-override` (perm-based; scim-tokens também com
  scope `admin:service_users`).
- Divergência documentada: tokens SCIM sob `/orgs/{o}/scim-tokens`
  (consistente com o resto do admin) em vez de `/api/v1/scim/tokens`.
- Testes: `Spec58ScimIdpTests` 5/5 (mint/validate/revoke, sync
  create+revoke, manual-preserva-role+ganha-tier, ordem de resolução
  override→tier→plano, CRUD admin).
