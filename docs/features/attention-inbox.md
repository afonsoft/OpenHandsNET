# Attention Inbox (SPEC-44)

Inbox de conversas que precisam de você — badge na sidebar com contagem
e filtro dedicado.

## Producers (kinds)

| Kind | Origem |
|---|---|
| `confirmation` | run esperando aprovação de tool risky (SPEC-29) |
| `question` | tool `ask_user` — o agente fez uma pergunta e pausou |
| `failed` | run terminou em `Error`/`Stuck` |

Projeção persistida na `ConversationRecord` (`NeedsAttentionKind` +
excerpt + índice) — escrita no funil `SetStatusAsync` do runner.

## `ask_user`

Tool builtin (`Risky:false`, não gated): o runner intercepta a chamada
e entra em `waiting_for_question` — a próxima mensagem do usuário no
composer é a resposta (sem banner de aprovação — a pergunta aparece no
card da tool).

## API e push

- `GET /api/v1/attention` — itens abertos do usuário.
- `GET /app-conversations?attention=1` — filtro no list.
- `POST /app-conversations/{id}/attention/dismiss` — `confirmation` →
  `409` (não pode dispensar sem decidir); demais limpam o flag.
- Hub: stream `AttentionChanges` — tick sem payload (`IAttentionBus`)
  que invalida o fetch no client (zero vazamento de conteúdo no push).

## UI

Badge com count, dot por conversa na sidebar, toast em item novo,
deep-link para a conversa.
