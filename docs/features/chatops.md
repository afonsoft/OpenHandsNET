# ChatOps (Slack) — SPEC-54

ChatOps conecta workspaces Slack ao OpenHandsNET: menções ao bot, slash
commands e replies em thread viram **app-conversations** (start-task
assíncrono) ou mensagens em conversas existentes, com postback do
resultado da run no próprio thread.

## Setup do Slack app

1. Crie um app em <https://api.slack.com/apps> com:
   - **Bot token** (`xoxb-…`) e **Signing secret**.
   - Scopes: `app_mentions:read`, `chat:write`, `channels:history`,
     `groups:history`, `im:history`, `reactions:write`,
     `assistant:write` (opcional, para status no thread).
   - Event subscriptions: `app_mention`, `message.channels` (e
     `message.im` para DMs).
   - Slash command (ex.: `/oh`) e interatividade habilitados.
2. Salve `bot_token` e `signing_secret` no **Secrets** da org.
3. Registre a conexão (admin):

   ```http
   POST /api/v1/orgs/{org_id}/chat-connections
   {
     "kind": "slack",
     "team_id": "T0123ABC",
     "bot_token_secret_ref": "slack-bot-token",
     "signing_secret_ref": "slack-signing-secret"
   }
   ```

4. Aponte as Request URLs do app Slack para:

   | Finalidade | Endpoint |
   |---|---|
   | Events API | `POST /api/v1/webhooks/chat/slack/events` |
   | Slash commands | `POST /api/v1/webhooks/chat/slack/commands` |
   | Interactions | `POST /api/v1/webhooks/chat/slack/interactions` |

5. Diagnóstico: `POST /api/v1/chat/connections/{id}/test`
   (chama `auth.test` no Slack com o bot token).

## Como funciona

- **@menção ou slash command** → cria uma app-conversation
  (`selected_repository` do mapeamento canal→repo, se houver), grava um
  `chat_thread_bindings` (connection + channel + thread_ts →
  conversation) e reage com 👀 na mensagem.
- **Reply no thread** → appended como `MessageEvent` (source=user) na
  conversa bound e notifica o runner — equivale a `send-message`.
- **Fim de run** → o `ChatOpsReporter` posta o resumo (redacted) no
  thread, troca a reação para ✅/❌ e atualiza o status do assistente.
- **`url_verification`** do Slack é respondido com o `challenge`;
  assinatura `v0=` (HMAC-SHA256 sobre `v0:ts:body`, janela de 5 min) é
  verificada em tempo constante; `event_id` dedup em
  `webhook_deliveries` evita reprocessamento de retries.

## Mapeamento canal→repo e auto-triage

```http
POST /api/v1/chat/channels/{connection_id}/config
{
  "channel_id": "C0123",
  "selected_repository": "org/repo",
  "selected_branch": null,
  "auto_triage": true
}
```

Com `auto_triage`, **toda** mensagem do canal (não só menções) inicia uma
sessão de investigação — útil para canais de alertas.

## CLI e admin

- `openhandsnet chat status` — tabela das conexões da org.
- `openhandsnet chat test --connection <id>` — diagnóstico `auth.test`.
- `/admin/chat` — listar/testar/remover conexões por org.

## Limitações (V1)

- Somente `kind=slack`; o contrato (`IChatSink`, `IChatOpsService`)
  já é genérico para futuros providers (Teams/Discord).
- Postback usa `chat.postMessage` + `reactions.*` — Block Kit rico fica
  para uma spec futura.
