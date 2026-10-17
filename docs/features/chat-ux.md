# Chat UX (SPEC-41)

Experiência de chat em tempo real sobre o EventStream V1. English: [en/features/chat-ux.md](../en/features/chat-ux.md).

## Streaming

- `streaming_delta` (SPEC-34) flui via SignalR sem tocar o event store —
  o texto do assistente digita em tempo real; o evento durável
  (`MessageEvent`) consolida o turno.
- `DeltaBatcher` server-side coalesce chunks (16ms/64B) — reduz frames
  sem latência perceptível.

## Reasoning

- `ThinkingSection` renderiza `reasoning`/thinking blocks do modelo
  (quando o provider expõe) colapsáveis sob o ToolCallCard.
- `DeltaFrame.channel` separa `answer` vs `thinking` no stream.

## Lifecycle

- Status da conversa no header: `STARTING → RUNNING →
  WAITING_FOR_CONFIRMATION / WAITING_FOR_QUESTION → PAUSED →
  FINISHED/ERROR`.
- `ConfirmationBanner` aparece quando `pending_action` é objeto
  `{action_id, tool}` — aprovar/rejeitar destrava o tool gate.
- `WAITING_FOR_QUESTION` (tool `ask_user`, SPEC-44): sem banner — a
  próxima mensagem do composer é a resposta.

## Heartbeat / reconexão

- `ConversationHubClient` mantém heartbeat + watchdog; queda de
  conexão reconecta com replay do stream a partir de `lastEventId`
  (V1 ids ordenáveis) — sem perda de eventos.

## Ações do usuário

- **Copy** por bloco de código e por mensagem.
- **Pause/Resume**: pause cancela o `ct` do run (transição via
  `OperationCanceledException`); resume recria o run com o fingerprint
  `agent_contract` (SPEC-33).
