# SPEC — 20 · Plan Enforcement & Usage Metering

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `plan-enforcement-billing` |
| Type | `Feature / Enterprise` |
| Stack | `.NET 10 · EF Core · Minimal API` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` (autopilot — gap list) |
| Ticket | _(issue a criar)_ (Epic [#1](https://github.com/afonsoft/OpenHandsNET/issues/1)) |
| Depends on | `08_enterprise_security_rbac` (plans/subscriptions schema), `14_enterprise_operations_spec` (usage aggregation) |
| Touches | `PlanRecord`, `SubscriptionRecord`, `UsageRecord`, `UsageService`, `AppConversationService`, `ConversationStartWorker`, `AgentRunner`, `AdminEndpoints`, `OrgEndpoints` |

## 1. User Story

**As a** operador enterprise
**I want** que planos tenham limites reais (conversas/dia, runs concorrentes,
tokens/mês, sandbox-segundos/mês) e que o uso seja efetivamente medido
**So that** o `/usage` deixa de ser um relatório de tabela vazia e a
instância ganha enforcement de billing — hoje `UsageRecords` só é **lida**
(`UsageService.cs`) e nada escreve nela.

**Problem context:**

- `PlanRecord.LimitsJson`, `SubscriptionRecord`, `UsageRecord` existem no
  schema (SPEC-08), `OrganizationRecord.QuotaDailyMax/QuotaConcurrentMax`
  têm endpoints CRUD (`OrgEndpoints.cs:166-205`) — mas **zero enforcement**:
  nem quota de org nem limit de plano é consultado em create/start.
- `GET /organizations/{id}/usage` agrega uma tabela sem escritores.
- `TokenEvent` já emite tokens por run — a fonte de metering existe, falta o
  sink.

## 2. Scope

**In scope:**

- **RF-001 — `PlanLimits` + parse**: `LimitsJson` →
  `{max_conversations_per_day?, max_concurrent_runs?, max_tokens_per_month?,
  max_sandbox_seconds_per_month?, max_sandbox_profile?}` (`null` = ilimitado
  no campo). Inválido → limites vazios (never-fail open; log warning).
- **RF-002 — `IPlanEnforcementService`**: resolve o plano efetivo da org:
  `Subscription.Status=="active"` mais recente → `Plan`; org sem `PlanId`/
  subscription → plano `free` seedado; org sem org (user pessoal) → plano do
  user-context = `free`. `CheckConversationCreateAsync(orgId)`,
  `CheckRunStartAsync(orgId)` retornam `PlanLimitError{Key,Limit,Used}|null`.
- **RF-003 — Enforcement points**:
  `AppConversationService.CreateAsync` → `max_conversations_per_day`;
  antes do sandbox start (`ConversationStartWorker`) → `max_concurrent_runs`
  (conta `Sandboxes.Status=="Running"` da org);
  `TokenEvent`/run end → `max_tokens_per_month` (soft cap: estoura → próximo
  run é bloqueado no `CheckRunStartAsync`, o run atual termina);
  sandbox seconds agregados do `UsageRecord` → `max_sandbox_seconds_per_month`
  (bloqueio no start). Violation → `403` `{detail:"plan_limit_exceeded",
  limit_key, limit, used}` (create) ou run `Error` + evento (worker).
  `Billing:EnforcementEnabled` (default `false` — OSS self-host sem
  enforcement; `true` liga).
- **RF-004 — Metering write-path**: `IUsageMeter.RecordRunAsync(orgId, tokens,
  costUsd)` chamado no fim do run (runner publish) agrega na `UsageRecord` do
  período `yyyy-MM`; `RecordSandboxSecondsAsync` no sandbox stop/delete.
  `period` string `yyyy-MM`.
- **RF-005 — Admin plans CRUD**: `GET /admin/plans`, `POST /admin/plans`,
  `PATCH /admin/plans/{id}`, `DELETE /admin/plans/{id}` (perm
  `manage_super_admins`); `PUT /admin/organizations/{org}/plan` cria
  subscription ativa (encerra a anterior). Sem gateway de pagamento — billing
  real fica fora (`IBillingProvider` segue stub documentado).
- **RF-006 — Seed `free`**: `DatabaseMigrator`/`DataSeeder` garante plano
  `free` `{max_conversations_per_day:50, max_concurrent_runs:2,
  max_tokens_per_month:null, max_sandbox_seconds_per_month:null}` — default
  permissivo; operador restringe via admin.

**Out of scope:**

- Integração Stripe/cobrança, invoices, cards — `IBillingProvider` documentado
  como extensão.
- Hard-kill de run em andamento por token cap (v1 = bloqueio no próximo start).
- Rate-limit por segundo (conceito diferente).

## 3. Design

```text
create conv ──► PlanEnforcement.CheckConversationCreate(org)
run start   ──► CheckRunStart(org)  [concurrent_runs, tokens, sandbox_secs]
run end     ──► UsageMeter.RecordRun(tokens, cost)     ──► usage_records
sandbox end ──► UsageMeter.RecordSandboxSeconds(secs) ──► usage_records
```

- Org do enforcement: `ConversationRecord.OrgId` (fallback: org default do
  `CreatedByUserId`; sem org → plano `free`).
- Contagem `conversations_per_day`: `Conversations` da org com
  `CreatedAt >= utc_today`; `concurrent_runs`: `Sandboxes` `Running` ligados a
  conversas da org.
- `403` no create mantém contrato existente (ProblemDetails).

## 4. API Contract

| Endpoint | Notas |
|---|---|
| `GET /api/v1/admin/plans` | lista catálogo (perm `manage_super_admins`) |
| `POST /api/v1/admin/plans` | `{name, limits{...}, monthly_price_usd?}` → 201 |
| `PATCH /api/v1/admin/plans/{id}` | atualiza limits/price |
| `DELETE /api/v1/admin/plans/{id}` | 409 se org/subscription ativa referencia |
| `PUT /api/v1/admin/organizations/{org_id}/plan` | `{plan_id}` → subscription ativa nova (ended anterior) |
| `GET /api/v1/organizations/{id}/plan` | plano efetivo + limits + uso do mês (`perm:view_billing`) |

Erro de enforcement (create): `403` `{detail:"plan_limit_exceeded",
limit_key:"max_conversations_per_day", limit:50, used:50}`.

## 5. Critérios de Aceite

- [ ] `EnforcementEnabled=true` + plano com `max_conversations_per_day=1` →
  2ª conversa do dia da org retorna 403 com `limit_key` correto.
- [ ] `max_concurrent_runs=1` → 2º start concorrente falha/bloqueia.
- [ ] Fim de run grava `UsageRecord` (tokens agregados por org/período);
  `/usage` passa a retornar dados não-vazios após um run.
- [ ] Plans CRUD funciona; delete de plano referenciado → 409.
- [ ] `EnforcementEnabled=false` → zero impacto (self-host livre).
- [ ] Migration: nenhuma (schema já existe) — só seed do plano `free`.

## 6. NFR

- Enforcement consulta ≤ 2 queries extras no create/start (índice já cobre).
- `LimitsJson` inválido nunca bloqueia (fail-open + log) — erro de config não
  pode derrubar o produto.
- Idempotência do seed `free` (name-unique check).

## 7. Task Plan

1. `PlanLimits` parse + `IPlanEnforcementService` + `PlanEnforcementService`
   (unit tests: resolução free/subscription/expirada).
2. Enforcement no `CreateAsync` + `ConversationStartWorker` + evento de erro.
3. `IUsageMeter`/`UsageMeter` + hooks run-end/stop-end.
4. Admin plans endpoints + `PUT org/plan` + `GET org/plan`.
5. Seed `free` no migrator.
6. Testes: unit (parse/enforce/meter) + integration WAF (403 paths, CRUD).
7. `.env.exemplo` `Billing__EnforcementEnabled` + docs + index.

## 8. Deliverables

```text
src/OpenHandsNET.Application/Identity/IPlanEnforcementService.cs
src/OpenHandsNET.Application/Identity/IUsageMeter.cs
src/OpenHandsNET.Domain/Identity/PlanLimits.cs
src/OpenHandsNET.Infrastructure/Identity/PlanEnforcementService.cs
src/OpenHandsNET.Infrastructure/Identity/UsageMeter.cs
src/OpenHandsNET.Api/Endpoints/AdminEndpoints.cs     # plans CRUD + org/plan
src/OpenHandsNET.Api/Endpoints/OrgEndpoints.cs       # GET org/plan
src/OpenHandsNET.Infrastructure/Persistence/DataSeeder.cs  # plano free
tests/OpenHandsNET.UnitTests/Identity/PlanEnforcementTests.cs
tests/OpenHandsNET.UnitTests/Identity/UsageMeterTests.cs
tests/OpenHandsNET.IntegrationTests/Identity/PlanEndpointsTests.cs
docs/enterprise/billing.md
```

## 9. Referências

- SPEC-08 (schema plans/subscriptions/usage + RBAC perms), SPEC-14 RF-004
  (UsageService read-side).
- Upstream OpenHands-Cloud quota/credits — conceito apenas (Polyform).

## 10. Guardrails

- Default `EnforcementEnabled=false` — nunca bloquear self-host OSS.
- Nenhuma migration de schema (campos já existem).
- `IBillingProvider` permanece stub — gateway real é outra SPEC.

## 11. DoD

- [ ] Enforcement E2E nos 4 limites; metering popula `/usage`.
- [ ] `dotnet build` + `dotnet test` verdes; CI do PR verde.
- [ ] Índice `.specs/README.md` + issue fechada no merge.
