# SCM — GitHub App, webhooks e pull requests (SPEC-52)

Integração de Source Control Management: um **GitHub App** por org permite
webhooks (`@openhandsnet` → conversa nova) e abrir PRs a partir do workspace
da conversa, com commit status `openhandsnet/run` ao fim do run.

## Contrato

| Endpoint | Auth | Descrição |
|---|---|---|
| `POST /api/v1/scm/github/manifest` | usuário | Devolve o manifest JSON do GitHub App (nome, permissões mínimas, eventos) |
| `GET /api/v1/scm/github/callback?code&org_id` | `perm:manage_integrations` | Troca o code do manifest flow por credenciais; grava PEM/webhook secret/client secret no secret store (por-referência) |
| `POST /api/v1/webhooks/scm/github` | público (HMAC) | Webhook do GitHub: `X-Hub-Signature-256` verificado, `X-GitHub-Delivery` idempotente (`webhook_deliveries`, chave única por kind+delivery) |
| `GET/POST/DELETE /api/v1/orgs/{org}/scm-connections` | `perm:manage_integrations` | CRUD das conexões (org-scoped) |
| `POST /api/v1/app-conversations/{id}/pull-request` | usuário | Push do work branch + PR no provider; `{title,body,base?,head?,draft?}` → `{url,number}` |

Permissões mínimas do app: `contents`, `pull_requests`, `issues`,
`statuses` (rw) + `metadata` (r). Eventos: `issue_comment`,
`pull_request`, `pull_request_review`, `pull_request_review_comment`.

## Fluxo de conexão (manifest)

1. `openhandsnet scm connect --server-url <url>` imprime o manifest.
2. Crie o app em `github.com/settings/apps/new` (método manifest).
3. O GitHub redireciona para `/api/v1/scm/github/callback?code=…` — a API
   troca o code em `api.github.com/app-manifests/{code}/conversions` e grava
   `scm_github_pem_{slug}` / `scm_github_hook_{slug}` /
   `scm_github_client_{slug}` no secret store da org (nunca em DB/log).

## Webhook → start-task

`POST /api/v1/webhooks/scm/github`:

1. Payload ≤ 256 KB; assinatura `sha256=` verificada em tempo constante
   contra `webhook_secret` (assim errada → 401 + audit `scm.webhook_rejected`).
2. Idempotência por `X-GitHub-Delivery` (replayed → `{status:"duplicate"}`).
3. Menções `@openhandsnet <instrução>` em `issue_comment`,
   `pull_request_review_comment` e `pull_request_review` criam uma
   conversa via `CreateForUserAsync` com `selected_repository` e prompt de
   contexto (repo, PR, branch, autor) + audit `scm.webhook_triggered`.

## Pull request do workspace

`POST /api/v1/app-conversations/{id}/pull-request` ou a tool do agente
`create_pull_request`: faz `git push` do work branch (`force-with-lease`
disponível via API), cria o PR no provider, persiste
`pull_request_{url,number,state,head_sha}` na conversa e emite o evento
`pull_request_created` no stream. Recriação é bloqueada (409) — a conversa
guarda o PR.

## Commit status

Ao fim do run (`finished`), `IScmStatusReporter` publica
`openhandsnet/run` = `success|failure` no `pull_request_head_sha`,
com `target_url` apontando para a conversa. Melhor esforço — nunca falha
o run.

## CLI

```bash
openhandsnet scm status --org <id>      # conexões da org
openhandsnet scm connect                # manifest do GitHub App
openhandsnet scm pr create <conv> --title "fix" --body "…"
```

## Storage

- `scm_connections` (org-scoped, `IOrgScoped`): kind, installation_id,
  secret refs, repos (JSON), app_id/slug.
- `webhook_deliveries` (`{kind, delivery_id}` único): replay protection.
- `conversations.pull_request_*`: vínculo conversa ↔ PR.
- Token de instalação: `IDistributedCache` chave
  `scm:gh:install-token:{installationId}`, TTL 50 min.

SPEC-53 estende o registro com GitLab/Bitbucket/Azure DevOps.
