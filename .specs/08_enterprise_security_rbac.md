# SPEC — 08 · Enterprise: Security, Auth, Orgs, Quota & Audit

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `enterprise-security-rbac` |
| Type | `Feature / API / Infra` |
| Stack | `.NET 10 · ASP.NET Core Auth · OIDC · EF Core` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
| Ticket | [#9](https://github.com/afonsoft/OpenHandsNET/issues/9) (Epic [#1](https://github.com/afonsoft/OpenHandsNET/issues/1)) |
| Depends on | `04_rest_and_signalr_api_spec` |
| Upstream | `enterprise/server/routes/` (Polyform — **conceitos apenas**) |

## 1. User Story

**As a** organização que self-hosta o OpenHandsNET
**I want** SSO/OIDC, API keys, organizações com papéis, cotas e trilha de auditoria
**So that** múltiplas equipes usem a plataforma com isolamento e governança —
o diferencial "enterprise" do OpenHands, porém em código 100% open-source.

**Problem context:** o upstream enterprise (Polyform) cobre `auth`, `oauth_v2`,
`oauth_device`, `idp`, `api_keys`, `billing` (Stripe), `orgs`, `org_invitations`,
`org_profiles`, `org_secrets`, `super_admins`, `quota`, `user_provisioning`,
`analytics_events`, `feature_flags`, `instance_admin`, `readiness` — mais
`DEPLOYMENT_MODE ∈ {cloud, self_hosted}`. Reimplementamos os conceitos com
nossa própria arquitetura. Cloud deploy model: `OpenHands-Cloud` usa
Keycloak+K8s+Runtime API — nosso equivalente documentado como opção OIDC+compose/Helm.

## 2. Scope

**In scope:**
- **Auth OSS:** primeiro usuário = admin (padrão open-webui-net); JWT bearer + API keys `sk-*` (hash SHA-256, prefixo visível, escopo/expiração).
- **OIDC plugável:** `Auth__Provider=oidc` + Authority/ClientId → code flow; SAML fora de escopo (OIDC cobre Entra/Okta/Keycloak).
- **Device flow** (RFC 8628) para o CLI (SPEC 06).
- **Orgs (multi-tenant tier):** `Organization`, `OrgMember{role: owner|admin|member}`, convites por email, troca de org context (`X-Org-Id`/claim).
- **RBAC:** policies `conversation:read/write`, `sandbox:admin`, `org:admin`, `admin:*`; checagem por endpoint.
- **Quota:** conversas diárias/concorrentes por org (paridade `_reserve_daily_conversation_quota` upstream) → `429` com `Retry-After`; pausa de conversas antigas.
- **Billing (contratos):** entidades `Plan`, `Subscription`, `UsageRecord` (tokens/eventos por período); provider `[A DEFINIR]` — Stripe atrás de `IBillingProvider` (desabilitado por default).
- **Audit:** todo `ActionEvent` executado, login, API-key uso, admin action → `audit_events` append-only (export JSON/CSV admin).
- **Feature flags:** `FeatureManagement` toggles (`enterprise`, `billing`, `mcp`, `acp`).
- **`deployment_mode`:** `oss|self_hosted|cloud` exposto em `/api/v1/config` — distinto de `app_mode` (licença), lição do upstream.

**Out of scope:** SAML 2.0 nativo, SCIM provisioning, SSO enforcement policies,
integrações Jira/Azure DevOps/Slack/Bitbucket-DC (fase 3), Stripe implementado,
residência de dados por região.

## 3. Technical Context

**Upstream:** `server/routes/*.py` (lista completa em
`migration-plan/research/api-inventory.md` §7); `server/constants.py`
(`DEPLOYMENT_MODE`); Keycloak no Cloud (`OpenHands-Cloud/docs/`).
**Regra aprendida do upstream:** `DEPLOYMENT_MODE` (cloud vs self-hosted) ≠
`app_mode` (oss vs saas) — modelar dois conceitos separados.
**Where:** `Infrastructure/Identity/` (JWT, API keys, OIDC, orgs), `Api/Endpoints/AuthEndpoints.cs`,
`Domain/Identity/` (User, Org, ApiKey, AuditEvent), `Application/Billing/` (contratos).

## 4. Requirements

### RF-001: Autenticação
- JWT (access 1h + refresh 30d) com claims `sub`, `org_id`, `roles[]`.
- API keys `sk-{rand32}`: `POST /api/v1/api-keys` → mostra 1×; list/delete;
  último uso registrado; escopos `read|write|admin`.
- OIDC: `Auth__Provider=oidc` habilita code flow + mapeia groups→roles
  (`groups_claim` configurável); logout front-channel opcional.
- Device flow: `POST /oauth/device/code` `{device_code,user_code,verification_uri,interval}`;
  `POST /oauth/token` (poll `authorization_pending|access_token`).

### RF-002: Orgs & RBAC
- `POST /api/v1/orgs` (cria + owner), `GET /orgs`, `GET|PATCH /orgs/{id}`,
  `POST /orgs/{id}/invitations` (email, expira 7d), `POST /orgs/invitations/{token}/accept`,
  `GET|PATCH|DELETE /orgs/{id}/members[/{userId}]` (role change/remove).
- Resource scoping: conversas/sandboxes/secrets carregam `OrgId` (nullable em OSS);
  EF global query filter `OrgId == CurrentOrg` quando multi-tenant ligado.
- Policies mapeadas: admin endpoints exigem `admin:*` ou role `owner/admin`.

### RF-003: Quota
- `ConversationQuota {daily_max, concurrent_max}` por org/plan; reserva atômica
  no `POST /app-conversations`, libera no delete/finish; excesso → `429` +
  `quota_exceeded` detail; pausa automática das conversas mais antigas (paridade).

### RF-004: Audit & Admin
- `audit_events(user, org, action, target, ip, ua, payload_json, ts)`; endpoint
  admin `GET /api/v1/admin/audit?user&action&from&to&page` + export.
- `GET /api/v1/admin/users`, `POST /api/v1/admin/users/{id}/disable`,
  `GET /api/v1/readiness` + `GET /api/v1/admin/stats` (conversas/tokens/dia).

### RF-005: Secrets
- Store cifrado (AES-256, chave via `OPENHANDSNET_SECRET_KEY`/DPAPI/KMS);
  endpoints `GET|PUT|DELETE /api/v1/secrets/{name}` + org-scoped
  `/api/v1/orgs/{id}/secrets`; serialização sempre mascarada (`"********"`).

**Invariants:** nunca retornar segredo/claro; hash de API key (não reversível);
queries com filtro de org obrigatório no modo multi-tenant; `deployment_mode`
em `X-Deployment-Mode` header + `/config`.

## 5. API Contract

`POST /api/v1/auth/login|refresh|logout` · `GET|POST|DELETE /api/v1/api-keys[/{id}]` ·
`GET /api/v1/auth/oidc/login|callback` · `POST /oauth/device/code|token` ·
`GET|POST /api/v1/orgs` · `GET|PATCH /api/v1/orgs/{id}` ·
`POST /api/v1/orgs/{id}/invitations` · `POST /api/v1/orgs/invitations/{token}/accept` ·
`GET|PATCH|DELETE /api/v1/orgs/{id}/members[/{userId}]` · `GET /api/v1/orgs/{id}/quota` ·
`GET|PUT|DELETE /api/v1/orgs/{id}/secrets[/{name}]` ·
`GET /api/v1/admin/{users|audit|stats}` · `GET /api/v1/readiness` ·
`GET /api/v1/billing/{plan,subscription,usage}` (contratos; provider `[A DEFINIR]`).

## 6. Critérios de Aceite

- [ ] Primeiro usuário vira admin; `sk-*` autentica `POST /app-conversations` e uso é auditado.
- [ ] OIDC com Keycloak de teste faz login e mapeia grupo→role.
- [ ] Convite de org adiciona membro com role; membro só vê conversas da própria org (query filter provado por teste).
- [ ] Quota diária estourada → 429 + reserva liberada após finish.
- [ ] `GET /admin/audit` lista login, api-key usage e admin actions com payload.
- [ ] `deployment_mode` correto em `/config` por ambiente; secrets nunca serializados.

## 7. Task Plan

1. Identity base: JWT + primeiro-admin + API keys (verify: auth tests).
2. Orgs + memberships + invitations + query filters (verify: isolation tests).
3. OIDC + device flow (verify: Keycloak fixture + CLI login E2E).
4. Quota service + 429 + auto-pause (verify: concurrency tests).
5. Audit pipeline + admin endpoints + export (verify: audit tests).
6. Secrets store cifrado + flags + `deployment_mode` (verify: serialization tests).

## 8. Deliverables — arquivos a criar/modificar

```text
src/OpenHandsNET.Domain/Identity/{User,Organization,OrgMember,OrgInvitation,
  ApiKey,ApiKeyScope,AuditEvent,Plan,Subscription,UsageRecord}.cs
src/OpenHandsNET.Application/Identity/{IAuthService,IApiKeyService,IOrgService,
  IQuotaService,ISecretStore,IBillingProvider,IAuditService}.cs
src/OpenHandsNET.Infrastructure/Identity/JwtTokenService.cs        # access+refresh
src/OpenHandsNET.Infrastructure/Identity/ApiKeyService.cs          # sk-* SHA-256
src/OpenHandsNET.Infrastructure/Identity/DeviceFlowService.cs      # RFC 8628
src/OpenHandsNET.Infrastructure/Identity/OrgService.cs             # orgs+members+invitations
src/OpenHandsNET.Infrastructure/Identity/QuotaService.cs           # reserva atômica
src/OpenHandsNET.Infrastructure/Identity/AuditService.cs           # append-only + export
src/OpenHandsNET.Infrastructure/Identity/SecretStore.cs            # AES-256/DPAPI
src/OpenHandsNET.Api/Endpoints/{Auth,ApiKeys,Orgs,Admin,Billing}Endpoints.cs
src/OpenHandsNET.Api/Endpoints/OAuthEndpoints.cs                   # device code+token
src/OpenHandsNET.Api/Auth/CurrentOrgAccessor.cs                    # X-Org-Id→claims
src/OpenHandsNET.Api/Middleware/DeploymentModeMiddleware.cs        # X-Deployment-Mode
tests/OpenHandsNET.IntegrationTests/Identity/{AuthFlowTests,OrgIsolationTests,
  QuotaTests,AuditTests}.cs
tests/OpenHandsNET.UnitTests/Identity/{ApiKeyHashTests,SecretStoreTests}.cs
```

## 9. Referências e links

### Internas (repo)
- `migration-plan/research/api-inventory.md` §7 — lista completa de rotas enterprise.
- `migration-plan/research/risk-assessment.md` — licenciamento Polyform.
- `.specs/06_cli_spec.md` — consumidor do device flow.
- `.specs/10_persistence_efcore_spec.md` — tabelas/query filters.

### Upstream — conceitos (Polyform — **não portar código**)
- Rotas SaaS: https://github.com/OpenHands/enterprise/tree/693589e/server/routes/
  (`auth.py`, `oauth_v2.py`, `oauth_device.py`, `api_keys.py`, `billing.py`, `orgs.py`,
  `org_invitations.py`, `org_secrets.py`, `quota.py`, `readiness.py`, `user_provisioning.py`,
  `super_admins.py`, `feature_flags.py`, `instance_admin.py`)
- `DEPLOYMENT_MODE` vs `app_mode` (regra crítica): https://github.com/OpenHands/enterprise/blob/693589e/server/constants.py
  e https://github.com/OpenHands/enterprise/blob/693589e/AGENTS.md
- Cloud deploy (Keycloak+K8s): https://github.com/OpenHands/OpenHands-Cloud/blob/d242d12/docs/ARCHITECTURE.md

### Documentação oficial
- Enterprise (SSO, RBAC, BYOK, integrações DC): https://docs.openhands.dev/enterprise
- Cloud API auth (`Bearer <API_KEY>`): https://docs.openhands.dev/openhands/usage/cloud/cloud-api

### Bibliotecas / padrões
- JWT bearer: https://learn.microsoft.com/aspnet/core/security/authentication/ ·
  `Microsoft.AspNetCore.Authentication.JwtBearer`
- OIDC: https://learn.microsoft.com/aspnet/core/security/authentication/oidc ·
  `Microsoft.AspNetCore.Authentication.OpenIdConnect`
- Device flow RFC 8628: https://www.rfc-editor.org/rfc/rfc8628
- EF global query filters: https://learn.microsoft.com/ef/core/querying/filters
- Data Protection API (secrets at rest): https://learn.microsoft.com/aspnet/core/security/data-protection/introduction
- `Microsoft.FeatureManagement`: https://learn.microsoft.com/azure/azure-app-configuration/feature-management-dotnet-reference
- Keycloak (OIDC de teste): https://www.keycloak.org/documentation
- Rate limiting nativo: https://learn.microsoft.com/aspnet/core/performance/rate-limit

## 10. Guardrails de execução (Devin)

- **Nunca** copiar código do `enterprise/`/`OpenHands-Cloud/` (Polyform) —
  conceitos e nomes de endpoints apenas.
- API keys: armazenar SHA-256; exibir plaintext **uma única vez** no create.
- Secrets: AES-256 (chave `OPENHANDSNET_SECRET_KEY`, DPAPI ou KMS); serialização
  sempre mascarada `"********"`.
- `deployment_mode` ≠ `app_mode` — modelar como dois conceitos (lição upstream).
- Multi-tenant off (OSS): `OrgId` nullable, query filter desabilitado; on: filtro
  global obrigatório + teste de isolamento.
- `429` inclui `Retry-After` + `detail` `quota_exceeded`.
- Logs: `user_id`/`org_id`/`ip` ok; nunca token/email em texto claro além do
  necessário (PII minimizada).

## 11. Definition of Done

- [ ] Auth OSS (JWT+refresh+`sk-*`) + primeiro-admin funcionais.
- [ ] Device flow + OIDC (Keycloak fixture) verde.
- [ ] Orgs/convites/roles + isolamento por query filter provado por teste.
- [ ] Quota 429 + liberação + auto-pause; audit append-only + export.
- [ ] `deployment_mode` correto; secrets nunca serializados em claro.
- [ ] `dotnet test` verde; SPEC `Done`; issue fechada pelo PR.
