# SPEC — 145 · Repo Wiki + MCP exposure (DeepWiki open-source)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `repo-wiki-mcp` |
| Type | `Feature / Indexing + Docs + MCP` |
| Stack | `.NET 10 · clone real por branch · LLM docs · MCP server` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | `GAP-WIKI-MCP` (docs/research/2026-11-competitive-gap-analysis.md §G6) |
| Depends on | `60` (indexação/Ask), `07`+`124` (MCP), `52` (SCM/clone creds) |
| Reference | Devin DeepWiki (wiki por repo, `.devin/wiki.json`, esforço, MCP `read_wiki_structure`/`read_wiki_contents`/`ask_question`) — conceitos apenas |
| Touches | `src/Sandcastle.{Application,Infrastructure,Api,Client,Mcp}`, `tests/` |

## 1. User Story

**As a** desenvolvedor entrando num repo novo
**I want** uma wiki auto-gerada por branch (visão de arquitetura,
tópicos, diagramas, citações file:line) navegável na UI e consultável
via MCP por qualquer cliente
**So that** o Sandcastle entrega o equivalente aberto do DeepWiki.
Hoje o `RepoIndexer.RunIndexingAsync` varre
`Directory.GetCurrentDirectory()` do servidor de API — **não clona o
repo e ignora `branch`/`CommitSha`**: a indexação "por repo" é nominal.
E o `Sandcastle.Mcp` (stdio, 12 tools) não expõe wiki nem ask.

## 2. Scope (RF)

### RF-001 — Indexação real por repo/branch (correção)

- `RunIndexingAsync` passa a obter o conteúdo do repo de verdade:
  `IRepoContentProvider` com duas implementações — (a) clone shallow
  (`git clone --depth 1 --branch <b>`) via `IScmProvider` credenciais
  para repos remotos, em workdir temporário; (b) `local` para o repo
  hospedado (dev) — hoje o único caso coberto.
- Persiste `CommitSha` resolvido no `RepoIndexRecord`; re-index quando
  `push` webhook (novo evento, mesma ingestão do SPEC-140/141) muda o
  head da branch — `Indexing:AutoRefreshOnPush=true`.
- Ask passa a citar `path:Lx-Ly` reais do repo indexado + link pro
  provider (`{repo}/blob/{sha}/{path}#Lx-Ly`).

### RF-002 — Wiki generation

- `POST /api/v1/repos/{repo}/wiki/generate {branch?, effort?}` → job
  (conversa `agent_type: wiki-writer` ou batch runner) que lê o índice
  + arquivos-chave e produz `wiki_pages`: árvore de tópicos
  (`overview`, `architecture`, `modules/{x}`, `data-flow`, `deployment`,
  `conventions`), markdown com diagramas mermaid e citações
  `[path:Lx-Ly](permalink)`.
- `.sandcastle/wiki.json` no repo: `{include[], exclude[], language,
  effort, topics_hint[]}` — config por repo (espelho de
  `.devin/wiki.json`).
- `effort low|medium|high` → nº de páginas/tokens
  (`Wiki:EffortMatrix` 5/15/40 páginas alvo); custo contado como usage
  normal; low é gratuito-limitado.
- Re-geração: on-demand + auto em push (opt-in por repo); diff-aware —
  só páginas cujos arquivos-fonte mudaram re-geram (`stale` flag por
  página via hash dos arquivos cobertos).

### RF-003 — UI

- Sidebar "Wiki" → browser de wiki: árvore de tópicos, página render
  (markdown + mermaid), seletor de branch, badge `stale` + botão
  regenerate, "X/Y repos indexados" counter.
- Ask mode referencia páginas da wiki como contexto adicional quando
  existem (prompt enrichment — feature flag `Wiki:AskEnrichment=true`).

### RF-004 — MCP exposure

- `Sandcastle.Mcp` ganha tools `sc_wiki_structure`
  `{repository, branch?}` → árvore; `sc_wiki_contents` `{repository,
  topic|page_id}` → markdown; `sc_ask` `{repository, question,
  branch?}` → resposta+citações (proxy do `IAskModeService`).
