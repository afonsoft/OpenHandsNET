# SPEC — 60 · Repo Indexing + Ask Mode (read-only Q&A)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `repo-indexing-ask-mode` |
| Type | `Feature / AI + API + Frontend` |
| Stack | `.NET 10 · EF Core · embeddings opcionais · Blazor WASM` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed` |
| Ticket | `TBD` |
| Depends on | `04`, `09` (LLM provider), `22` (context), `23` (OmniRoute) |
| Reference | competitive-analysis §6.3 (G8/G33); docs.devin.ai
  `/work-with-devin/ask-devin`, `/work-with-devin/deepwiki` |
| Touches | `src/OpenHandsNET.{Domain,Application,Infrastructure,Api,Client}`, `tests/` |

## 1. User Story

**As a** dev explorando um repositório
**I want** um modo Ask read-only que responde perguntas sobre o código
com citações file:line e gera um plano/prompt enriquecido para handoff
a uma sessão Agent
**So that** reproduzimos o Ask Devin — a feature-assinatura do Devin
Web que não existe em nenhum OpenHands. Requer índice de código por
repositório (mesma base que futura geração de docs/diagramas, G33).

## 2. Scope (RF)

### RF-001 — Repo index

- Entidade `repo_indexes`: `Id, OrgId, Repository (full_name),
  Branch, CommitSha, Status (indexing|ready|stale|failed), StatsJson`.
- `IRepoIndexer`: clona/fetch shallow em sandbox efêmero (reusa
  `ISandboxEngine` modo `index`) ou em host worker; extrai unidades
  `code_units` (`path, start_line, end_line, kind (symbol|chunk),
  text, hash` — chunking por AST-simplificada: regex de declarações +
  fallback sliding window).
- Storage: tabela `code_units` (FTS via SQLite FTS5 / Postgres
  `tsvector` — dupla impl atrás de `ICodeSearchStore`); embeddings
  **opcionais** (`Index:Embeddings=off|on` + role OmniRoute `embeddings`
  — pgvector quando Postgres; sem pgvector → FTS only).
- Reindex: `POST /api/v1/repos/{id}/reindex`; auto on push via webhook
  SCM (SPEC-52/53) → marca `stale` + reindex em background.
- Respeita `.openhands/indexignore` (glob de exclusão).

### RF-002 — Ask conversation

- `POST /api/v1/ask-sessions` `{repository, question}` → conversa
  especial `Mode=ask` (não cria sandbox de escrita; workspace read-only
  sobre o índice).
- Pipeline: pergunta → `code_search` (FTS [+embedding rerank]) →
  top-N unidades → LLM (role `ask` no OmniRoute) → resposta
  **com citações obrigatórias** `path:Lx-Ly` resolvidas para links.
- Ferramentas do modo ask (read-only): `code_search`, `file_read`
  (do índice/snapshot), `list_symbols` — sem bash/edit/browser.
- Follow-ups no mesmo thread; `POST /{id}/promote` → gera
  `plan_document` + `start-task` de conversa Agent normal com o plano
  como initial_message (handoff Ask→Agent, igual Devin).

### RF-003 — UI

- Home: toggle `Ask | Agent` no composer; página `/ask/{repo}`.
- Resposta renderiza citações clicáveis → file viewer read-only do
  índice (Monaco somente-leitura).
- Lista de ask-sessions separada (histórico de Q&A).

### RF-004 — Index management admin

- Settings `Repos`: indexed list, status, trigger reindex,
  `.indexignore` docs, stats (units, cobertura de linguagens).
- GC: índices de repos desconectados/expirados limpos por job.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| GET/POST | `/api/v1/repos` | list/add repo p/ indexar |
| GET | `/api/v1/repos/{id}` | status+stats |
| POST | `/api/v1/repos/{id}/reindex` | |
| GET | `/api/v1/repos/{id}/search?q=` | debug/admin |
| GET/POST | `/api/v1/ask-sessions` | `{repository, question}` → session |
| GET | `/api/v1/ask-sessions/{id}` | |
| POST | `/api/v1/ask-sessions/{id}/messages` | follow-up |
| POST | `/api/v1/ask-sessions/{id}/promote` | → `{start_task_id}` |

Erros: `400` repo não indexado (`index_required`) · `404` · `409`
indexing em curso.

## 4. Data model

`repo_indexes`, `code_units` (FTS5/tsvector + colunas), `ask_sessions`
(modo ask reusa `conversations` + `Mode` + `RepoIndexId`). Migrations
SQLite+Postgres; migration Postgres cria `tsvector` + opcional pgvector.

## 5. NFR / Security

- Índice é por org — ACL herdada (membro da org lê; `perm:repos:read`).
- Ask mode não executa nada — ferramentas read-only enforced no
  `ToolRegistry` (allowlist `Mode=ask`).
- Custos: reindex limitado (≤N arquivos/≤MB por repo — config);
  embeddings off por default.
- Conteúdo indexado passa por `SecretRedactor` antes de persistir
  (evita virar oráculo de secrets).

## 6. Task Plan

1. `code_units` + `ICodeSearchStore` FTS (SQLite/Postgres) + indexer
   (clone shallow + chunking).
2. Repos endpoints + reindex worker + stale on webhook.
3. Ask pipeline (search→LLM→citações) + `Mode=ask` tool allowlist.
4. Promote→Agent handoff.
5. UI (toggle, página, citações clicáveis) + admin + CLI
   (`openhandsnet ask`).

## 7. Deliverables

Repos indexados com busca; modo Ask com citações file:line; handoff
plan→Agent; admin de índices.

## 8. Referências

- `migration-plan/research/competitive-analysis-devin-openhands-cloud.md`.
- `docs/specs/22_context_engine_compaction_spec.md` (chunking/ledger),
  `docs/specs/23_omniroute_model_catalog_routing_spec.md` (role `ask`,
  `embeddings`), `docs/specs/42_ide_workspace_spec.md` (Monaco reuse).

## 9. Guardrails

- Sem dependência de serviço externo de busca (Elastic/Qdrant) —
  FTS nativo dos dois bancos; embedding opcional.
- Indexar é operação de leitura — nunca toca no repo além de
  clone/fetch.
- Ask sessions contam quota como conversas (SPEC-20), bucket próprio
  `ask_sessions/dia` se quisermos mais barato — default: mesma quota.

## 10. DoD

- Indexar repo real → `code_units` >0, status ready.
- Pergunta retorna resposta com ≥1 citação resolvível para o arquivo.
- Promote cria start-task Agent com o plano no initial_message.
- Repo sem index → 400 `index_required`. FTS Postgres+SQLite cobertos
  por teste parametrizado. Suite verde; golden OpenAPI atualizado.

## 11. Delivered

- `Infrastructure/Indexing/` + `Api/Endpoints/AskAndIndexEndpoints.cs` —
  repo indexing (`code_units`, `repo_indexes`) + Ask read-only.
- Entidades `CodeUnitRecord`/`RepoIndexRecord` (EF); migration tardia
  absorvida em `spec63_knowledge` (PR #140) — ver spec 63 §Delivered.
- PR: https://github.com/afonsoft/OpenHandsNET/pull/136
