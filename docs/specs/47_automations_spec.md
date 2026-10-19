# SPEC — 47 · Automations (Schedules + Hook Triggers + Routine Tool)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `automations` |
| Type | `Feature / Infrastructure + API + Client` |
| Stack | `.NET 10 · EF Core (2 ctx) · BackgroundService · Blazor WASM` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed (PR #113, fc4acb4)` |
| Ticket | [#105](https://github.com/afonsoft/OpenHandsNET/issues/105) |
| Depends on | `04` (start-task), `08` (auth/RBAC), `10` (persistence), `20` (plan quotas), `25` (tool catalog) |
| Reference | `afonsoft/open-webui` (MIT — port permitido): `AutomationService`/`AutomationScheduler`, `AutomationEndpoints` + `AutomationHookEndpoints`, `Pages/Automations.razor`, `RoutineBuiltinTools` |
| Touches | `src/OpenHandsNET.Infrastructure` (entities+scheduler+tool), `src/OpenHandsNET.Api/Endpoints`, `src/OpenHandsNET.Client/Pages`, migrations ×2 |

## 1. User Story

**As a** usuário do OpenHandsNET
**I want** automações que disparam conversas do agente por agenda
(interval/daily/weekly/once) ou por webhook externo, com histórico de
runs e "rodar agora"
**So that** trabalho recorrente (review diária, digest semanal, tarefa
one-shot agendada, trigger n8n/CI) roda sem eu abrir a UI — hoje não
existe nenhum agendamento de usuário: só hosted services internos e
webhooks inbound de eventos.

**Contexto:** fork entrega o modelo completo: `Automation`+`AutomationRun`
+`AutomationHook` (migration), `ComputeNextRun` (interval/daily/weekly/
once UTC), `BackgroundService` tick, `/api/v1/automations` CRUD +
`/{id}/run-now` + `/runs`, `POST /hooks/{token}` anônimo rate-limited,
páginas lista/detalhe, tool `routine` p/ o agente. Adaptação natural:
uma execução de automation = **`POST /app-conversations` existente**
(start-task `WORKING→READY` com `initial_message=prompt`,
`selected_repository` opcional) — reusa plan quotas, sandbox lifecycle,
UI de conversa. Decisões travadas: calendário visual defer (lista
"próximas execuções" apenas); webhook trigger separado dos
`WebhookEndpoints` inbound (estes são eventos GitHub-style, aquele é
trigger genérico de automation).

## 2. Scope (RF)

### RF-001 — Entidades + migration (dois contextos)

- `AutomationRecord`: `{Id, OrgId, CreatedByUserId, Name, Prompt,
  LlmModel?, SelectedRepository?, ScheduleKind(interval|daily|weekly|
  once), ScheduleTimeUtc(HH:mm), IntervalMinutes, Weekday?, Enabled,
  NextRunAt, LastRunAt, CreatedAt, UpdatedAt}`.
- `AutomationRunRecord`: `{Id, AutomationId, StartedAt, FinishedAt?,
  Status(running|succeeded|failed|skipped), ConversationId?, Error?}`.
- `AutomationHookRecord`: `{Id, AutomationId, TokenHash(sha256),
  CreatedAt, LastUsedAt}`.
- EF migration **SQLite + Postgres** (padrão do repo — dois contexts).

### RF-002 — `AutomationScheduler` (BackgroundService)

- Tick 30s: `Enabled && NextRunAt <= now` → dispara: cria start-task
  (`POST /app-conversations` interno via `IAppConversationService` —
  `initial_message=Prompt`, `selected_repository`, model default do
  usuário), grava `AutomationRun(status=running, conversation_id)`,
  `NextRunAt = ComputeNextRun`, `LastRunAt`.
- **Catch-up**: runs perdidas (serviço down) não acumulam — dispara uma
  vez e recomputa.
- **Concurrency guard**: automation com `AutomationRun` ainda `running`
  → skip + log (`skipped` run row) — nunca 2 runs simultâneas da mesma
  automation.
- Reconcile: tick marca `succeeded|failed` olhando `ExecutionStatus`
  da conversa linkada.
- `ComputeNextRun` (pure, testável): interval→now+min; daily→próximo
  HH:mm UTC; weekly→próximo weekday HH:mm; once→uma vez depois Enabled=false.

### RF-003 — REST API

- `/api/v1/automations` CRUD (owner/org scope), validação: prompt
  non-empty ≤8KB, `interval_minutes ≥ 5`, `once` sem recorrência.
- `POST /{id}/run-now` → dispara imediato (mesmo guard de concorrência).
- `GET /{id}/runs?take=50` + `GET /runs?from&to` (histórico global).
- Hooks: `POST /{id}/hooks` → `{token}` **exibido uma vez** (hash no
  banco); `DELETE /hooks/{hookId}`; `POST /api/v1/hooks/{token}`
  **anônimo** → run-now da automation linkada — rate-limit token bucket
  10/min por token + 60/min por IP; token inválido → 404 (não 401, para
  não vazar existência).

### RF-004 — UI

- `/automations` (nav sidebar/admin-area): tabela {name, schedule humano,
  next_run, last_status, enabled toggle, run-now} + lista "próximas
  execuções" (top-10 agregada).
- `/automations/{id}`: editor (nome, prompt textarea, model selector,
  repo, schedule picker) + histórico de runs inline (link p/ conversa).
- Sem grid de calendário no MVP.

### RF-005 — Tool `routine` (catálogo builtin)

- Agente lista/cria/habilita/desabilita/deleta routines **do owner da
  conversa** (scope da conversa atual; SPEC-25 catalog + SPEC-29
  permission: `routine` default `ask` em `build` mode? — default
  `allow` para list, `ask` para mutações; tuning na implementação).
- Mesma `AutomationService` — tool é thin adapter.

### RF-006 — Enforcement + audit

- Conversas de automation passam pelo pipeline normal → quotas de plano
  (SPEC-20: conversas/dia, runs concorrentes, tokens) — `run` negada por
  quota → `AutomationRun(failed, error=quota)`.
- Audit: `automation_fired`, `automation_run_created`, hook trigger
  (IP+token-id) no audit log (SPEC-08).
- `agent_diagnostic` events na conversa criada marcando origem
  `automation:{id}`.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| GET/POST | `/api/v1/automations` | list/create |
| GET/PATCH/DELETE | `/api/v1/automations/{id}` | detail/update/enable toggle via PATCH `{enabled}` |
| POST | `/api/v1/automations/{id}/run-now` | → `{run_id, conversation_id}`; 409 running |
| GET | `/api/v1/automations/{id}/runs`, `/api/v1/automations/runs?from&to` | histórico |
| POST/DELETE | `/api/v1/automations/{id}/hooks`, `/api/v1/automations/hooks/{hookId}` | token 1× |
| POST | `/api/v1/hooks/{token}` | anônimo, rate-limited → 202 |

## 4. Data model

3 tabelas novas (`automations`, `automation_runs`,
`automation_hooks`) + índices `(Enabled,NextRunAt)`, `(AutomationId,
StartedAt)` — migration **ambos os contextos**.

## 5. NFR / Security

- Hook token: 256-bit random, sha256 no banco, mostrado 1×; rate-limit
  duplo (token+IP); 404 em inválido.
- Automation dispara com as credenciais do owner — prompt injection no
  prompt da automation é do próprio usuário (mesmo trust de start-task).
- `Prompt` nunca contém secrets — UI avisa; secrets vêm de
  `SecretNamesJson` da conversa se necessário (não no MVP).
- Tick resiliente: falha numa automation não bloqueia as demais (try/
  catch por item + log).

## 6. Task Plan

1. Entidades + config + migrations (2 ctx) + `ComputeNextRun` puro +
   testes da tabela verdade.
2. `AutomationService` + scheduler tick + guards + reconcile; testes.
3. Endpoints CRUD + run-now + runs + validação; testes de contrato.
4. Hooks (create/rotate/delete + trigger anônimo + rate-limit); testes.
5. Páginas `/automations` + detalhe + sidebar nav; bUnit.
6. Tool `routine` + permission defaults + `agent_diagnostic` origem.
7. Quota wiring + audit + docs + `dotnet test` verde.

## 7. Deliverables

`Infrastructure/Automations/{AutomationService,AutomationScheduler,
AutomationSchedule}.cs`, records + migrations ×2, `AutomationEndpoints`
+ `HookTriggerEndpoints`, `Pages/Automations.razor` +
`AutomationDetail.razor`, tool `routine`, testes, `docs/automations.md`.

## 8. Referências

- `afonsoft/open-webui` (MIT): `Infrastructure/Services/
  AutomationService.cs` (`ComputeNextRun` + scheduler),
  `Api/Endpoints/{AutomationEndpoints,AutomationHookEndpoints}.cs`,
  `Pages/{Automations,AutomationDetail}.razor`,
  `Infrastructure/ChatTools/Tools/RoutineBuiltinTools.cs`,
  `docs/specs/SPEC-20261001-automations-calendar.md` +
  `SPEC-20261010-agent-routines.md`.
- `docs/specs/04` (start-task), `docs/specs/20` (quota), `docs/specs/25` (tool
  catalog), `docs/specs/29` (permissions), `WebhookEndpoints.cs` (inbound
  existente — distinto).

## 9. Guardrails

- Execução = start-task pipeline normal — nenhum caminho alternativo de
  run (quotas/permissions/sandbox valem igual).
- Token de hook nunca persistido em claro (sha256) nem reexibido.
- Concurrency guard: sem runs paralelas da mesma automation.
- `.github/workflows/` intocado; `main` só via PR.

## 10. DoD

- Automation `interval 5min` → conversa criada e visível na UI + run row
  `succeeded` reconciliado; conversa mostra origem automation.
- `run-now` + `409` quando running; once dispara 1× e desabilita.
- Hook: token inválido→404; >10/min→429; válido→202 e run criada.
- Tool `routine` cria routine via chat (visível na página).
- `dotnet test` verde (schedule math, guards, endpoints, bUnit).
