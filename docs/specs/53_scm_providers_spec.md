# SPEC — 53 · SCM Providers: GitLab, Bitbucket, Azure DevOps

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `scm-providers` |
| Type | `Feature / Integration` |
| Stack | `.NET 10 · IScmProvider (SPEC-52) · REST clients tipados` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed` |
| Ticket | `TBD` |
| Depends on | `52` (IScmProvider, connections, webhook pipeline) |
| Reference | competitive-analysis §6.1; docs.devin.ai `/integrations/gitlab`,
  `/integrations/bitbucket`, `/enterprise/integrations/azure-devops`;
  docs.openhands.dev `cloud/{gitlab,bitbucket}-installation`,
  `enterprise/integrations/azure-devops` |
| Touches | `src/OpenHandsNET.{Application,Infrastructure,Api}`, `tests/` |

## 1. User Story

**As a** org self-hosted cujo código vive em GitLab, Bitbucket ou Azure
DevOps
**I want** as mesmas capacidades do SPEC-52 (MR/PR, comentários,
@menções, status) nos meus providers
**So that** o OpenHandsNET não fica preso ao GitHub — paridade com
OpenHands Enterprise (GitLab self-hosted, BBDC, AzDO) e Devin
(todos + self-managed). Reusa a interface e o pipeline de webhook da
SPEC-52; nada muda nos endpoints públicos além de `Kind` e URLs.

## 2. Scope (RF)

### RF-001 — GitLab provider

- `GitLabScmProvider : IScmProvider` via REST v4 tipado
  (`HttpClientFactory` + `Polly` retry já presente ou `System.Net.Http.Json`).
- Auth: PAT por connection (`PrivateTokenSecretRef`) ou OAuth app;
  `api_url` configurável (self-managed ≥15.0).
- MR: `POST /projects/:id/merge_requests`; comentários em
  `notes`/`discussions`; status via `POST /projects/:id/statuses`.
- Webhook `POST /api/v1/webhooks/scm/gitlab` — `X-Gitlab-Token` match +
  `X-Gitlab-Event`: `Note Hook` com `@openhandsnet` (issue/MR),
  `Merge Request Hook`.

### RF-002 — Bitbucket provider

- `BitbucketScmProvider`: Cloud (app password / workspace token) e
  Data Center (`base_url` configurável).
- PR: `POST /repositories/{ws}/{repo}/pullrequests`; comments
  `POST .../pullrequests/{id}/comments`; build status
  `POST .../commit/{sha}/statuses/build`.
- Webhook `POST /api/v1/webhooks/scm/bitbucket` — HMAC
  `X-Hub-Signature` + `X-Event-Key` (`pullrequest:comment_created`,
  `issue:comment_created`).

### RF-003 — Azure DevOps provider

- `AzureDevOpsScmProvider`: `base_url` por org
  (`https://dev.azure.com/{org}` ou server), auth PAT (`Basic`) ou
  Microsoft Entra service principal.
- PR: `POST .../git/repositories/{id}/pullrequests`; threads
  `POST .../pullRequests/{id}/threads`; status `POST .../statuses`.
- Webhook via Service Hooks (`POST /api/v1/webhooks/scm/azuredevops` —
  segredo em query/header configurável): `git.pullrequest.created`,
  `ms.vss-code.git-pullrequest-comment-event` com `@openhandsnet`.

### RF-004 — Integração uniforme

- `scm_connections.Kind` ∈ `{github, gitlab, bitbucket, azuredevops}`;
  campos novos `ApiUrl`, `AuthKind` (`pat|app|oauth|entra_sp`).
- `POST /api/v1/scm/connections/{id}/test` → valida token + escopos e
  devolve diagnóstico (usuário, versão do servidor, repos acessíveis).
- Roteamento do webhook por `Kind` na URL + lookup da connection.
- CLI `openhandsnet scm connect --kind gitlab --url ...`.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| POST | `/api/v1/webhooks/scm/{kind}` | kind ∈ gitlab/bitbucket/azuredevops |
| POST | `/api/v1/scm/connections/{id}/test` | diagnóstico |
| (herdados) | `…/scm-connections`, `…/pull-request`, `…/webhooks/scm/github` | SPEC-52 |

