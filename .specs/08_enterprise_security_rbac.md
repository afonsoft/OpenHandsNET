# SPEC — 08 · Enterprise: Security, Auth, Orgs, Quota & Audit

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `enterprise-security-rbac` |
| Type | `Feature / API / Infra` |
| Stack | `.NET 10 · ASP.NET Core Auth · OIDC · EF Core` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` — v2 fiel ao `enterprise/server/` real (reescrita aprovada) |
| Ticket | [#9](https://github.com/afonsoft/OpenHandsNET/issues/9) (Epic [#1](https://github.com/afonsoft/OpenHandsNET/issues/1)) |
| Depends on | `04_rest_and_signalr_api_spec` |
| Upstream | `enterprise/server/{auth,routes}/` — **Polyform Free Trial: conceitos/contratos apenas; todo o código C# é reimplementação original** |

## 1. User Story

**As a** organização que self-hosta o OpenHandsNET
**I want** SSO via broker OIDC (Keycloak), API keys com binding de org, RBAC
baseado em permissões com dois níveis de role, provisioning admin e trilha de
auditoria
**So that** múltiplas equipes usem a plataforma com isolamento e governança
reais — o diferencial "enterprise" do OpenHands, reimplementado 100% open-source.

**Problem context:** a versão anterior desta SPEC modelava "SAML/Okta/Azure AD
genérico" + RBAC por role. A leitura do código real corrigiu:

1. **Auth = Keycloak como broker OIDC** — fluxo `GET /oauth/keycloak/callback`
   troca `code` por tokens Keycloak, resolve `user_info` e grava cookie JWT
   assinado e *chunked* (`keycloak_auth`). SAML/Okta/Entra são **IdPs federados
   atrás do Keycloak**, detectados via atributo `idp_type` (oidc/saml) — não
   integrações diretas (`auth.py:254-296`, `448-468`).
2. **Dois modos coexistem**: cookie (browser) e `Bearer` API key
   (`AuthType.BEARER`); `GET /api/keys/current` só funciona com API key
   (`api_keys.py:386-402`).
3. **API keys têm binding de org**: bound ou unbound; org efetiva resolvida por
   request na ordem `X-Org-Id` header > binding da key > `user.current_org_id`
   (`api_keys.py:116-124`, `170-185`).
4. **RBAC é permission-based**: enum `Permission` mapeado a roles via
   `ROLE_PERMISSIONS`; roles `owner|admin|member` (`authorization.py:48-226`).
5. **Dois níveis de role**: org-scoped (`org_member.role_id`) e super-role
   (`user.role_id`); superadmin é instance-level com permissões explícitas
   (`SUPER_ROLE_PERMISSIONS`), sem herança de org perms; checagem faz fallback
   org-role → super-role (`authorization.py:472-494`).
6. **Invariante**: impossível remover o último super admin → `409`
   (`super_admins.py:255-256`).
7. **Device flow** emite **API key com expiração de 7 dias** (não JWT)
   (`oauth_device.py:24-25`, `217-220`).
8. **Provisioning admin** cria usuário Keycloak pré-verificado + pula
   TOS/onboarding + integração LiteLLM + retorna email/senha/API key, com
   **rollback compensatório** (`user_provisioning.py:251-275`).
9. **Invitations** são batch + token; admin não pode convidar como `owner`
   (`org_invitations.py:293-297`).
10. **"Auditoria" upstream = logging estruturado + PostHog analytics** — não há
    tabela de audit trail (`super_admins.py:124-127`, `analytics_events.py`).
    Nossa `AuditLog` dedicada é **melhoria** (SOC2/ISO27001), não port.

## 2. Scope

**In scope:**
- **OIDC via broker** (`Auth__Provider=oidc`): Keycloak como impl de referência;
  Entra/Okta/Dex como IdPs OIDC equivalentes (SAML fica a cargo do broker).
  `Microsoft.AspNetCore.Authentication.OpenIdConnect` + callback + cookie
  `openhandsnet_auth` assinado (DataProtection), chunked > 4KB.
- **Dual auth**: cookie session (browser) + `Bearer sk-*` (API key); JWT bearer
  opcional para compat (`Auth__EnableJwt`).
- **API keys**: bound/unbound a org; resolução de org efetiva por request;
  `GET /keys/current` Bearer-only; BYOR LLM keys → fase posterior (contrato).
- **RBAC permission-based**: `Permission` enum + `ROLE_PERMISSIONS` +
  `SUPER_ROLE_PERMISSIONS`; policies ASP.NET Core
  `[Authorize(Policy="perm:manage_secrets")]` via `IAuthorizationHandler`
  `RequirePermission` com fallback org→super.
- **Multi-tenancy real**: org efetiva resolvida por request (não por login);
  `OrgMember.RoleId` vs `User.RoleId` (super-role); org suspended state.
- **Orgs**: CRUD, members, settings, financial, invitations, provisioning,
  super admins, quota, billing contracts, feature flags.
- **Audit**: `audit_log` append-only (adição OpenHandsNET) + logging estruturado
  (paridade) + analytics externo opcional (`Analytics__Provider=none|posthog`).
- **OSS single-user**: `deployment_mode=oss` → primeiro usuário = owner de org
  default + admin de instância; multi-tenancy ligado só em `self_hosted|cloud`.

**Out of scope:** SAML 2.0 nativo no app (delegado ao broker), SCIM, SSO
enforcement, recaptcha/sheets sync, BYOR LiteLLM keys (fase 2 — LiteLLM opcional
SPEC 13), providers git/PM externos (fase 3), Stripe impl (contrato apenas),
org suspension UI (campo + enforcement sim, dashboard não).

## 3. Technical Context

**Upstream (evidência — Polyform, não portar código):**

| Arquivo | O que prova |
|---|---|
| `server/auth/authorization.py` | `Permission` enum (~30), `RoleName`, `ROLE_PERMISSIONS`, `SUPER_ROLE_PERMISSIONS`, `require_permission`, `authorize_permission` (fallback + suspended org), `require_financial_data_access` |
| `server/routes/auth.py` | `oauth_router /oauth` + `api_router /api`: `/oauth/keycloak/callback`, `/keycloak/offline/callback`, `/github/callback`, `/api/authenticate`, cookie `keycloak_auth` chunked, TOS/onboarding, `idp_type` |
| `server/auth/cookie_chunking.py` | cookie >4KB dividido em partes indexadas |
| `server/routes/api_keys.py` | `ApiKeyCreate{org_id?}`, `ApiKeyResponse`, `CurrentApiKeyResponse{org_id,bound_org_id,auth_type}`, BYOR `/llm/byor*` |
| `server/routes/orgs.py` | `/api/organizations` CRUD/members/settings/financial |
| `server/routes/org_invitations.py` | `/api/organizations/{org}/members/invite` (batch) + `/accept` (token GET redirect + POST) |
| `server/routes/user_provisioning.py` | `/api/organizations/provision-user` + rollback |
| `server/routes/super_admins.py` | `/api/admin/super-admins` + `/me` + last-admin 409 |
| `server/routes/oauth_device.py` | `/oauth/device/{authorize,token,verify-authenticated}`, code 600s, interval 5s, key 7d |
| `server/routes/{quota,billing,feature_flags,analytics_events}.py` | quota/billing/flags/analytics |

**Where (.NET):**
- `Domain/Identity/`: `User{Id,Email,DisplayName,RoleId?}` (super-role),
  `Organization{Id,Slug,Name,PlanId,SuspendedAt}`, `OrgMember{OrgId,UserId,RoleId}`,
  `Role{Name}`, `OrgInvitation`, `ApiKey{Id,Name,KeyHash,Prefix,OrgId?,NotBefore?,
  ExpiresAt,LastUsedAt,UserId}`, `DeviceCode`, `AuditLog`, `Permission` (enum),
  `RolePermissions` (mapa estático).
- `Infrastructure/Identity/`: `EffectiveOrgResolver`, `PermissionAuthorizationHandler`,
  `ApiKeyService`, `DeviceFlowService`, `ProvisioningService`, `SecretStore`.
- `Api/`: `Auth/{AuthEndpoints,OAuthEndpoints,KeysEndpoints,OrgEndpoints,
  AdminEndpoints}`, `Middleware/{EffectiveOrgMiddleware,DeploymentModeMiddleware}`,
  `Policies/PermissionPolicy.cs` (`RequirePermission` → `IAuthorizeData`/handler).

## 4. Requirements

### RF-001: Autenticação OIDC (broker)
- `Auth__Provider=oidc` + `Auth__Authority` + `Auth__ClientId/Secret` →
  code flow com PKCE; `GET /oauth/oidc/callback` (equiv. `keycloak/callback`)
  valida tokens, resolve `userinfo`, provisiona `User` local (JIT) e emite
  cookie `openhandsnet_auth` assinado (DP API), **chunked** quando > 4KB
  (`openid_auth.0`, `.1`, … — paridade `cookie_chunking.py`).
- Pós-callback: verificação de email pendente → verify page; TOS não aceito →
  `accept_tos`; sem org e multi-tenancy on → onboarding (`/onboarding`), com
  `returnTo` preservado (paridade `_build_onboarding_redirect`).
- `idp_type` do broker (claim/atributo) propagado para analytics/UI; SAML fica
  no broker — o app nunca fala SAML.
- `GET /oauth/oidc/offline/callback` (equiv. offline): renova sessão **sem**
  reescrever o cookie principal (paridade linha 793).
- Logout front-channel/back-channel conforme broker; `Auth__Provider=none` →
  OSS local (dev login form / first-user bootstrap).

### RF-002: Dual auth — cookie + Bearer API key
- Pipeline: `AuthenticationSchemes = "cookie,apikey,jwt"`;
  `AuthType ∈ {Cookie, Bearer}` exposto em `HttpContext` (`CurrentApiKeyResponse.auth_type`).
- `GET /api/v1/keys/current` **só responde a Bearer `sk-*`** → `401/403` em cookie
  (paridade estrita).
- API key resolve `user_id` + `bound_org_id` + atualiza `last_used_at` (async,
  fora do hot path).

### RF-003: API keys — binding de org
```csharp
public sealed record ApiKeyCreate(string? Name, DateTimeOffset? NotBefore,
    DateTimeOffset? ExpiresAt, Guid? OrgId);   // OrgId null → unbound key
