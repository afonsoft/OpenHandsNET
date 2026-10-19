# SPEC — 55 · Ticketing Triggers: Linear + Jira → Conversas

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `ticketing-triggers` |
| Type | `Feature / Integration` |
| Stack | `.NET 10 · webhooks · EF Core · SignalR` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed` |
| Ticket | `TBD` |
| Depends on | `47` (automation triggers), `52` (PR de volta) |
| Reference | competitive-analysis §6.1 (G4); docs.devin.ai
  `/integrations/linear`, `/integrations/jira`; docs.openhands.dev
  `cloud/project-management/jira-integration` |
| Touches | `src/OpenHandsNET.{Application,Infrastructure,Api,Client}`, `tests/` |

## 1. User Story

**As a** time que usa Linear ou Jira
**I want** que tickets atribuídos/marcados disparem sessões do
OpenHandsNET com o contexto do ticket e recebam status/link de volta
**So that** o caso de uso nº 1 do Devin ("assign ticket → PR") e a
integração Jira do OpenHands Cloud funcionam no self-host. Hoje só
webhooks genéricos de automation (SPEC-47) — sem parse semântico de
ticket nem write-back.

## 2. Scope (RF)

### RF-001 — `ITicketProvider` + conexões

- `Application/Tickets/ITicketProvider.cs`: `ParseWebhookAsync`,
  `GetIssueAsync`, `TransitionAsync`, `AddCommentAsync`,
  `AddLabelAsync`.
- Entidade `ticket_connections`: `Id, OrgId, Kind (linear|jira),
  ApiUrl, TokenRef, WebhookSecretRef, TriggerConfigJson`.
- `TriggerConfigJson`: `{ mode: label|assignee|state, value,
  prompt_template?, selected_repository? }` — ex.: disparar quando
  label `openhandsnet` aplicada ou assignee = conta-serviço.

### RF-002 — Webhook receivers

- `POST /api/v1/webhooks/tickets/linear` — Linear webhook:
  `Linear-Signature` HMAC; tipos `Issue.update` (label/assignee/state
  batem trigger), `Comment.create` com `@openhandsnet`.
- `POST /api/v1/webhooks/tickets/jira` — Jira Cloud webhook:
  segredo via `?secret=` registrado ou header custom; eventos
  `jira:issue_updated`, `comment_created` com `@openhandsnet`.
- Idempotência por delivery/event id (reusa `webhook_deliveries`).

### RF-003 — Start-task enriquecido

- Prompt montado do template do connection + contexto:
  identificador, título, descrição (markdown→text), labels, comentário
  disparador, url do ticket, repo mapeado
  (`ticket_project_configs: ProjectKey → selected_repository/branch`).
- `TicketBinding` persistido na conversa (`ticket_kind, ticket_id,
  ticket_url`) — visível na UI e usado no write-back.

### RF-004 — Write-back

- Run concluída → comentário no ticket: resumo + link da conversa +
  link do PR (SPEC-52 se houver) + transição para estado configurado
  (`done_state` no config, ex.: "In Review").
- Falha → comentário de erro + transição opcional p/ estado de
  blocked.
- `POST /api/v1/tickets/{kind}/{issueKey}/comment` manual também
  (admin/debug).

### RF-005 — Admin UI + CLI

- Settings `Tickets`: add connection (kind, url, token), triggers por
  projeto, mapeamento projeto→repo.
- CLI: `openhandsnet tickets connect|test|run <issue>` — `run`
  dispara start-task manual com o mesmo enriquecimento (útil sem
  webhook público).

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| POST | `/api/v1/webhooks/tickets/linear` | público, HMAC |
| POST | `/api/v1/webhooks/tickets/jira` | público, secret |
| GET/POST/DELETE | `/api/v1/orgs/{org}/ticket-connections` | admin |
| POST | `/api/v1/tickets/connections/{id}/test` | diagnóstico |
| POST | `/api/v1/tickets/connections/{id}/run` | `{issueKey}` → start-task |

Erros: `400` · `401` · `404` issue/connection · `422` provider.

## 4. Data model

`ticket_connections`, `ticket_project_configs` (conn, project,
repo, branch), campo `ticket_binding` na conversa (`TicketKind,
TicketId, TicketUrl`). Migrations SQLite+Postgres.

## 5. NFR / Security

- Tokens via `SecretNamesJson`; `test` não vaza.
- Webhooks verificados; payload ≤256 KB; rate limit por connection.
- Comentários recebidos tratados como contexto não-confiável
  (`IInputGuard` SPEC-59 quando presente).
- Audit: `ticket.run_started`, `ticket.writeback`.

## 6. Task Plan

1. `ITicketProvider` + entidades + Linear (parse/update/comment).
2. Jira provider (webhook + REST v3).
3. Trigger evaluation → start-task + binding.
4. Write-back (comment + transition) no fim de run.
5. Admin UI + CLI `run` manual + docs.

## 7. Deliverables

Ticket (Linear/Jira) com label/assignee/menção dispara sessão com
contexto rico; resultado volta como comentário + transição; run manual
via CLI.

## 8. Referências

- `docs/specs/47_automations_spec.md`, `docs/specs/52_github_app_scm_spec.md`.
- Linear webhooks + GraphQL API; Jira Cloud REST v3 + webhooks.

## 9. Guardrails

- Parse estrito: campos conhecidos apenas; sem eval de payload.
- Sem polling — webhooks only; `run` manual cobre labs sem URL pública.
- Write-back best-effort: falha de comentário não falha a run.

## 10. DoD

- Webhook Linear válido dispara start-task c/ contexto correto.
- Jira issue_updated + trigger bate → idem.
- Fim de run posta comentário com link conversa (+ PR quando houver).
- Replay delivery → 1× processado. Testes unit do parser + integração
  mock. Suite verde; OpenAPI golden atualizado.


## 11. Entrega — evidência (2026-10-18)

- `Application/Tickets/TicketContracts.cs` — `ITicketProvider`, contratos
  de webhook (`TicketWebhookEvent`), `TicketTriggerConfig`
  (label|assignee|state + done_state + prompt_template +
  selected_repository), `TicketConnection`.
- `Infrastructure/Tickets/` — `TicketRecords` (ticket_connections +
  ticket_project_configs + filtros de org), `TicketProviders`
  (`RestTicketProvider` + `LinearTicketProvider` GraphQL +
  `JiraTicketProvider` REST v3/ADF), `TicketConnectionService`,
  `TicketTriggerService` (signature check por connection, routing por
  workspace hint, triggers label/assignee/state + @openhandsnet mention,
  dedup `webhook_deliveries`, prompt render + start-task + binding),
  `TicketReporter` (comentário + transição done_state + audit).
- `Api/Endpoints/TicketEndpoints.cs` — `/webhooks/tickets/linear|jira`,
  admin `/orgs/{org}/ticket-connections` (+/projects), `/tickets/
  connections/{id}/test|run`, `/tickets/{kind}/{issue_key}/comment`.
- `AgentRunner` chama `ITicketReporter.ReportAsync` no fim do run.
- Admin `/admin/tickets` + CLI `openhandsnet tickets status|test|run`.
- Testes: `Spec55TicketsTests` 9/9 verdes (parse Linear/Jira, label
  trigger + binding, non-match Ignored, @mention, dedup, bad signature,
  run manual, reporter comment+transition).
- Migrations `*_spec55_tickets` (SQLite + Postgres); golden OpenAPI
  regenerado.

## 11. Delivered

- `Application/Tickets/` + `Infrastructure/Tickets/` — triggers
  Linear/Jira → start-task, write-back de status/comentário.
- `Api/Endpoints/TicketEndpoints.cs` — config + webhook + bindings.
- CLI `ticket` commands; docs `docs/features/tickets.md`.
- EF migrations `spec55_tickets` (SQLite + Postgres).
- PR: https://github.com/afonsoft/OpenHandsNET/pull/129
