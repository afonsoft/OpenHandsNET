# SPEC — 54 · ChatOps: Slack App (mention, slash, thread sync)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `chatops-slack` |
| Type | `Feature / Integration` |
| Stack | `.NET 10 · Slack Web API + Events API · SignalR · EF Core` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Delivered` |
| Ticket | `TBD` |
| Depends on | `04` (API/SignalR), `47` (automations), `52` recomendado
  (postar PR de volta) |
| Reference | competitive-analysis §6.2 (G2); docs.devin.ai
  `/integrations/slack`, `/product-guides/auto-triage`;
  docs.openhands.dev `cloud/slack-installation` |
| Touches | `src/OpenHandsNET.{Application,Infrastructure,Api,Client}`, `tests/` |

## 1. User Story

**As a** membro de um workspace Slack
**I want** mencionar `@openhandsnet` ou usar `/openhandsnet` num canal
ou thread e receber a sessão do agente respondendo no thread
**So that** triagem de bug/feature acontece onde o time já conversa —
paridade com Slack app do OpenHands Cloud e @Devin no Slack. Hoje o
único caminho é a UI/API; não há bot, thread binding nem postback.

## 2. Scope (RF)

### RF-001 — Slack app credentials + endpoints de ingestão

- Entidade `chat_connections`: `Id, OrgId, Kind=slack, TeamId,
  BotTokenRef, SigningSecretRef, BotUserId, DefaultChannelId?`.
- `POST /api/v1/webhooks/chat/slack/events` — Events API:
  - `url_verification` challenge; verificação
    `X-Slack-Signature` v0 HMAC-SHA256 + `X-Slack-Request-Timestamp`
    ≤5 min; idempotência por `event_id`.
  - `app_mention` e `message.im` → cria/continua sessão.
- `POST /api/v1/webhooks/chat/slack/commands` — `/openhandsnet <prompt>`
  (e `/openhandsnet status|stop`).
- `POST /api/v1/webhooks/chat/slack/interactions` — botões
  (confirm/cancel de tool `ask` exposto no thread).

### RF-002 — Thread → sessão binding

- `chat_thread_bindings`: `(TeamId, ChannelId, ThreadTs)` ↔
  `ConversationId`; primeira menção num thread cria a conversa
  (`selected_repository` via mapeamento canal→repo — RF-004); replies
  seguintes viram `send-message`.
- Contexto do thread recente (últimas N mensagens, N=10) entra no
  prompt inicial — mesmo comportamento do @Devin.

### RF-003 — Postback e sync

- `IChatSink`: `PostMessage`, `UpdateMessage`, `AddReaction`,
  `SetStatus` — implementação Slack (`chat.postMessage`, `reactions.add`,
  `assistant.threads.setStatus` quando disponível).
- Eventos do stream mapeados: run iniciada → mensagem "🔄 Working…";
  `ask_user`/confirmação → bloco com botões; conclusão → resumo +
  link da conversa + link do PR (se SPEC-52 presente); falha →
  mensagem de erro com `support` link.
- Reação `👀` na menção = ack; `✅` ao concluir.
- Não ecoar segredos: passar texto de saída pelo `SecretRedactor`.

### RF-004 — Mapeamento canal→repo e permissões

- `chat_channel_configs`: `ChannelId → selected_repository,
  selected_branch?, AutomationId?`.
- `POST /api/v1/orgs/{org}/chat-connections` (admin) +
  `POST /api/v1/chat/channels/{id}/config`.
- Permissão: apenas canais configurados disparam runs (ou modo aberto
  por flag org `ChatOps:OpenChannels`); usuário Slack sem conta
  OpenHandsNET mapeada → resposta educada sem run.

### RF-005 — Auto-triage (modo canal)

- Flag `auto_triage=true` por canal: toda mensagem nova (não-bot) vira
  sessão com prompt de triagem (classifica bug/pergunta/feature e age
  ou responde) — equivalente ao auto-triage do Devin.

### RF-006 — Admin UI + CLI

- Seção `ChatOps` em settings: add workspace (bot token + signing
  secret), list bindings, config canal→repo.
- CLI: `openhandsnet chat status`, `chat test --channel`.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| POST | `/api/v1/webhooks/chat/slack/events` | público, sig verificada |
| POST | `/api/v1/webhooks/chat/slack/commands` | slash command |
| POST | `/api/v1/webhooks/chat/slack/interactions` | block_actions |
| GET/POST/DELETE | `/api/v1/orgs/{org}/chat-connections` | admin |
| POST | `/api/v1/chat/channels/{id}/config` | canal→repo |
| POST | `/api/v1/chat/connections/{id}/test` | `auth.test` + post efêmero |

Erros: `400` · `401` assinatura · `404` · `429` (retry com
`Retry-After` — Slack re-tenta 3×).

## 4. Data model

`chat_connections`, `chat_thread_bindings` (unique team+channel+ts),
`chat_channel_configs`. Migrations SQLite+Postgres.

## 5. NFR / Security

- Bot token + signing secret via `SecretNamesJson`.
- Verificação Slack obrigatória em todo POST; timestamps fora da
  janela → 401.
- Menções só disparam se `ChatOps:Enabled` e org tem connection
  válida; rate limit por `team+channel` (ex.: 20 runs/h default).
- Mensagens externas nunca executadas — só entram como contexto de
  prompt (prompt-injection: tratado pelo `IInputGuard` da SPEC-59
  quando presente).
- Audit: `chatops.run_started`, `chatops.thread_bound`.

## 6. Task Plan

1. Entidades + `IChatSink` Slack (post/update/react/status) + config.
2. Events endpoint (url_verification, sig, idempotência) + mention →
   start-task + thread binding.
3. Postback mapping (run events → mensagens/reactions/blocks).
4. Slash commands + interactions (confirm/cancel).
5. Canal→repo config + auto-triage flag.
6. Admin UI + CLI + docs (`docs/features/chatops.md`).

## 7. Deliverables

Slack app instalável por org: @menção e `/openhandsnet` iniciam
sessões; progresso/resultado no thread; bindings persistentes;
auto-triage por canal opt-in.

## 8. Referências

- `migration-plan/research/competitive-analysis-devin-openhands-cloud.md`.
- Slack API docs: events-api, slash commands, chat.postMessage,
  block kit.
- `.specs/47_automations_spec.md` (webhook triggers — reusar
  `AutomationService` como orquestrador quando couber).

## 9. Guardrails

- Escopo mínimo Slack scopes: `app_mentions:read`, `chat:write`,
  `commands`, `channels:history`, `reactions:write`,
  `im:history`, `im:write`.
- Socket Mode fica fora — HTTP webhooks only (self-host exige URL
  pública; documentar túnel dev).
- Teams fica para SPEC futura; abstrair `IChatSink` para permitir.

## 10. DoD

- `url_verification` responde challenge.
- Assinatura inválida → 401; `event_id` repetido → processado 1×.
- Menção em canal configurado cria conversa e posta status; reply no
  thread injeta `send-message`.
- Slash command responde em ≤3 s (ack) e encaminha run.
- Migrations verdes; testes unit do verifier + integração do binding.


## 11. Delivered (evidência)

- `Application/ChatOps/ChatOpsContracts.cs` — `ChatConnection`,
  `ChatChannelConfig`, `ChatOpsResult`, `IChatOpsService`,
  `IChatSink`, `IChatConnectionService`, `IChatOpsReporter`.
- `Infrastructure/ChatOps/` — `ChatConnectionService` (CRUD +
  channel configs), `SlackChatSink` (chat.postMessage/update/
  reactions.add/setStatus via Bearer), `SlackChatOpsService`
  (verify v0 ±300s fixed-time, url_verification challenge,
  event dedup em `webhook_deliveries`, mention/IM/auto-triage →
  start-task, thread binding → send-message), `ChatOpsReporter`
  (postback ✅/❌ + resumo redacted no thread).
- `Api/Endpoints/ChatOpsEndpoints.cs` — webhooks públicos
  `events|commands|interactions`, CRUD `/orgs/{org}/chat-connections`,
  `/chat/channels/{id}/config`, `/chat/connections/{id}/test`.
- EF `chat_connections`/`chat_thread_bindings`/`chat_channel_configs`
  (migrations SQLite + Postgres).
- AgentRunner: `IChatOpsReporter.ReportRunFinishedAsync` fire-and-forget.
- `/admin/chat` (AdminChatPage) + CLI `chat status|test` + i18n.
- Docs: `docs/features/chatops.md`.
- Testes: `Spec54ChatOpsTests` — 8/8 verdes (signature, challenge,
  mention→binding, dedup, thread reply→send, auto-triage, slash).