// resposta create: ApiKeyCreateResponse = ApiKeyResponse + { key = "sk-…" } (1×)
```
- `ApiKey{Id:int, UserId, OrgId?}` — `OrgId=null` = unbound.
- **Org efetiva por request** (ordem exata upstream):
  `X-Org-Id` header (se user é membro) → `bound_org_id` da key → `user.current_org_id`.
- `CurrentApiKeyResponse{org_id /* efetiva */, bound_org_id /* persistida ou null */}`.
- Validações upstream: `expires_at` no futuro; `not_before < expires_at`; create
  bound exige membership na org; list/delete escopados à org efetiva.
- BYOR (`/keys/llm/byor|refresh|permitted`): contrato reservado, `403` até
  LiteLLM configurado (paridade `_validate_litellm_enabled_for_managed_keys`).

### RF-004: RBAC permission-based
- `Permission` enum (paridade completa):
  `manage_secrets, manage_org_secrets, manage_mcp, manage_integrations,
  manage_integration_providers, manage_application_settings, manage_api_keys,
  view_llm_settings, edit_llm_settings, view_billing, add_credits,
  invite_user_to_organization, change_user_role:member|admin|owner,
  view_org_settings, change_organization_name, delete_organization,
  create_organization, edit_org_settings, manage_org_claims, view_automations,
  manage_automations, provision_user, view_org_conversations,
  manage_super_admins, manage_feature_flags, manage_org_quota,
  create_signup_link, manage_oauth_providers`.
- `ROLE_PERMISSIONS` (estático em `Domain/Identity/RolePermissions.cs`):
  - **owner**: tudo do admin + `change_user_role:owner`, `change_organization_name`,
    `delete_organization`.
  - **admin**: manage_secrets/org_secrets/mcp/integrations(+providers)/
    app_settings/api_keys, edit+view llm_settings, view_billing+add_credits,
    invite + change role member/admin, view+edit org_settings, manage_org_claims,
    view+manage automations, provision_user, view_org_conversations.
  - **member**: manage_secrets/mcp/integrations/app_settings/api_keys,
    view org+llm settings, view_automations.
- `SUPER_ROLE_PERMISSIONS` (só `superadmin` funcional = `user.role_id→admin`):
  `create_organization, provision_user, invite_user_to_organization,
  manage_super_admins, manage_feature_flags, manage_org_quota,
  create_signup_link, manage_oauth_providers`. **Não herda** org perms.
- Enforcement: `IAuthorizationHandler` executando `authorize_permission`:
  resolve org alvo (`{org_id}` route > `X-Org-Id` > effective) → checa org-role
  (bloqueado se `org.SuspendedAt`, exceto super admin) → **fallback super-role**
  → `403 Missing required permission: {perm}`.
- API key bound a outra org que o `{org_id}` da rota → `403` (binding check
  antes da permissão — paridade do passo 3 do `require_permission`).
- `RequireFinancialDataAccess` — policy separada p/ dados financeiros.

### RF-005: Organizations
- `POST /api/v1/organizations` (perm `create_organization` — **só super-role**
  em self_hosted; em OSS o primeiro usuário ganha org default).
- `GET /organizations` (minhas, paginado), `GET|PATCH|DELETE /{id}`
  (delete → `delete_organization`), `GET /{id}/settings` + `PATCH`
  (`view/edit_org_settings`).
- Members: `GET /{id}/members[?role]`, `GET /{id}/members/count`,
  `PATCH /{id}/members/{userId}` (`change_user_role:{target}` — role alvo
  decide a permissão exigida), `DELETE /{id}/members/{userId}`,
  `GET /{id}/members/financial` (`require_financial_data_access`).
- Membership implícita em org efetiva: request a `{org_id}` sem membership → `403`.

### RF-006: Invitations (batch + token)
- `POST /api/organizations/{org_id}/members/invite` `{emails[], role}` → cria
  convites pendentes (token `inv_…`, expira 7d); regra: **admin não pode
  convidar como `owner`** (`change_user_role:{role}` requerida).
- `GET /{org_id}/members/invite` (pendentes), `DELETE /{org_id}/members/invite/{id}`.
- `GET /api/organizations/members/invite/accept?token=` → redirect p/ signup;
  `POST …/accept` `{token}` → cria membership, marca `accepted_at`.
- `org.auto_add` (setting): domínio de email allowlist → auto-join sem convite.

### RF-007: User provisioning (instance admin)
- `POST /api/v1/organizations/provision-user` `{email, name, org_id, role,
  generate_password}` — exige `provision_user` (super-role ou org admin).
- Cria usuário no broker pré-verificado (email verified, sem TOS/onboarding),
  cria membership, emite API key inicial; resposta `{email, password, api_key}`.
- **Rollback compensatório**: falha pós-criação deleta usuário do broker +
  registros locais (paridade `user_provisioning.py` compensations).
- `USER_PROVISIONING_ENABLED` feature flag gateia o endpoint.

### RF-008: Super admins (instance level)
- `GET /api/v1/admin/super-admins` (lista; com `USER_PROVISIONING_ENABLED` qualquer
  autenticado lista — paridade OHE-3196), `POST` (grant por `user_id|email`),
  `DELETE /{user_id}` (revoke), `GET /admin/super-admins/me` (self status, sem gate).
- **409 ao revogar o último super admin** — invariante obrigatória.
- Admin users: `GET /admin/users`, `GET /admin/users/count`,
  `DELETE /admin/users/{id}` (também 409 no último super admin),
  `POST /admin/users/bulk-allow-match` (opt-in por email para match futuro).

### RF-009: Device flow (RFC 8628) — CLI
- `POST /oauth/device/authorize` → `{device_code, user_code, verification_uri,
  verification_uri_complete, expires_in:600, interval:5}`.
- `POST /oauth/device/token` `{device_code}` → `access_token` = **API key**
  `sk-*` criada com `expires_at = +7d`, nome `"Device Link Access Key"`;
  erros: `authorization_pending`, `slow_down` (+`interval`), `expired_token`,
  `access_denied`, `invalid_device_code`.
- `GET /oauth/device/verify?user_code=` (page) + `POST …/verify-authenticated`
  (autoriza/nega — session cookie).
- Expiração configurável: `Auth__DeviceKeyExpirationDays=7`.

### RF-010: Quota & billing (contratos)
- `ConversationQuota{daily_max, concurrent_max}` por org; reserva atômica no
  `POST /app-conversations`; `429 quota_exceeded` + `Retry-After`; auto-pause
  das conversas mais antigas (paridade).
- `MANAGE_ORG_QUOTA` é **instance-level** — owner de org **não** pode alterar a
  própria cota (lição upstream: permissão fora do role org).
- `Plan`/`Subscription`/`UsageRecord` + `IBillingProvider` (`view_billing`,
  `add_credits`) — Stripe `A DEFINIR`, desabilitado por default.

### RF-011: Audit
- `audit_log(id, actor_user_id, org_id?, action, target_type, target_id, ip,
  user_agent, metadata jsonb, created_at)` — **append-only**, escrita por
  pipeline (`IAuditService.RecordAsync`) em: login/logout, api-key
  create/delete/use-falha, grant/revoke roles & super admins, provision-user,
  org create/delete/member change, secrets write, quota change.
- `GET /api/v1/admin/audit[?user&action&from&to&page]` + export CSV/JSON
  (super admin).
- Logging estruturado com `actor_user_id|target_user_id` (paridade) +
  `Analytics__Provider=none|posthog` desligável (paridade opcional).

### RF-012: Secrets & org-scoped resources
- `MANAGE_SECRETS` (user secrets) vs `MANAGE_ORG_SECRETS` (org-shared:
  admin/owner gerenciam; **todos os membros usam** em conversas — leitura de
  nomes liberada a membros).
- Store cifrado AES-256 (`SecretStore`, SPEC 10 `secrets` table); serialização
  mascarada sempre.
- EF global query filter `OrgId == effective_org` quando `MultiTenancy__Enabled`
  (SPEC 10); effective org do middleware, não do JWT.

### RF-013: deployment_mode
- `oss|self_hosted|cloud` em `/api/v1/config` + header `X-Deployment-Mode` —
  **distinto de `app_mode`** (licença). Multi-tenancy/super admins só ativos
  fora de `oss` (salvo flags `ENABLE_SUPER_ADMIN`, `USER_PROVISIONING_ENABLED`).

**Invariants:** nunca retornar segredo em claro; `sk-*` armazenado como SHA-256
(prefixo indexado p/ lookup); cookie assinado+cifrado via DataProtection;
último super admin irremovível; org efetiva resolvida por request — nunca
congelada no token.

## 5. API Contract

### Auth

| Método | Path | Auth | Notas |
|---|---|---|---|
| GET | `/oauth/oidc/login` | — | redirect ao broker (returnTo) |
| GET | `/oauth/oidc/callback` | — | troca code → cookie chunked |
| GET | `/oauth/oidc/offline/callback` | cookie | refresh silencioso |
| POST | `/api/v1/authenticate` | cookie | `{user, orgs[], idp_type}` (paridade `/api/authenticate`) |
| POST | `/api/v1/auth/accept_tos` | cookie | marca TOS |
| GET | `/api/v1/auth/onboarding_status` | cookie | `{needs_onboarding, valid_offline_token}` |
| POST | `/api/v1/auth/logout` | cookie | clear cookie + broker logout |

### API keys (`/api/v1/keys`)

| Método | Path | Perm | Notas |
|---|---|---|---|
| POST | `/api/v1/keys` | `manage_api_keys` | body `ApiKeyCreate` → `{key}` 1× |
| GET | `/api/v1/keys` | `manage_api_keys` | escopo = org efetiva |
| DELETE | `/api/v1/keys/{id}` | `manage_api_keys` | 404 se não pertence à org efetiva |
| GET | `/api/v1/keys/current` | Bearer only | `CurrentApiKeyResponse` |
| GET/POST/DELETE | `/api/v1/keys/llm/byor[/refresh|/permitted]` | — | reservado; `403` sem LiteLLM |

### Orgs (`/api/v1/organizations`)

| Método | Path | Perm |
|---|---|---|
| GET | `/organizations` | — (lista minhas) |
| POST | `/organizations` | `create_organization` (super) |
| GET/PATCH/DELETE | `/organizations/{id}` | `view_org_settings`/`edit_org_settings`/`delete_organization` |
| GET | `/{id}/members` · `/{id}/members/count` | membership |
| PATCH/DELETE | `/{id}/members/{uid}` | `change_user_role:{role}` |
| GET | `/{id}/members/financial` | financial access |
| GET/PATCH | `/{id}/settings` | `view|edit_org_settings` |
| POST/GET/DELETE | `/{id}/members/invite[/{invId}]` | `invite_user_to_organization` + `change_user_role:{role}` |
| POST | `/organizations/members/invite/accept` | cookie (token) |
| GET | `/organizations/{id}/quota` | `view_billing` |
| PUT/DELETE | `/organizations/{id}/quota` | `manage_org_quota` (super) |
| POST | `/organizations/provision-user` | `provision_user` |
| GET/PUT/DELETE | `/organizations/{id}/secrets[/{name}]` | `manage_org_secrets` |

### Admin & device

| Método | Path | Perm |
|---|---|---|
| GET/POST/DELETE | `/api/v1/admin/super-admins[/{uid}]` | `manage_super_admins` (list liberado c/ flag) |
| GET | `/api/v1/admin/super-admins/me` | cookie |
| GET | `/api/v1/admin/users[?q]` · `/count` | `manage_super_admins` |
| DELETE | `/api/v1/admin/users/{id}` | `manage_super_admins` (409 último) |
| GET | `/api/v1/admin/audit` | `manage_super_admins` |
| GET | `/api/v1/admin/stats` · `/api/v1/readiness` | admin |
| POST | `/oauth/device/authorize` · `/oauth/device/token` | — |
| GET/POST | `/oauth/device/verify[-authenticated]` | cookie |
| GET/POST/DELETE | `/api/v1/admin/feature-flags[/{name}]` | `manage_feature_flags` |

## 6. Critérios de Aceite

- [ ] Login OIDC (Keycloak fixture) → cookie `openhandsnet_auth` chunked válido; `POST /authenticate` retorna user+orgs+`idp_type`.
- [ ] API key **bound** resolve org persistida; **unbound** honra `X-Org-Id` > `current_org_id`; `/keys/current` devolve `org_id` efetiva + `bound_org_id`.
- [ ] `RequirePermission` nega `403 Missing required permission: …` sem permissão; org suspended bloqueia member mas admite super admin (fallback provado).
- [ ] `DELETE /admin/super-admins/{last}` → `409`; grant+revoke auditados.
- [ ] Device flow end-to-end emite `sk-*` com expiração 7d; erros `authorization_pending/slow_down/expired_token/access_denied` corretos.
- [ ] `provision-user` cria usuário utilizável (login + key) e faz **rollback** em falha forçada (teste com provider fake que falha no passo N).
- [ ] Invite batch: admin convidando `owner` → `403`; accept token cria membership.
- [ ] `audit_log` registra login, key use, grant/revoke, provision, secret write — consultável em `/admin/audit`.
- [ ] Key bound à org B acessando `{org_id}` da org A → `403` (binding check).

## 7. Task Plan

1. `Permission`/`RolePermissions` + `EffectiveOrgResolver` + `RequirePermission` handler (verify: unit tests da matriz).
2. Identity base: cookie auth OIDC + JIT user + `sk-*` service + binding/effective-org (verify: auth flow tests).
3. Orgs/members/invitations + suspended + auto-add (verify: isolation tests).
4. Super admins + admin users + last-admin invariant + provisioning+rollback (verify: 409 & compensation tests).
5. Device flow + `keys/current` Bearer-only (verify: RFC 8628 flow + CLI E2E).
6. Quota service + 429 + billing contracts (verify: concurrency tests).
7. `AuditLog` pipeline + admin endpoints + analytics provider none/posthog (verify: audit tests).
8. Secrets user/org + query filter + `deployment_mode` middleware (verify: serialization tests).

## 8. Deliverables — arquivos a criar/modificar

```text
src/OpenHandsNET.Domain/Identity/{User,Organization,OrgMember,OrgInvitation,
  Role,Permission,RolePermissions,ApiKey,DeviceCode,AuditLog,Plan,Subscription,
  UsageRecord,FeatureFlag}.cs