- Transporte: além do stdio atual, `Sandcastle.Mcp` aceita
  `--http :PORT` (Streamable HTTP, `/mcp`) para clientes remotos —
  mesmo modelo do deepwiki MCP (`mcp.deepwiki.com/mcp`), auth por
  `sk-*` bearer.
- Golden: atualizar `tools/check-mcp-tools-golden.py` baseline.

### RF-005 — Fora de escopo

- Wiki público estilo deepwiki.com (site separado) · badge auto-refresh ·
  embeddings semânticos (search continua keyword/símbolo — evolução
  futura) · multi-repo wiki agregada · diagramas drawio.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| POST | `/api/v1/repos/{repo}/wiki/generate` | `{branch?, effort?}` → 202 |
| GET | `/api/v1/repos/{repo}/wiki` | árvore (`?branch=`) |
| GET | `/api/v1/repos/{repo}/wiki/pages/{pageId}` | conteúdo |
| GET | `/api/v1/indexing/status` | lista repos+branch+stats (counter X/Y) |
| POST | `/api/v1/indexing/reindex` | `{repository, branch}` |

MCP: +3 tools (`sc_wiki_structure`, `sc_wiki_contents`, `sc_ask`) +
transporte HTTP opcional.

## 4. Data model

- `repo_indexes` já tem `CommitSha` (começar a gravar).
- `wiki_runs` (id, org, repo, branch, commit_sha, effort, status,
  stats_json, created/finished).
- `wiki_pages` (id, wiki_run_id, repo, branch, topic, path_key,
  title, content_md, source_files_json, content_hash, stale, order).

## 5. NFR / Security

- Clones usam credenciais da conexão SCM da org — respeitar
  `perm:indexing.manage` (quem pode indexar) e `perm:wiki.read`.
- Conteúdo indexado sanitizado (`SecretRedactor` já existe — manter);
  wiki **não** inclui arquivos `exclude`d nem secrets detectados.
- HTTP MCP endpoint exige `sk-*` e respeita IP allowlist (SPEC-59).
- Branch default = default branch do repo; branchs arbitrários
  permitidos (multi-branch é o ponto).

## 6. Task Plan

1. `IRepoContentProvider` (clone shallow + local) + CommitSha +
   `push` webhook → re-index.
2. `wiki_runs`/`wiki_pages` + wiki-writer skill + esforço matrix.
3. UI wiki browser + counter + stale badges.
4. MCP tools + HTTP transport + golden update.
5. Ask enrichment via wiki (flag).
6. Testes: unit (chunk/hash/stale), integração (repo git local → wiki),
   MCP golden, OpenAPI golden.

## 7. Deliverables

Wiki navegável e citável por repo/branch, regenerável, exposta a
qualquer cliente MCP — DeepWiki MIT, com indexação correta de verdade.

## 8. Referências

- `docs/research/2026-11-competitive-gap-analysis.md` §2.3/§4 G6.
- Devin `work-with-devin/deepwiki` + `deepwiki-mcp` (conceitos apenas).
- `docs/specs/60_repo_indexing_ask_mode_spec.md`,
  `124_mcp_acp_exposure_spec.md`, `52_github_app_scm_spec.md`.

## 9. Guardrails

- Corrigir indexação SEM quebrar o Ask atual (contrato
  `IAskModeService` estável).
- Wiki nunca inventa citações: toda `path:Lx` deve existir no índice
  (validação pós-geração; página com citação inválida → `stale`).
- MCP HTTP off por default (stdio mantém compat).

## 10. DoD

- Repo git fixture remoto → indexado com CommitSha real + Ask cita
  permalinks corretos.
- `wiki/generate` produz árvore+páginas com diagramas e citações
  válidas; push muda head → páginas afetadas `stale`.
- `sc_wiki_*` e `sc_ask` respondem via stdio e HTTP; golden atualizado.
- Suite verde; migrations duplas.
