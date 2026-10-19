# SPEC — 63 · Knowledge Pipeline (suggestions + review + dedup)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `knowledge-pipeline` |
| Type | `Feature / AI + API + Frontend` |
| Stack | `.NET 10 · OmniRoute · EF Core · Blazor` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Delivered` |
| Ticket | `TBD` |
| Depends on | `30` (memory org+projeto), `61` recomendado (insight
  alimenta sugestões) |
| Reference | competitive-analysis §6.3 (G11); docs.devin.ai
  `/onboard-devin/knowledge-onboarding`, `/product-guides/memory`
  (Dreaming — dedup/organização auto) |
| Touches | `src/OpenHandsNET.{Application,Infrastructure,Api,Client}`, `tests/` |

## 1. User Story

**As a** admin de org
**I want** que a plataforma proponha entradas de memória a partir das
sessões (deduplicadas, com fila de revisão) e mantenha a base de
knowledge organizada
**So that** o conhecimento cresce curado — o modelo "knowledge +
suggestions + dreaming" do Devin. Hoje o SPEC-30 tem
remember/recall/forget/promote **manuais**: nada gera candidatos nem
detecta duplicatas.

## 2. Scope (RF)

### RF-001 — Suggestion generation

- `KnowledgeSuggestionService`: triggers —
  (a) fim de run (via insight do SPEC-61 quando presente; fallback:
  eventos `work_summary`/`agent_diagnostic`),
  (b) `remember` recorrente — mesma chave/valor re-lembrado N vezes
  em sessões diferentes → candidato a promote org,
  (c) manual `POST /api/v1/knowledge/suggestions` {texto/repo}.
- Extrai `knowledge_suggestions`: `{Id, OrgId, Scope (org|project),
  RepoPath?, Title, Content, SourceConversationIds[], Status
  (pending|approved|dismissed), Reason, CreatedAt}`.
- Role OmniRoute `analysis` para redigir entrada canônica.

### RF-002 — Review queue

- `GET /api/v1/knowledge/suggestions?status=` +
  `POST .../{id}/approve` (→ `IMemoryService.WriteAsync` no escopo
  alvo) + `POST .../{id}/dismiss`.
- Aprovação com edit: `{content_override?}`.
- Badge na UI (contador pending); página `Knowledge` com fila +
  entries existentes.

### RF-003 — Dedup & consolidation ("dreaming")

- Job periódico `KnowledgeGroomer` (diário — `Knowledge:GroomCron`):
  recall similar (mesmo tópico — embeddings se disponíveis, senão
  similaridade de tokens) → propõe merge (gera suggestion
  `kind=merge` com entries fonte) ou `kind=conflict` (duas entradas
  contraditórias → aponta ambas p/ revisão humana).
- `POST /api/v1/knowledge/groom` manual (admin) — relatório
  `{duplicates[], conflicts[], stale[]}` (stale = não re-lembra há
  `Knowledge:StaleDays`, default 90).

### RF-004 — Integração com agente

- `recall`/`remember` existentes: `remember` passa a checar duplicata
  (mesma chave+escopo → update em vez de insert — já faz? verificar
  e formalizar no SPEC).
- Nova tool `suggest_knowledge` — o agente propõe entrada para a fila
  de revisão em vez de gravar direto (para orgs que exigem curadoria —
  `Knowledge:AgentWriteMode=direct|suggest` por org).

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| GET | `/api/v1/knowledge/suggestions?status=&scope=` | fila |
| POST | `/api/v1/knowledge/suggestions/{id}/approve` | `{content_override?}` |
| POST | `/api/v1/knowledge/suggestions/{id}/dismiss` | `{reason?}` |
| POST | `/api/v1/knowledge/groom` | relatório dedup/conflict |
| GET | `/api/v1/knowledge/entries?scope=` | (alias p/ memory list — navegação) |

Erros: `404` suggestion · `409` já resolvida.

## 4. Data model

`knowledge_suggestions` (status, scope, content, sources json,
resolution). Migration.

## 5. NFR / Security

- Conteúdo sugerido passa por `SecretRedactor` + `IInputGuard` leve.
- Aprovação exige `perm:memory:write` no escopo alvo (org = admin).
- Groom é read+propose — nunca deleta automaticamente; merge exige
  aprovação.
- Custo LLM registrado em usage bucket `knowledge`.

## 6. Task Plan

1. Entidade + geração de sugestões (pós-run + re-remember + manual).
2. Fila de revisão (approve/dismiss/override) + badge UI.
3. Groom job (dedup/conflict/stale report).
4. `suggest_knowledge` tool + `AgentWriteMode`.
5. Docs (`docs/features/knowledge.md`).

## 7. Deliverables

Sugestões automáticas curadas por humanos; dedup/conflict grooming;
modo curadoria para orgs; fila visível na UI.

## 8. Referências

- `migration-plan/research/competitive-analysis-devin-openhands-cloud.md`.
- `.specs/30_centralized_memory_spec.md`, `.specs/61_session_insights_spec.md`.

## 9. Guardrails

- Nenhuma gravação em memória org sem aprovação quando
  `AgentWriteMode=suggest`.
- Groom nunca remove entries — só propõe.
- Sugestões expiram (`Knowledge:SuggestionTtlDays`, default 30).

## 10. DoD

- Run com aprendizado gera suggestion pending; approve grava na
  memória do escopo correto; dismiss fecha.
- Duas entries similares → groom reporta par; merge aprovado
  consolida.
- `AgentWriteMode=suggest` + tool `suggest_knowledge` → fila (não
  grava direto).
- Suite verde; golden OpenAPI atualizado.

## 11. Delivered

- `Application/Knowledge/KnowledgeContracts.cs` —
  `SuggestionStatus`, `SuggestionKind`, `KnowledgeSuggestionInfo`,
  `KnowledgeGroomReport`, `IKnowledgeService`.
- `Infrastructure/Knowledge/KnowledgeService.cs` —
  `KnowledgeSuggestionRecord` (`IOrgScoped`), approve via
  `IMemoryService` (`SecretRedactor` no conteúdo), Jaccard
  dedup/conflict/stale; `KnowledgeGroomer` hosted service.
- `Api/Endpoints/KnowledgeEndpoints.cs` — fila + approve/dismiss +
  groom + entries.
- `SessionInsightService` — cada `learning` do insight gera uma
  suggestion `entry` pendente (fire-and-forget via `IServiceProvider`).
- EF migrations `spec63_knowledge` (SQLite + Postgres).
- UI `/knowledge` + `ApiClient` + i18n (8 locales).
- `tests/.../Spec63KnowledgeTests.cs` — 7 testes verdes.
- Divergência: `suggest_knowledge`/`AgentWriteMode=suggest`
  deferred (tool dispatch no agente do sandbox — SPEC-62).