src/OpenHandsNET.Application/Identity/{IAuthService,IApiKeyService,IOrgService,
  IInvitationService,IProvisioningService,ISuperAdminService,IQuotaService,
  IEffectiveOrgResolver,ISecretStore,IBillingProvider,IAuditService,
  IAnalyticsService}.cs
src/OpenHandsNET.Infrastructure/Identity/{OidcAuthService,ApiKeyService,
  OrgService,InvitationService,ProvisioningService,DeviceFlowService,
  QuotaService,AuditService,SecretStore,ChunkedCookieManager}.cs
src/OpenHandsNET.Infrastructure/Identity/Provisioning/IBrokerUserAdmin.cs  # Keycloak impl
src/OpenHandsNET.Api/Auth/{EffectiveOrgMiddleware,CurrentOrgAccessor,
  DeploymentModeMiddleware}.cs
src/OpenHandsNET.Api/Policies/{PermissionRequirement,PermissionHandler}.cs # RequirePermission
src/OpenHandsNET.Api/Endpoints/{Auth,Keys,Orgs,Invitations,Provisioning,
  Admin,Quota,Billing,FeatureFlags,OAuth}Endpoints.cs
tests/OpenHandsNET.UnitTests/Identity/{RolePermissionsMatrixTests,
  EffectiveOrgResolverTests,ApiKeyBindingTests,ChunkedCookieTests}.cs
