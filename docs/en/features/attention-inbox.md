# Attention Inbox (SPEC-44)

Inbox of conversations that need you — sidebar badge with count and a
dedicated filter.

## Producers (kinds)

| Kind | Source |
|---|---|
| `confirmation` | run waiting for risky-tool approval (SPEC-29) |
| `question` | `ask_user` tool — the agent asked a question and paused |
| `failed` | run ended in `Error`/`Stuck` |

Persisted projection on `ConversationRecord` (`NeedsAttentionKind` +
excerpt + index) — written in the runner's `SetStatusAsync` funnel.

## `ask_user`

Builtin tool (`Risky:false`, ungated): the runner intercepts the call
and enters `waiting_for_question` — the next user message in the
composer is the answer (no approval banner — the question renders on
the tool card).

## API and push

- `GET /api/v1/attention` — the user's open items.
- `GET /app-conversations?attention=1` — list filter.
- `POST /app-conversations/{id}/attention/dismiss` — `confirmation` →
  `409` (can't dismiss without deciding); others clear the flag.
- Hub: `AttentionChanges` stream — payload-free tick (`IAttentionBus`)
  that invalidates the client fetch (zero content leak in the push).

## UI

Badge with count, per-conversation dot in the sidebar, toast on new
items, deep-link into the conversation.
