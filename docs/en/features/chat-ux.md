# Chat UX (SPEC-41)

Real-time chat experience on top of the V1 EventStream. Português: [../../features/chat-ux.md](../../features/chat-ux.md).

## Streaming

- `streaming_delta` (SPEC-34) flows over SignalR without touching the
  event store — assistant text types out in real time; the durable
  `MessageEvent` consolidates the turn.
- Server-side `DeltaBatcher` coalesces chunks (16ms/64B) — fewer frames
  with no perceivable latency.

## Reasoning

- `ThinkingSection` renders model reasoning/thinking blocks (when the
  provider exposes them), collapsible under the ToolCallCard.
- `DeltaFrame.channel` separates `answer` vs `thinking` in the stream.

## Lifecycle

- Conversation status in the header: `STARTING → RUNNING →
  WAITING_FOR_CONFIRMATION / WAITING_FOR_QUESTION → PAUSED →
  FINISHED/ERROR`.
- `ConfirmationBanner` appears when `pending_action` is an object
  `{action_id, tool}` — approve/reject unblocks the tool gate.
- `WAITING_FOR_QUESTION` (`ask_user` tool, SPEC-44): no banner — the
  next composer message is the answer.

## Heartbeat / reconnect

- `ConversationHubClient` keeps a heartbeat + watchdog; on disconnect
  it reconnects and replays the stream from `lastEventId` (orderable V1
  ids) — no event loss.

## User actions

- **Copy** per code block and per message.
- **Pause/Resume**: pause cancels the run `ct` (transition via
  `OperationCanceledException`); resume restarts the run under the
  `agent_contract` fingerprint (SPEC-33).
