# SPEC — 140 · PR Review Agent (auto-review em `pull_request` + findings)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `pr-review-agent` |
| Type | `Feature / SCM + Agent` |
| Stack | `.NET 10 · GitHub/GitLab webhook · start-task · commit status` |
| Repository | `afonsoft/sandcastle` |
| Status | `Done` |
| Ticket | `GAP-SCM-PR-REVIEW` (docs/research/2026-11-competitive-gap-analysis.md §G1) |
| Depends on | `52` (GitHub App), `53` (providers), `09` (agent runner), `47` (automations) |
| Reference | OH Verification Stack L2 (`/codereview` + `/github-pr-review`); Devin Review (commit status + findings + re-review skip) — conceitos apenas |
| Touches | `src/Sandcastle.{Application,Infrastructure,Api,Client}`, `tests/` |

## 1. User Story

**As a** usuário com o GitHub App instalado
**I want** que todo PR (do Sandcastle ou humano) nos repos inscritos seja
revisado automaticamente por um agente que posta comentários inline com
prioridade e um commit status
**So that** o primeiro revisor é sempre a máquina — paridade com Devin
Review e com o Verification Stack do OpenHands (hoje o webhook só
responde a `@sandcastle`; nenhum `pull_request` event dispara review).

## 2. Scope (RF)

### RF-001 — Enrollment e triggers

- `ScmConnection` ganha `ReviewEnabled` + `ReviewReposJson` (vazio = todos
  os repos da conexão) + `ReviewTriggerMode` (`all_prs`|`label_only`).
- Webhook passa a aceitar `pull_request` com actions
  `opened`/`synchronize`/`ready_for_review`/`labeled`/`unlabeled`
  (GitHub; GitLab `Merge Request Hook` equivalente).
- Regras de disparo: PR não-draft + (mode=all_prs **ou** label
  `sandcastle-review` presente); reviewer request
  `sandcastle-agent[bot]` também dispara (campo `requested_reviewer`).
- Trigger manual: `POST /api/v1/scm/reviews {repository, pr_number}` +
  botão "Review with Sandcastle" na página da conversa/PR.

### RF-002 — Review run

- Disparo cria `ReviewRunRecord` e inicia conversa interna
  (`agent_type: pr-review`, prompt de sistema dedicado) ou job
  lightweight — decisão: **job próprio** `IReviewRunner` que reusa
  `AgentRunner` com sandbox clonando head SHA (não precisa de conversa
  visível na inbox; roda sob service identity do owner da conexão).
- O agente recebe: diff completo (novo provider method
  `GetPullRequestFilesAsync` + `GetFileContentAsync`), regras do repo
  (`AGENTS.md`, `CLAUDE.md`, `.sandcastle/review.md`, `.sandcastle/rules/`
  — lidas do clone, concat ≤ `Review:MaxRulesChars=8000`), e skill
  `code-review` builtin (padrão `*.agent.md` da SPEC-95).
- Saída estruturada: findings tipados
  `kind: bug|security|investigate|note`, `severity: critical|important|
  suggestion|nit`, `confidence 0..1`, `path`, `line`, `title`, `body`,
  `suggested_patch?`.

### RF-003 — Posting

- Novo provider method `CreateReviewAsync(connection, repo, prNumber,
  headSha, body, comments[])` → GitHub `POST /pulls/{n}/reviews`
  (event=COMMENT, comments com `path`/`line`/`side`); fallback
  `CreateIssueCommentAsync` único quando provider não suporta review
  (Bitbucket/AzDO v1).
- Prioridade como emoji/label no comentário (🔴🟠🟡🟢) + agrupamento
  no summary da review.
- `sandcastle/review` commit status via `SetCommitStatusAsync` com
  pass-condition configurável por conexão: `completed` (default) |
  `no_critical` | `no_findings`. **Nunca required por default** —
  branch rules do repo decidem.
- Idempotência: dedup por `(repo, pr, head_sha, diff_hash)` — pula
  re-review quando diff vs base inalterado (paridade Devin re-review
  skip); `synchronize` com novo head → novo run.

### RF-004 — Findings persistidos + UI

- `ReviewFindingRecord`: id, org, repo, pr, review_run_id, kind,
  severity, confidence, path/line, body, `status: open|fixed|dismissed|
  stale`, created/updated.
- Página "Reviews" na conversa/PR card: findings agrupados por
  severidade, link para o comentário, marcar dismissed (feedback p/
  futuro tuning — só registrado, não treina nada).
