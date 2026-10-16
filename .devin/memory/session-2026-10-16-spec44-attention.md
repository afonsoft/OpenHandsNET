# Session — SPEC-44 Attention Inbox (Issue #102)

## Entregue

- Projeção persistida `NeedsAttentionKind`/`NeedsAttentionAtTicks` em
  `ConversationRecord` + migrations nos DOIS contextos (SQLite
  `20261016194550`, Postgres `20261016194636`) + índice
  `(org_id, needs_attention_at_ticks)`.
- `AgentRunner.SetStatusAsync` = funil central de projeção:
  `waiting_for_confirmation`→confirmation, `waiting_for_question`→
  question, `error`/`stuck`→failed, demais limpam. Publica tick no
  `IAttentionBus` só quando o kind muda.
- `ask_user`: builtin tool (Risky=false — não é gated por confirmation
  mode); o runner intercepta ANTES de invocar o handler →
  `waiting_for_question` SEM `pending_action` (banner approve/reject
  não aparece) → próxima `MessageEvent` user em `_incoming` vira o
  ToolResult.
- **Bug latente corrigido**: server emitia `pending_action` como string,
  mas `ConversationStateService.PendingAction` só parseia objeto
  `{action_id, tool}` → ConfirmationBanner nunca aparecia. Agora emite
  o objeto.
- `IAttentionBus`/`AttentionBus` (padrão AgentActivityBus) + hub
  `AttentionChanges` (stream payload-free; refetch aplica scoping).
- Endpoints: `GET /attention`, `?attention=1` no `/search`,
  `POST /{id}/attention/dismiss` (204/409 confirmation/404).
- Service: `ListAttentionAsync` (não-arquivadas, excerpt =
  AgentErrorEvent p/ failed, ActionEvent question/command/path p/
  confirmation|question), `DismissAttentionAsync` (confirmation →
  Conflict).
- Client: `AttentionInboxState` (scoped, boot load, tick → debounce 2s
  → refetch, SEM poll), `AttentionBadge` (sino + count + breakdown no
  title), sidebar: badge no `.sb-head`, filtro "Aguardando você", dots
  `attention-{kind}` vencem status; toast opt-out via
  `localStorage:ohn.attention.toasts=0`.

## Lições

- `SetStatusAsync` cobre todos os caminhos de erro (context_overflow,
  exception, stuck) — projetar ali evita N call-sites.
- `stuck` também é falha (record=Error) — mapear junto com `error`.
- Record positional params não aceitam `///` — usar `<param>` no record
  exige tag para TODOS os params → preferir `//` inline.
- Scoping do inbox segue `SearchAsync` (não-arquivadas); OrgId é
  populado no create mas list queries não filtram por org — paridade
  documentada na SPEC.

## Verificação

- Build Release limpo; 635/635 testes (4 unit + 3 bUnit + 5 endpoint
  novos).
