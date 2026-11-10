# SPEC — 141 · CI Autofix Loop (`check_run`/`workflow_run` → fix → verde)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `ci-autofix-loop` |
| Type | `Feature / SCM + Agent + Automations` |
| Stack | `.NET 10 · webhook check_run/check_suite/workflow_run · start-task` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | `GAP-SCM-CI-AUTOFIX` (docs/research/2026-11-competitive-gap-analysis.md §G2) |
| Depends on | `52`/`53` (SCM), `09` (runner), `140` (review findings como entrada opcional) |
| Reference | Devin "Fix CI Failures" automation + Auto-Fix; OH `iterate` skill (conceitos apenas) |
| Touches | `src/Sandcastle.{Application,Infrastructure,Api,Client}`, `tests/` |

## 1. User Story

**As a** autor de PR (agente ou humano)
**I want** que uma falha de CI no PR dispare automaticamente uma sessão
que lê os logs, corrige e empurra o fix na branch — repetindo até verde
ou até o limite
**So that** o ciclo push→CI→fix→CI se fecha sem intervenção (paridade
Devin autofix / OH iterate). Hoje nenhum evento `check_run`/
`workflow_run` chega ao sistema — o grep não encontra ocorrências.

## 2. Scope (RF)

### RF-001 — Ingestão de eventos de check

- Webhook passa a aceitar (GitHub): `check_run` (completed),
  `check_suite` (completed), `workflow_run` (completed), `status`.
  Normalizar para `CiCheckEvent {repo, sha, pr_number?, name,
  conclusion, url, provider}`.
- Resolver PR: `check_run.pull_requests[]`; senão lookup por head SHA
  via `GET /commits/{sha}/pulls`; senão match contra
  `conversations.PullRequestHeadSha`.
- Só falhas (`failure|timed_out|action_required|cancelled`) em PRs de
  **repos inscritos** (`AutofixEnabled` na connection/repos) ou cujo PR
  foi criado por conversa Sandcastle.

### RF-002 — Política de disparo (anti-loop — crítico)

- **Nunca** disparar por check do próprio Sandcastle
  (`sandcastle/*`, `Devin Review`-like contexts) nem por commits
  cujo autor é o bot (`Autofix:BotAuthors`, default
  `sandcastle-agent`, `github-actions[bot]`).
- Dedup: um fix-run por `(repo, pr, head_sha, check_name)`; novo head
  SHA reseta o contador.
- `Autofix:MaxAttempts=3` por PR (org-config) — ao esgotar, comenta o
  diagnóstico final + abre attention item.
- Gate de escopo: só dispara para check names que batem
  `Autofix:CheckNamePatterns` (default `*`) — org pode restringir a
  `test|lint|typecheck` (timeouts de infra/Docker não valem fix).

### RF-003 — Fix run

- Cria `AutofixRunRecord` + conversa interna (ou job
  `IAutofixRunner` compartilhando pipeline do SPEC-140):
  clona a **branch do PR** no sandbox, baixa logs do check
  (`GetCheckRunLogsAsync`/`GetWorkflowRunLogsAsync` — novo provider
  method; trunca `Autofix:MaxLogBytes=256KB`), prompt: falha + diff +
  instrução "fix e commit".
- O agente commita na branch do PR (reusa `GitService.CommitAsync` +
  `PushAsync` com credencial da conexão) → novo check → observador
  (`AutofixWatcher` hosted service, poll `Autofix:PollSeconds=60`,
  timeout `Autofix:RunTimeoutMinutes=45`) decide: verde → sucesso;
  falha → nova tentativa até cap; senão comenta root-cause + log trecho.
- `sandcastle/autofix` commit status: `pending` durante loop,
  `success`/`failure` no fim.

### RF-004 — Findings/attempts visíveis

- `autofix_runs` + `autofix_attempts` (run, attempt#, head_sha_in/out,
  checks_failed_json, outcome `fixed|gave_up|blocked`, log_digest).
- Card "Autofix" na página da conversa/PR + timeline de tentativas;
  `GET /api/v1/scm/autofix-runs?repository=&pr=`.
- Comentário no PR ao esgotar tentativas (root cause + o que tentou) —
  via `CreateIssueCommentAsync` existente.
- Auto-fix opcional p/ findings do SPEC-140: toggle
  `Autofix:FixReviewFindings=false` — quando ON, findings
  `bug|security` viram prompt de fix adicional.

### RF-005 — Fora de escopo

- Stacked PRs · merge automático · fixes em repos sem connection ·
  re-run de workflow inteiro · edição de workflows `.github/`.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| POST | `/api/v1/scm/autofix` | `{repository, pr_number}` manual → `{run_id}` (202) |
| GET | `/api/v1/scm/autofix-runs` | filtros `repository, pr, outcome` |
| GET | `/api/v1/scm/autofix-runs/{id}` | run + attempts |
| PATCH | `/api/v1/scm/connections/{id}` | `autofix_*` campos |

Webhook: aceita `check_run|check_suite|workflow_run|status` no endpoint
github existente (HMAC + idempotência já estabelecidos).

## 4. Data model

- `scm_connections` + `AutofixEnabled`, `AutofixReposJson`,
  `AutofixCheckPatterns`, `AutofixMaxAttempts`.
- `autofix_runs` (id, connection, repo, pr, head_sha, status, attempt_count,
  conversation_id, created/finished).
- `autofix_attempts` (id, run_id, n, sha_in, sha_out, failed_checks_json,
  outcome, log_digest).

## 5. NFR / Security

- Logs de CI são untrusted (podem conter secrets → `SecretRedactor`;
  podem conter instruções → envelope SPEC-91).
- Push na branch do PR usa credencial da conexão — nunca credencial
  pessoal do autor do PR.
- Cap duro de attempts; cada tentativa consome quota do owner.
- Não seguir redirects/links dentro de logs; não executar conteúdo de
  logs — só ler como diagnóstico.

## 6. Task Plan

1. `CiCheckEvent` normalizer + parse dos 4 event types + PR resolution.
2. `AutofixPolicy` (repos, bot-authors, check patterns, dedup, caps).
3. Provider: `GetCheckRunLogsAsync` (+ `GetWorkflowRunLogsAsync` GitHub).
4. `IAutofixRunner` (clone branch → logs → fix → push) + `AutofixWatcher`.
5. Status/comments + API + UI card.
6. Testes: unit (policy/dedup/PR-resolution), integração com provider
   fake, golden OpenAPI.

## 7. Deliverables

Falha de CI elegível dispara fix-run; agente empurra correções até
verde ou cap; tentativas auditáveis; comentário final com root cause.

## 8. Referências

- `docs/research/2026-11-competitive-gap-analysis.md` §2.2/§2.3/§4 G2.
- Devin `use-cases/gallery/api-github-actions-ci-fix` +
  `product-guides/automations` (check_run trigger) — conceitos apenas.
- OH `extensions/skills/iterate/SKILL.md` (push→poll→fix→push) —
  conceitos apenas.
- `docs/specs/52_github_app_scm_spec.md`; SPEC-140 (reusa runner).

## 9. Guardrails

- Cap 3 tentativas default; nunca desativar anti-loop por autor.
- Fix só na branch do PR — nunca em `main`/protegidas direto.
- Comentários de autofix são do bot → não re-triggam (SPEC-140 RF-005).

## 10. DoD

- `check_run` failure em PR inscrito → fix-run criado; log lido; push na
  branch; poll detecta verde.
- `MaxAttempts` respeitado; comentário de give-up postado.
- Check do próprio Sandcastle/autor bot → ignorado (teste).
- Suite verde; migrations duplas; golden OpenAPI.
