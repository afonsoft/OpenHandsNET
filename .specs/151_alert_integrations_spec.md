# SPEC — 151 · Alert integrations (Sentry/PagerDuty → sessão + Teams sink)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `alert-integrations` |
| Type | `Feature / Integrations + ChatOps` |
| Stack | `.NET 10 · inbound webhooks (SPEC-52 pattern) · ChatOps sinks` |
| Repository | `afonsoft/sandcastle` |
| Status | `Draft` |
| Ticket | `GAP-ALERT-INTEGRATIONS` (docs/research/2026-11-competitive-gap-analysis.md §P2) |
| Depends on | `52` (webhook HMAC+idempotency), `54` (ChatOps), `66` (automations/triggers) |
| Reference | Devin Sentry/PagerDuty/Teams integrations — conceitos apenas |
| Touches | `src/Sandcastle.{Domain,Application,Infrastructure,Api,Client}`, `tests/` |

## 1. User Story

**As a** engenheiro on-call
**I want** um alerta do Sentry/PagerDuty virar automaticamente uma
sessão do Sandcastle que investiga a causa raiz no repo afetado e abre
PR de fix — e resultados notificados no Microsoft Teams
**So that** paridade com as integrações de alerta do Devin: o "Oncall"
open-source. Hoje o único inbound externo é SCM/webhook e o único
sink de notificação é Slack.

## 2. Scope (RF)

### RF-001 — Inbound alert ingestion (genérico + adaptadores)

- `alert_sources` entity: `{id, org, provider(sentry|pagerduty|generic),
  name, secret_ref, repo_mapping_json, enabled, auto_create_session,
  triage_severity_min}`. Config via `POST/PATCH /api/v1/alerts/sources`.
- `POST /api/v1/webhooks/alerts/{source_id}` — inbound endpoint:
  signature verify per provider (Sentry `sentry-hook-signature` HMAC;
  PagerDuty `X-PagerDuty-Signature` v1 HMAC chain; generic = shared
  secret header), idempotency por `delivery id`/`incident.id`,
  payload→`alert_events` normalizado `{source, provider, kind, title,
  url, severity, service?, culprit?, first_seen, labels_json}`.
- Repo mapping: `service→repository` ou payload `project→repository`;
  sem mapping → evento fica `unrouted` (listável, roteável manual).

### RF-002 — Alert → session pipeline

- `alert_events` com repo mapeado + `auto_create_session` + severity
  ≥ `triage_severity_min` → conversa `agent_type: incident-investigator`
  com prompt contextual (título, stack trace resumido, url, repo) —
  reusa start-task + policies; dedup por `(source, external_id)` e
  `Alerts:MaxSessionsPerHour` por source.
- Outcome tracking: `alert_events.session_id`, `status
  (new|investigating|pr_open|resolved|dismissed)`; PR url link;
  resolved quando o alerta fecha (evento `resolved` inbound → marca).
- Slack/Teams/ChatOps notifica o resumo da investigação (reusa
  `IChatOpsReporter` — ver RF-004).

### RF-003 — Teams ChatOps sink

- `IChatSink` implementação `TeamsChatSink` (Incoming Webhook URL por
  canal — MessageCard/AdaptiveCard JSON; ou Graph API se credencial
  de app existir — v1 = Incoming Webhook, o caminho zero-setup).
- `chat_connections` ganha `provider: slack|teams` + `webhook_url`;
  UI admin ganha picker + test-connection (POST `{text:"test"}`).
- `SlackTextSanitizer` → generalizar `ChatTextSanitizer` (pings do
  Teams: `<at>`, `@channel`, `@team` — neutralizar conforme v1; docs
  dizem `<at>` exige mention payload — default: strip anyway).

### RF-004 — UI + auditoria

- Sidebar "Alerts": sources CRUD (masked secret), events list
  (provider/severity/status/repo), event → session link, manual
  "Create session" e "Dismiss".
- Audit `alert.*` events + stats_json; `SecretRedactor` em todo
  payload persistido/postado.

### RF-005 — Fora de escopo

- Acknowledge/resolve bidirecional no PagerDuty (write-back) ·
  Oncall rotação/scheduling · Datadog/NewRelic/Grafana adaptadores
  (generic cobre) · Teams Graph API (mentions reais) · alert
  grouping/correlation avançada — fase 2.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| POST/GET/PATCH/DELETE | `/api/v1/alerts/sources` | CRUD admin |
| POST | `/api/v1/webhooks/alerts/{source_id}` | inbound, signature per provider |
| GET | `/api/v1/alerts/events` | filtros source/severity/status/repo |
| POST | `/api/v1/alerts/events/{id}/create-session` | manual |
| PATCH | `/api/v1/alerts/events/{id}` | `{status: dismissed}` |
| POST | `/api/v1/chat-connections/{id}/test` | `{provider}` send test |

## 4. Data model

- `alert_sources`, `alert_events` — migrations duplas.
- `chat_connections` + `provider`, `webhook_url` cols.

## 5. NFR / Security

- Signature verify obrigatória por provider (fail-closed — sem
  signature válida, 401; nunca processa payload).
- Secrets por nome (`SecretNamesJson`); payload é untrusted →
  envelope SPEC-91 + SecretRedactor na persistência.
- `perm:alerts.manage` (admin), `alerts.view` (member); inbound
  endpoint só autentica por signature (não por sessão).
- Rate limit inbound por source (`Alerts:MaxInboundPerMin=60`).

## 6. Task Plan

1. `alert_sources`/`alert_events` + migrations.
2. Inbound webhook + signature verifiers (Sentry, PD, generic) +
   normalizer + idempotency.
3. Session pipeline (mapping→dedup→cap→investigator conv).
4. `TeamsChatSink` + sanitizer generalizado + admin UI test.
5. Alerts UI (sources, events, actions) + i18n.
6. Testes: unit (signature verify ×3, normalizer, dedup, sanitizer
   Teams), integração (inbound→event→session fake), golden OpenAPI.

## 7. Deliverables

Sentry/PagerDuty/generic → sessão de investigação com PR, Teams
notificado — "Oncall" open-source sobre a fundação ChatOps/webhook.

## 8. Referências

- `docs/research/2026-11-competitive-gap-analysis.md` §P2.
- Devin integrations docs (conceitos).
- `docs/specs/52_github_app_scm_spec.md` (webhook verify/idempotency),
  `54_chatops_slack_spec.md`, `66_quality_gates_automation_spec.md`.

## 9. Guardrails

- Fail-closed: signature ausente/inválida → 401, payload descartado.
- Nenhum write-back ao provider em v1.
- Auto-session sempre opt-in por source (default OFF).

## 10. DoD

- Fixture Sentry signature válida → event persistido → sessão criada
  (dedup no re-delivery); signature inválida → 401.
- Teams sink posta card de teste; sanitizer neutraliza pings.
- Suite verde; migrations duplas; golden; i18n ×8.
