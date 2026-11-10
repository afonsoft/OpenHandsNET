# SPEC — 146 · Multi-Repository Conversations (`selected_repositories[]`)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `multi-repo-conversations` |
| Type | `Feature / API + Sandbox + Agent` |
| Stack | `.NET 10 · start-task · git clone ×N · tools multi-root` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | `GAP-MULTI-REPO` (docs/research/2026-11-competitive-gap-analysis.md §G7) |
| Depends on | `04` (start-task), `12`/`15` (git), `52`/`53` (SCM creds) |
| Reference | OH Enterprise 0.103 Multi-Repository Conversations; Devin `/repo` multi-select — conceitos apenas |
| Touches | `src/Sandcastle.{Domain,Application,Infrastructure,Api,Client}`, `cli/`, `tests/` |

## 1. User Story

**As a** usuário com tarefa cross-repo (mudança API+cliente, refactor
compartilhado)
**I want** iniciar uma conversa com vários repositórios — o agente
clona todos, trabalha em cada um e abre PR por repo
**So that** paridade com OpenHands Enterprise 0.103 e Devin. Hoje
`CreateConversationRequest` aceita um único `selected_repository` e o
sandbox clona um repo só.

## 2. Scope (RF)

### RF-001 — Contrato

- `CreateConversationRequest`/`InitialMessage` ganha
  `selected_repositories: string[]` (max `MultiRepo:MaxRepos=4`).
  `selected_repository` permanece (primário/legado) — quando ambos,
  `selected_repository` deve ser o primeiro da lista.
- `ConversationRecord`/`AppConversationDto`: `RepositoriesJson` +
  `selected_repository` preservado como primário (compat retroativa —
  clients velhos continuam funcionando).

### RF-002 — Sandbox e clone

- Layout: `/workspace/repos/{short-name}/` por repo (com `RepoPath`
  legado `/workspace/repo` → symlink/bind para o primário, preservando
  tools/scripts que assumem o caminho).
- Clones em paralelo via `GitService` com credencial da conexão SCM
  de cada repo (repos podem vir de providers distintos);
  falha parcial → `setup` do agente informa quais clones falharam
  (run não aborta se primário ok).
- Blueprint `sandcastle.yaml` lido do repo primário; `.sandcastle/` de
  cada repo disponível como contexto (rules/AGENTS.md por repo).

### RF-003 — Agent + tools

- System prompt lista os repos montados; tools (`exec`, `str_replace`,
  `read`) recebem caminhos absolutos — sem mudança de schema;
  `git_*` tools ganham arg `repo` opcional (default: primário).
- PR: `ScmPullRequestService.CreateAsync` continua 1-PR-por-conversa
  no primário; novo `repository` param opcional permite PR num repo
  secundário (um por repo; `pull_requests_json` na conversa agrega
  todos — URLs/números/estado por repo).
- Ask/indexação (SPEC-60/145): indexer já é por `(repo,branch)` —
  conversa multi-repo referencia vários índices; citações incluem
  `repo:` prefix.

### RF-004 — UI/CLI

- Composer/seletor de repo vira multi-select (chips); badge no header
  com os repos (primário destacado).
- `sandcastle` CLI: `--repo` repetível.
- i18n: novas chaves em 8 locales.

### RF-005 — Fora de escopo

- PR único atômico cross-repo (não existe nos concorrentes — PRs são
  por repo) · sync de merges · monorepo subdirs seletivos ·
  `selected_repositories` em automations/delegate (delegates já levam
  `selected_repository` — herdar do pai quando omitido).

## 3. API Contract

| Field | Change |
|---|---|
| `POST /api/v1/app-conversations` | `+ selected_repositories[]` |
| `GET /api/v1/app-conversations/{id}` | `+ repositories[]`, `+ pull_requests[]` |
| `POST /api/v1/conversations/{id}/pull-request` | `+ repository?` |

Erros: `400` >MaxRepos ou repo inválido · `403` sem acesso a repo
secundário · `409` PR já existe naquele repo.

## 4. Data model

- `conversations` + `RepositoriesJson`, `PullRequestsJson` — migration
  dupla; `selected_repository`/`pull_request_*` legados continuam
  espelhando o primário.

## 5. NFR / Security

- Cada repo secundário exige `perm:conversations` + acesso via
  conexão SCM da org — mesma checagem do primário.
- Secrets por repo respeitam `SecretNamesJson` escopado.
- Custo: N clones + contexto maior — `MaxRepos` duro e quota normal.
- Cross-repo leaks: system prompt nomeia os repos; tools não vazam
  caminho de host (só `/workspace/...`).

## 6. Task Plan

1. Contrato/record/dto + migration (`RepositoriesJson`,
   `PullRequestsJson`).
2. Clone multi-repo no provisionamento do sandbox + symlink primário.
3. Tools `git_*` com `repo` param + system prompt multi-root.
4. PR por repo secundário + agregação `pull_requests`.
5. UI multi-select + badges + CLI `--repo` + i18n.
6. Testes: unit (contrato/compat), integração (2 repos clone+PR),
   golden OpenAPI.

## 7. Deliverables

Conversa com N repos clona tudo, trabalha cross-repo e abre PR por
repo — com compat total para `selected_repository` legado.

## 8. Referências

- `docs/research/2026-11-competitive-gap-analysis.md` §2.1 (0.103)/§4 G7.
- OH Enterprise 0.103 release notes (conceitos apenas).
- `docs/specs/04_rest_and_signalr_api_spec.md`,
  `12_git_scm_integration_spec.md`, `52_github_app_scm_spec.md`.

## 9. Guardrails

- Compat: cliente antigo enviando só `selected_repository` funciona
  igual; `repositories[]` é aditivo.
- Primário sempre clona; falha em secundário não derruba a run.
- `MaxRepos=4` duro — sem bypass por config de usuário.

## 10. DoD

- Conversa com 2 repos clona ambos, agente edita em cada um, PR aberto
  no secundário via `repository` param.
- Contrato antigo intacto (teste de compat); golden OpenAPI atualizado.
- Suite verde; migrations duplas; i18n parity verde.