tests/OpenHandsNET.IntegrationTests/Identity/{OidcFlowTests,OrgIsolationTests,
  SuperAdminInvariantTests,DeviceFlowTests,ProvisioningRollbackTests,AuditTests}.cs
```

## 9. Referências e links

### Internas (repo)
- `migration-plan/research/api-inventory.md` §7 — rotas enterprise.
- `.specs/06_cli_spec.md` (device flow consumer) · `.specs/10_persistence_efcore_spec.md`
  (`api_keys` ganha `bound_org_id`; `audit_log` entra no schema) ·
  `.specs/13_devops_observability_spec.md` (Keycloak no compose enterprise).

### Upstream — conceitos (Polyform — **não portar código**)
- Authorization: https://github.com/OpenHands/enterprise/blob/693589e/server/auth/authorization.py
- Cookie chunking: https://github.com/OpenHands/enterprise/blob/693589e/server/auth/cookie_chunking.py
- Auth routes (Keycloak callbacks, `/api/authenticate`, TOS): https://github.com/OpenHands/enterprise/blob/693589e/server/routes/auth.py
- API keys (binding/BYOR/current): https://github.com/OpenHands/enterprise/blob/693589e/server/routes/api_keys.py
- Orgs: https://github.com/OpenHands/enterprise/blob/693589e/server/routes/orgs.py
- Invitations: https://github.com/OpenHands/enterprise/blob/693589e/server/routes/org_invitations.py
- Provisioning + rollback: https://github.com/OpenHands/enterprise/blob/693589e/server/routes/user_provisioning.py
- Super admins: https://github.com/OpenHands/enterprise/blob/693589e/server/routes/super_admins.py
- Device flow: https://github.com/OpenHands/enterprise/blob/693589e/server/routes/oauth_device.py
- Analytics (paridade = logging+PostHog, sem audit table): https://github.com/OpenHands/enterprise/blob/693589e/server/routes/analytics_events.py
- `DEPLOYMENT_MODE` vs `app_mode`: https://github.com/OpenHands/enterprise/blob/693589e/server/constants.py + `AGENTS.md`
- Cloud Keycloak: https://github.com/OpenHands/OpenHands-Cloud/blob/d242d12/docs/ARCHITECTURE.md

### Documentação oficial
- Enterprise (SSO/RBAC/BYOK): https://docs.openhands.dev/enterprise
- Cloud API auth (`Bearer <API_KEY>`): https://docs.openhands.dev/openhands/usage/cloud/cloud-api

### Bibliotecas / padrões
- OIDC: https://learn.microsoft.com/aspnet/core/security/authentication/oidc
- Policy-based authz (`IAuthorizationHandler`): https://learn.microsoft.com/aspnet/core/security/authorization/policies
- `ITicketStore`/chunked cookie: https://learn.microsoft.com/aspnet/core/security/authentication/cookie
- JWT bearer: https://learn.microsoft.com/aspnet/core/security/authentication/
- Device flow RFC 8628: https://www.rfc-editor.org/rfc/rfc8628
- Data Protection: https://learn.microsoft.com/aspnet/core/security/data-protection/introduction
- EF query filters: https://learn.microsoft.com/ef/core/querying/filters
- Keycloak Admin REST (provision/rollback): https://www.keycloak.org/docs-api/latest/rest-api/
- PostHog .NET (opcional): https://posthog.com/docs/libraries/dotnet
- Rate limiting: https://learn.microsoft.com/aspnet/core/performance/rate-limit

## 10. Guardrails de execução (Devin)

- **Zero código Python portado** — o enum e as regras são reescritos do zero em C#
  (mesmos *nomes* de permission/endpoint por compat de contrato, não por cópia).
- `sk-*`: SHA-256 no banco; plaintext exibido **uma vez** no create.
- Cookie `openhandsnet_auth`: assinado+cifrado (DP), `HttpOnly`+`Secure`+`SameSite=Lax`,
  chunked quando >4KB; nunca em logs.
- Effective org é **server-side por request** — clientes não confiam no claim
  congelado; `X-Org-Id` valida membership antes de aceitar.
- Último super admin irremovível — invariante testado (409).
- `deployment_mode` ≠ `app_mode`; multi-tenancy off em OSS (first-user=owner+superadmin).
- Emails/tokens em logs → minimização PII (paridade: loga ids, não emails).

## 11. Definition of Done

- [ ] Todos os critérios §6 verificados por testes (OIDC fixture, binding, 403,
  409, device flow, rollback, audit).
- [ ] Matriz `ROLE_PERMISSIONS`/`SUPER_ROLE_PERMISSIONS` coberta por unit tests.
- [ ] `dotnet test` verde; sem segredo serializado; SPEC re-aprovada → `Done`
  quando implementada; issue #9 atualizada.
