# SPEC — 61 · Session Insights (post-run analysis)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `session-insights` |
| Type | `Feature / AI + API + Frontend` |
| Stack | `.NET 10 · OmniRoute role `analysis` · EF Core · Blazor` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
| Ticket | `TBD` |
| Depends on | `02` (events), `23` (OmniRoute), `39` (eval — compartilha
  conceito de scoring) |
| Reference | competitive-analysis §6.3 (G9); docs.devin.ai
  `/product-guides/session-insights` + `/work-with-devin/advanced-capabilities` (analyze sessions) |
| Touches | `src/OpenHandsNET.{Application,Infrastructure,Api,Client}`, `cli/`, `tests/` |

## 1. User Story

**As a** usuário/admin
**I want** uma análise automática pós-sessão: outcome, dead-ends,
issues, custo por fase e um prompt melhorado sugerido
**So that** cada sessão gera aprendizado — paridade com Devin Session
Insights. Hoje temos eventos `agent_diagnostic` e o eval harness, mas
nenhum produto de insights na UI/CLI.

## 2. Scope (RF)

### RF-001 — Insight job

- `SessionInsightService` (background): ao `run` terminar
  (succeeded|failed|stopped) enfileira análise — janela de eventos
  (ledger SPEC-22 + amostragem de tool calls/erros/tokens) → LLM role
  `analysis` (OmniRoute — modelo barato default) → `session_insights`:
  `{ ConversationId, Outcome (success|partial|failed|unclear),
  IssuesJson[{kind,severity,summary,event_ids}], DeadEndsJson[],
  PhaseCostsJson[{phase,tokens,seconds}], SuggestedPrompt?,
  LearningsJson[], Model, CreatedAt }`.
- Retentativa com backoff; `Insights:Enabled` por org (default on);
  opt-out por conversa.
- Também on-demand: `POST /api/v1/app-conversations/{id}/insights`
  (re-gera) + batch `POST /api/v1/insights/analyze` `{ids[]}` —
  análise multi-sessão (padrões entre N sessões, estilo "analyze
  these 4 sessions" do Devin) → `insight_reports`.

### RF-002 — API + tipos

- `GET /api/v1/app-conversations/{id}/insights` → último insight.
- `GET /api/v1/insights?outcome=&severity=&from=` (lista org).
- Evento `session_insight` no stream da conversa quando pronto.
- CLI: `openhandsnet insights <id>` + `--batch`.

### RF-003 — UI

- Aba `Insights` na página da conversa: outcome badge, timeline de
  fases com custo, lista de issues com deep-link para o evento,
  card "suggested prompt" com botão "use" → composer pre-preenchido.
- Widget na home: insights recentes + distribuição de outcomes.

### RF-004 — Métricas agregadas

- `GET /api/v1/insights/stats` → outcome distribution, top issues,
  avg tokens/run, dead-end rate por período — alimenta dashboard
  admin de usage (SPEC-20 UI) sem PSP.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| GET | `/api/v1/app-conversations/{id}/insights` | |
| POST | `/api/v1/app-conversations/{id}/insights` | re-gera (rate-limited) |
| GET | `/api/v1/insights` | filtro/paginação |
| POST | `/api/v1/insights/analyze` | batch multi-sessão |
| GET | `/api/v1/insights/stats` | agregados |

Erros: `404` conversa · `409` run ainda ativa · `429` re-gen.

## 4. Data model

`session_insights` (conversation_id, outcome, jsons, model),
`insight_reports` (multi-sessão). Migration.

## 5. NFR / Security

- Amostragem passa por `SecretRedactor` antes do LLM externo.
- Role `analysis` roteável (BYOR/LiteLLM ok); timeout 60 s; falha do
  analyzer não afeta a run (fire-and-forget).
- Insights são dados da org — `perm:insights:read` (admin) /
  dono da conversa lê os próprios.
- Custo de análise registrado em usage (bucket `insights`).

## 6. Task Plan

1. Entidade + `SessionInsightService` (janela, prompt, parse JSON
   robusto → fallback `outcome=unclear`).
2. API + evento de stream + CLI.
3. Aba Insights no client + widget home.
4. Batch analyze + stats + docs.

## 7. Deliverables

Insight automático pós-run com outcome/issues/custos/prompt sugerido;
análise batch; stats agregados; UI dedicada.

## 8. Referências

- `migration-plan/research/competitive-analysis-devin-openhands-cloud.md`.
- `.specs/22_context_engine_compaction_spec.md` (ledger),
  `.specs/23_omniroute_model_catalog_routing_spec.md` (roles),
  `.specs/39_eval_harness_spec.md` (conceito de scoring compartilhado).

## 9. Guardrails

- Insight é descritivo, não prescritivo — nunca altera estado da
  conversa nem re-executa ferramentas.
- JSON do LLM validado por schema; resposta malformada → retry 1×
  depois `unclear`.
- Não enviar código fonte além do necessário — amostra com limite de
  tokens fixo.

## 10. DoD

- Run concluída gera insight com outcome correto em cenário feliz e
  em falha simulada.
- Endpoint retorna insight; `suggested_prompt` presente quando a run
  teve dead-ends.
- Stats agregam corretamente; suite verde; golden OpenAPI atualizado.