- `GET /api/v1/scm/reviews?repository=&pr=&status=` listagem + detalhe.

### RF-005 — Anti-loop e limites

- Comentários do próprio bot (`sandcastle-agent[bot]` ou login do
  provider) nunca disparam novo review nem automations.
- `Review:MaxFilesPerRun=200`, `MaxDiffBytes=512KB` — acima disso revisa
  os arquivos mais relevantes (heurística: não-generated, não-lock, não
  vendor) e marca o review como `partial`.
- Quota: run de review conta para usage do owner da conexão;
  `Review:MaxDailyRunsPerRepo` (default 50).

### RF-006 — Fora de escopo

- Autofix dos achados (SPEC-141) · QA funcional (SPEC-142) ·
  re-review de stacked PRs · review em AzDO/Bitbucket inline (v1 =
  summary comment) · bloqueio de merge obrigatório.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| POST | `/api/v1/scm/reviews` | `{repository, pr_number}` → `{review_run_id}` (202) |
| GET | `/api/v1/scm/reviews` | filtros `repository, pr, status` + cursor |
| GET | `/api/v1/scm/reviews/{id}` | run + findings |
| PATCH | `/api/v1/scm/reviews/{id}/findings/{fid}` | `{status}` → dismissed/open |
| PATCH | `/api/v1/scm/connections/{id}` | `review_*` campos novos |

Webhook: `X-GitHub-Event: pull_request` aceito no endpoint existente
`/api/v1/scm/webhooks/github` (mesma cadeia HMAC→idempotência).

Erros: `404` repo/PR · `409` review em andamento p/ mesmo head ·
`403` quota.

## 4. Data model

- `scm_connections` + `ReviewEnabled`, `ReviewReposJson`,
  `ReviewTriggerMode`, `ReviewPassCondition` — migration (SQLite+PG).
- `review_runs` (id, connection_id, repo, pr_number, head_sha, diff_hash,
  status, conversation_id?, stats_json, created/finished).
- `review_findings` (RF-004); índice `(repo, pr_number)`, `(status)`.

## 5. NFR / Security

- Diff/comentários passam por `SecretRedactor` antes de persistir e de
  ir ao LLM; payload de webhook continua untrusted (SPEC-91).
- Review roda com a identidade do dono da conexão (mesmo modelo do
  mention flow); service account dedicada fica para SPEC de automations.
- Rate limit de posting: ≤ `Review:MaxInlineComments=30`/run — excedente
  vira linha no summary.
- Token do provider com escopo mínimo (`pull_requests:write`,
  `statuses:write`).

## 6. Task Plan

1. Schema: campos de review em `scm_connections` + `review_runs` +
   `review_findings` (migrations duplas).
2. Provider: `GetPullRequestFilesAsync`, `GetFileContentAsync`,
   `CreateReviewAsync` (GitHub primeiro; stubs 501 nos demais).
3. Webhook: parse `pull_request` + regras de disparo + dedup.
4. `IReviewRunner` + skill `code-review` builtin + rules loader.
5. Posting (review inline + status) + findinds API + UI card.
6. Testes: unit (parse/dedup/pass-condition), integração (webhook→run
   com provider fake), golden OpenAPI.

## 7. Deliverables

Todo PR em repo inscrito recebe review automático com comentários
inline priorizados, `sandcastle/review` status e findings navegáveis —
sem ação do usuário.

## 8. Referências

- `docs/research/2026-11-competitive-gap-analysis.md` §2.2/§4 G1.
- OH: `docs.openhands.dev/openhands/usage/use-cases/code-review`;
  blog `20260506-the-verification-stack` (conceitos apenas).
- Devin: `docs.devin.ai/work-with-devin/devin-review` (conceitos apenas).
- `docs/specs/52_github_app_scm_spec.md`, `53_scm_providers_spec.md`,
  `95_declarative_agent_catalog_spec.md`.

## 9. Guardrails

- Comment-only por default; pass-condition `no_findings` é opt-in e
  documentado como advisory (branch rules mandam).
- Nunca reagir a bot comments; nunca editar comentários de outros.
- Repo rules são instruções *do repo* — sanitizar como untrusted input
  (envelope SPEC-91) antes de entrar no prompt.

## 10. DoD

- `pull_request` opened em repo inscrito → review postado com findings
  corretos em provider fake; `synchronize` com mesmo diff → skip;
  label-gated mode respeitado.
- `sandcastle/review` status postado conforme pass-condition.
- API + UI listam findings; dismiss persiste.
- Suite verde; migrations duplas; golden OpenAPI.
