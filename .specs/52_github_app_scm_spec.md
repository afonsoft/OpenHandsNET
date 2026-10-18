# SPEC — 52 · GitHub App + SCM Provider Abstraction

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `github-app-scm` |
| Type | `Feature / Integration` |
| Stack | `.NET 10 · Octokit (net) · webhooks · EF Core` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
| Ticket | `TBD` |
| Depends on | `04` (API), `12` (git_scm), `47` (automations/webhooks), `10` (EF) |
| Reference | `migration-plan/research/competitive-analysis-devin-openhands-cloud.md` (G1); docs.devin.ai `/integrations/gh`; docs.openhands.dev `cloud/github-installation` |
| Touches | `src/OpenHandsNET.{Domain,Application,Infrastructure,Api}`, `cli/`, `tests/` |

## 1. User Story

**As a** usuário do OpenHandsNET
**I want** que o agente abra PRs, responda a review comments e seja
disparado por `@openhandsnet` em issues/PRs do GitHub
**So that** o fluxo ticket→PR fica completo sem abrir a UI — paridade
com GitHub App do OpenHands Cloud e @Devin do Devin Web. Hoje só existe
`git_commit` local (SPEC-12) e credenciais git por host; não há entidade
SCM, webhooks verificados nem escrita de PR.

## 2. Scope (RF)

### RF-001 — `IScmProvider` + registro de providers

- `Application/Scm/IScmProvider.cs`: `CreatePullRequestAsync`,
  `CreateIssueCommentAsync`, `ReplyToReviewCommentAsync`,
  `GetPullRequestAsync`, `SetCommitStatusAsync`, `ParseWebhookAsync`.
- `ScmProviderRegistry` keyed por `ScmKind` (`github` nesta SPEC;
  `gitlab|bitbucket|azuredevops` entram na SPEC-53 como implementações
  da mesma interface — nenhum endpoint muda).
- `Octokit` (produto `Octokit`, versão fixa ≥7 dias) como client GitHub.

### RF-002 — GitHub App credentials por org

- Entidade `scm_connections`: `Id, OrgId, Kind, InstallationId,
  PemSecretRef (SecretNamesJson), WebhookSecretRef, ReposJson, CreatedAt`.
- Endpoints admin: `POST/GET/DELETE /api/v1/orgs/{org}/scm-connections`
  (e `/api/v1/scm/callback` para o fluxo de instalação do GitHub App —
  manifest flow: `POST /api/v1/scm/github/manifest` retorna manifest
  JSON; callback `GET /api/v1/scm/github/callback?code=` troca code→
  credenciais do app).
- Token de instalação cacheado 50 min (`IDistributedCache` SPEC-36);
  nunca persistido.

### RF-003 — Pull request a partir do workspace

- `POST /api/v1/app-conversations/{id}/pull-request` → body:
  `{ title, body, base?, head?, draft?, reviewers? }`. Executa push do
  branch do sandbox via credencial da org → `CreatePullRequestAsync` →
  grava `PullRequestUrl`/`Number` na conversa + evento
  `pull_request_created` no stream.
- Tool `create_pull_request` no registry (args: title/body/base/draft) —
  o agente chama como `git_commit` hoje.

### RF-004 — Webhook verificado + triggers

- `POST /api/v1/webhooks/scm/github` — assinatura HMAC-SHA256
  (`X-Hub-Signature-256`), `X-GitHub-Event` roteado para:
  - `issue_comment`/`pull_request_review_comment` com `@openhandsnet`
    → start-task com contexto (repo, branch, comentário, diff hunk).
  - `pull_request.review` com `@openhandsnet` → idem.
  - `pull_request.synchronize` em PR de conversa viva → re-avalia CI
    guard (postar status).
- Idempotência por `X-GitHub-Delivery` (tabela `webhook_deliveries`,
  chave única `DeliveryId`).
- Resposta a review comments: thread-aware — `in_reply_to_id` mantém a
  conversa no thread correto.

### RF-005 — Commit status

- Ao completar run de conversa originada por PR ou com PR aberto:
  `SetCommitStatusAsync` (`openhandsnet/run` → success/failure +
  `target_url` da conversa) no head SHA.

### RF-006 — Settings/UX

- Seção `SCM` em settings admin (list connections, install GitHub App
  via manifest flow, repos cobertos). Status do app na página da
  conversa (link do PR quando existe).
- CLI: `openhandsnet scm status|connect|pr create`.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| POST | `/api/v1/scm/github/manifest` | retorna manifest p/ criar o App |
| GET | `/api/v1/scm/github/callback` | troca `code` → credenciais |
| POST | `/api/v1/webhooks/scm/github` | público, HMAC verificado, idempotente |
| GET/POST/DELETE | `/api/v1/orgs/{org}/scm-connections` | admin |
| POST | `/api/v1/app-conversations/{id}/pull-request` | `{title,body,base?,head?,draft?}` → `{url,number}` |

Erros: `400` payload inválido · `401` assinatura · `404` connection ·
`409` PR já existe p/ o head.

## 4. Data model

`scm_connections` (org, kind, installation_id, pem/webhook secret refs,
repos), `webhook_deliveries` (delivery_id unique, kind, payload_sha,
processed_at), campos `pull_request_url/number/state` na conversa.
Migrations SQLite + Postgres.

## 5. NFR / Security

- PEM e webhook secret via `SecretNamesJson` — nunca no DB em claro.
- Webhook sem auth de usuário; segurança = HMAC + idempotência +
  payload limit (256 KB) + rate limit por IP.
- Audit: `scm.pr_created`, `scm.webhook_triggered`, `scm.connection_added`.
- GitHub App permissions mínimas: `contents:rw`, `pull_requests:rw`,
  `issues:rw`, `statuses:rw`, `metadata:r`.

## 6. Task Plan

1. `IScmProvider` + registry + `GitHubScmProvider` (Octokit) + entidades
   + migrations.
2. Manifest flow + connections endpoints + install token cache.
3. `pull-request` endpoint + tool `create_pull_request` + push do head.
4. Webhook receiver (HMAC, idempotência, roteamento) + start-task por
   menção + reply-in-thread.
5. Commit status no fim de run.
6. Admin UI section + CLI + docs (`docs/features/scm.md`).

## 7. Deliverables

GitHub App instalável por org; PR criado pelo agente/API; menções
`@openhandsnet` em issues/PRs iniciam conversas; status commit reporta
run; base `IScmProvider` pronta p/ SPEC-53.

## 8. Referências

- `migration-plan/research/competitive-analysis-devin-openhands-cloud.md` §6.1.
- docs.devin.ai `/integrations/gh`, `/integrations/pr-templates`;
  docs.openhands.dev `cloud/github-installation` (conceitos, Polyform).
- `.specs/12_git_scm_integration_spec.md`, `.specs/47_automations_spec.md`.

## 9. Guardrails

- Conceitos apenas do upstream enterprise (Polyform) — implementação própria.
- `Octokit` fixo (≥7 dias publicado); sem `latest`.
- Nenhum token/PEM em logs nem serializado — seguir `SecretRedactor`.
- Webhook nunca executa código do payload; só extrai contexto p/ start-task.

## 10. DoD

- Manifest flow cria app e grava connection (teste com mocks Octokit).
- Webhook com assinatura inválida → 401; replay → 200 idempotente.
- Menção em issue_comment cria start-task com `selected_repository` e
  contexto correto.
- `pull-request` endpoint abre PR real (integração mock) e grava evento.
- Migration ambos providers; suite verde; golden OpenAPI atualizado.
