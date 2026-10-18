# Ticket Triggers (Linear / Jira)

SPEC-55 — dispara sessões OpenHandsNET a partir de eventos de ticket e
escreve o resultado de volta no ticket (comentário + transição de estado).

## Conceitos

- **TicketConnection** (`POST /api/v1/orgs/{org}/ticket-connections`) —
  credenciais de uma instância Linear ou Jira por org:
  - `kind`: `linear` | `jira`
  - `api_url`: Jira exige o `https://acme.atlassian.net`; Linear ignora
    (usa `https://api.linear.app/graphql`).
  - `token_ref` / `webhook_secret_ref`: **refs ao secret store** — tokens
    e segredos nunca trafegam em claro.
  - `workspace_id`: filtro de roteamento — `organizationId` (Linear) ou
    host do `issue.self` (Jira).
  - `trigger_config`: `{mode, value, done_state, prompt_template,
    selected_repository}`:
    - `label` — dispara quando o issue ganha o label `value`
      (ex.: `openhands`).
    - `assignee` — dispara quando `value` vira assignee.
    - `state` — dispara quando o status vira `value`.
    - Comentário `@openhandsnet` dispara **sempre** (mention bypass).
    - `prompt_template` opcional com placeholders `{identifier}`,
      `{title}`, `{description}`, `{labels}`, `{url}`, `{comment}`.
- **Project → repo** (`POST …/ticket-connections/{id}/projects`) —
  mapeia `project_key` → `repository` + `default_branch`; sobrescreve o
  `selected_repository` do trigger.

## Webhooks públicos

| Endpoint | Assinatura | Eventos |
|---|---|---|
| `POST /api/v1/webhooks/tickets/linear` | `Linear-Signature` = hex HMAC-SHA256(webhook secret, body) | `Issue` update; `Comment` create |
| `POST /api/v1/webhooks/tickets/jira` | `?secret=` ou header `X-OpenHandsNet-Secret` (fixed-time compare) | `jira:issue_updated`; `comment_created` |

Payload máximo 256 KB. Replays são deduplicados via `webhook_deliveries`
(kind `ticket`) — resposta idempotente.

## Write-back

Quando uma conversa disparada por ticket termina, o `TicketReporter`
comenta no issue:

```
OpenHandsNET: run finished — {resumo redigido}.
PR: {pr_url} Conversation: {conv_id}
```

e (quando `done_state` configurado) transiciona o issue. Falha →
comentário de erro. Audit: `ticket.run_started` e `ticket.writeback`.

## Fluxo manual

Sem expor webhook público? Use o run autenticado:

```bash
openhandsnet tickets status --org <org-id>
openhandsnet tickets test --connection <id>
openhandsnet tickets run --connection <id> --issue ENG-42
```

## Admin UI

`/admin/tickets` — lista conexões por org, teste de conectividade
(chama `POST /tickets/connections/{id}/test`) e remoção.

## Comentário manual

`POST /api/v1/tickets/{linear|jira}/{issue_key}/comment` com
`{body, workspace_id}` comenta no ticket sem disparar run.

## Configuração no tracker

- **Linear**: Settings → API → Webhooks → URL
  `…/webhooks/tickets/linear`, eventos `Issue` + `Comment`, copie o
  signing secret para `webhook_secret_ref`.
- **Jira**: System → WebHooks → URL
  `…/webhooks/tickets/jira?secret=<segredo>` com events `issue updated`
  + `comment created`.