Erros: `400` · `401` assinatura/token · `404` · `422` provider
rejeitou (ex.: branch protegido) — corpo `{error, detail, provider}`.

## 4. Data model

`scm_connections` + `ApiUrl`, `AuthKind`, `MetaJson` (project keys,
workspace, collection). Migration leve (colunas nullable).

## 5. NFR / Security

- PATs/tokens sempre via `SecretNamesJson`; test endpoint não ecoa
  segredos.
- Verificação por provider: GitLab `X-Gitlab-Token`; Bitbucket HMAC;
  AzDO shared secret — todos com payload ≤256 KB + idempotência por
  delivery id.
- Self-hosted: `ApiUrl` validada (https; http só `AllowInsecureScm`
  explícito p/ labs internos — documentado).

## 6. Task Plan

1. `GitLabScmProvider` + webhook note-hook → start-task + test.
2. `BitbucketScmProvider` (cloud; DC via `api_url`).
3. `AzureDevOpsScmProvider` + service hooks.
4. `test` endpoint + diagnósticos + CLI/docs sweep.

## 7. Deliverables

3 providers novos plugados na mesma interface; MR/PR, comentários,
@menção→conversa e status funcionando em cada um; `test` diagnóstico.

## 8. Referências

- `docs/specs/52_github_app_scm_spec.md` (interface, entidades, pipeline).
- GitLab REST v4 docs; Bitbucket Cloud API 2.0; Azure DevOps REST 7.1.

## 9. Guardrails

- Sem SDKs pesados: `HttpClientFactory` tipado por provider; Polly só
  se já dependência — senão retry linear simples.
- Sem fan-out de permissões: cada connection é scoped à org.
- Payloads de webhook nunca confiáveis — extração estrita de campos.

## 10. DoD

- Para cada provider: unit tests do mapping + integração mock do
  webhook (@menção → start-task com repo/branch corretos).
- `test` endpoint reporta auth ok/fail com detalhe seguro.
- Migrations verdes; OpenAPI golden atualizado; docs
  `docs/features/scm.md` com tabela por provider.

## 11. Delivered (evidência)

- `RestScmProvider` base + `GitLabScmProvider` /
  `BitbucketScmProvider` / `AzureDevOpsScmProvider` em
  `src/OpenHandsNET.Infrastructure/Scm/RestScmProviders.cs` — MR/PR,
  comentários, replies, commit statuses e `TestConnectionAsync`
  por provider; token sempre por-referência via `ISecretStore`.
- `scm_connections` + `ApiUrl` / `AuthKind` / `MetaJson`
  (migration `spec53_scm_providers` SQLite + Postgres).
- Webhook roteado por kind em `POST /api/v1/webhooks/scm/{kind}`
  (`ScmEndpoints`): GitLab `X-Gitlab-Token` + `Note Hook`,
  Bitbucket `X-Hub-Signature` HMAC + `pullrequest:comment_created`,
  AzDO shared secret (header `X-OpenHandsNet-Secret` ou query
  `?secret=`) + `ms.vss-code.git-pullrequest-comment-event`;
  idempotência por delivery id compartilhada.
- `POST /api/v1/scm/connections/{id}/test` → diagnóstico
  (`ScmConnectionDiagnostic`: ok/user/server_version/repo_count/
  detail — sem ecoar segredos).
- CLI `openhandsnet scm connect --kind gitlab|bitbucket|azuredevops
  [--url]` imprime o payload de registro + instruções.
- Testes: `tests/OpenHandsNET.UnitTests/Scm/Spec53ScmProvidersTests.cs`
  — 8 verdes (token errado → 401, menção por kind → start-task,
  registry resolve os 4 kinds, `TestAsync` diag).
- Golden OpenAPI regenerado (novas rotas `{kind}` + `/test`).
