# SPEC — 41 · Stream/Chat UX (Reasoning, Watchdog, Tool Cards, Copy/Resume)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `stream-chat-ux` |
| Type | `Feature / Client + Agent` |
| Stack | `Blazor WASM · SignalR · AgentRunner · Semantic Kernel` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Draft` |
| Ticket | `TBD` |
| Depends on | `02` (events), `09` (agent loop), `24` (activity protocol), `26` (ToolCallCard base), `34` (streaming_delta) |
| Reference | `afonsoft/open-webui` (MIT — port permitido): `ChatStreamService`, `ThinkingSection.razor`, `ToolCallCard.razor`, `ChatView` resume/watchdog |
| Touches | `src/OpenHandsNET.Infrastructure/Agent` (provider delta pipeline), `src/OpenHandsNET.Application/Events` (DeltaFrame), `src/OpenHandsNET.Api` (endpoints + hub), `src/OpenHandsNET.Client` (ChatPane, EventCard, novos componentes) |

## 1. User Story

**As a** usuário acompanhando uma run do agente
**I want** ver o raciocínio do modelo ao vivo numa seção colapsável,
tool calls como cards ricos com duração/status, feedback quando o stream
trava, e poder copiar mensagens e retomar runs pausadas
**So that** o chat nunca parece congelado e eu entendo o que o agente está
fazendo — hoje: reasoning é descartado no pipeline, tool call mostra args
cru, run pausada não tem resume, e um stream parado não dá nenhum sinal.

**Contexto:** o Domain já tem os campos — `StreamingDeltaEvent.ReasoningContent`
e `ActionEvent.ThinkingBlocks` — mas **nada os popula** (grep:
zero produtores em `Infrastructure/Agent`). O fork resolveu a mesma frente
em `SPEC-20261015-chat-thinking-stream` (entregue): normalização de
reasoning por provider + `ThinkingSection` colapsável + fases de progresso.
Divergência de transporte: o fork usa SSE com heartbeat `: hb`; nós usamos
SignalR + `DeltaFrame` efêmero — a SPEC adapta, **não** troca o transporte.

## 2. Scope (RF)

### RF-001 — Reasoning no pipeline do agente

- O streaming do LLM (`IAgentLlmProvider`, SPEC-09/23) passa a extrair
  reasoning: OpenAI-compat `delta.reasoning_content`/`reasoning`/
  `reasoning_details[]` (OmniRoute gateway, SPEC-23), Anthropic
  `thinking_delta`, Gemini `thought:true` parts — normalizado para um
  canal único `reasoning`.
- `DeltaFrame` ganha `channel` (`"content" | "reasoning"`); o batcher
  (SPEC-34) separa os dois canais (ou frame carrega o campo — menor diff).
- Ao fechar a action/turn, o texto de reasoning acumulado vira
  `ActionEvent.ThinkingBlocks` (já serializado no event store — verificar
  em T1) para replay no histórico.
- Redaction/outlet rules do SPEC-33 aplicam a reasoning igual a content.
- Reasoning **nunca** é concatenado ao `content` persistido.

### RF-002 — `ThinkingSection.razor`

- Seção dentro do turn do assistente: header `icon + label + elapsed`
  (live enquanto aberta); corpo muted/mono, `max-height` + scroll interno,
  auto-scroll ao fim enquanto expandida.
- **Auto-expande** enquanto reasoning chega; **auto-colapsa** no primeiro
  delta de conteúdo; toggle do usuário sobrescreve o auto no resto da run.
- Sem reasoning → nada renderiza. `run.failed` mid-reasoning → mantém o
  parcial com affordance de erro. Histórico renderiza `ThinkingBlocks`
  persistidos colapsados.
- Cap client ~64KB exibidos (elide head com "…").

### RF-003 — ToolCallCard rico

- `EventCard` case `ActionEvent` evolui para card dedicado: header
  `icon-por-tool + tool_name + elapsed/duração + status dot`
  (running/done/error); args pretty-printed colapsável com preview de 1
  linha (port `ToolArgsPreview`/`ToolActivityMap` — resumo por tool:
  `file_read /etc/…`, `exec "dotnet test"`…); link/âncora para o
  `ObservationEvent` correspondente (`parent_id`).
- `think` tool renderiza como thinking inline (não card genérico).

### RF-004 — Heartbeat + stall watchdog

- Runner emite `run_progress {phase:"heartbeat", elapsed_ms}` a cada 15s
  durante run ativa (barato; também alimenta elapsed na UI).
- Client: watchdog `StallMs = 45s` sem **nenhum** evento/delta em run
  `Running` → indicador "conexão lenta — reconectando" no header do chat;
  limpa ao primeiro evento. `WithAutomaticReconnect` já cobre drop real;
  watchdog cobre stream vivo-mas-mudo.
- `Pause` já existe no hub; UI ganha botão Pause durante run ativa.

### RF-005 — Resume de run pausada

- `POST /api/v1/app-conversations/{id}/resume` + método `Resume` no
  `ConversationHub` (par de `Pause`/`Interrupt`): retoma run `Paused`
  → `Running` (`ConversationExecutionStatus.Paused` já existe).
- UI: banner/botão "Resume" quando `ExecutionStatus == Paused` ou último
  `PauseEvent` sem resume; `PauseEvent`/`InterruptEvent` renderizam como
  cards de lifecycle discretos.

### RF-006 — Copy affordances

- Botão copy em bolhas de `MessageEvent` (user/agent) e em blocos de
  código do markdown (`ohn.copyText` JS + feedback "copiado").
- Ícone `copy` já existe em `Icons.cs` — falta uso.

### RF-007 — i18n-ready + a11y

- Strings novas isoladas (const keys) para sweep da SPEC-46; header da
  ThinkingSection `aria-live="polite"` só em open/close (não por token);
  toggle com `aria-expanded`.

## 3. API Contract

```text
POST /api/v1/app-conversations/{id}/resume   → 202 {execution_status} | 409 (not paused)
hub:  Resume()                               → retoma run (broadcast state update)
deltas: {item_id, seq, channel, text}        → channel="reasoning"|"content" (aditivo)
events: run_progress {phase:"heartbeat", elapsed_ms}  (aditivo — clients antigos ignoram)
```

Sem breaking change: `channel` default `"content"` quando ausente.

## 4. Data model

Sem migration. `ActionEvent.ThinkingBlocks` já existe no Domain
(verificar serialização no event store — T1). DeltaFrame é efêmero.

## 5. NFR / Security

- Reasoning passa pelo mesmo outlet-rule/redaction de content (SPEC-33).
- Watchdog/heartbeat nunca derruba run — telemetria best-effort.
- Buffer client capado (64KB reasoning + deltas) — sem memory leak em runs longas.

## 6. Task Plan

1. T1 discovery: serialização de `ThinkingBlocks` no event store; campos
   de reasoning que o gateway OmniRoute emite (curl stream) — trava lista RF-001.
2. Provider/SK streaming → canal reasoning + `DeltaFrame.channel` + testes.
3. `ActionEvent.ThinkingBlocks` populate + testes de acumulação.
4. `ThinkingSection` + wiring no ChatPane (auto-collapse, cap, a11y) + bUnit.
5. ToolCallCard rico (preview args, elapsed, link observation) + bUnit.
6. Heartbeat runner + watchdog client + Pause/Resume endpoint+hub+UI + testes.
7. Copy buttons + sweep final + `dotnet test` verde.

## 7. Deliverables

`ThinkingSection.razor`, `ToolCallCard.razor` (ou `EventCard` enriquecido),
`Resume` endpoint+hub, heartbeat `run_progress`, `DeltaFrame.channel`,
reasoning pipeline no `AgentRunner`, watchdog no `ConversationStateService`,
copy JS, testes unit+client.

## 8. Referências

- `afonsoft/open-webui` `docs/specs/SPEC-20261015-chat-thinking-stream.md`
  (modelo desta SPEC), `src/OpenWebUI.Client/Components/ThinkingSection.razor`,
  `ToolCallCard.razor`, `Services/{ToolCallView,ToolActivityMap,ToolArgsPreview}.cs`,
  `ChatView.razor` (watchdog/resume) — **MIT, port permitido**.
- `.specs/34_streaming_deltas_artifacts_spec.md` (DeltaFrame/batcher),
  `.specs/26` (EventCard base), `.specs/24` (kinds).

## 9. Guardrails

- SignalR permanece — **não** adotar SSE do fork (rework sem ganho).
- Reasoning nunca entra em `content` persistido; sem migration.
- Mudanças de contrato só aditivas (`channel`, `phase` novos).
- `.github/workflows/` intocado; `main` só via PR.

## 10. DoD

- Modelo com `reasoning_content` (via OmniRoute) streama "Pensamento"
  ao vivo; auto-colapsa na resposta; re-expansível; histórico colapsado.
- Tool call exibe card rico (nome, args preview, duração, link observation).
- Stream mudo >45s em run ativa mostra indicador; heartbeat visível em
  `run_progress` (debug).
- Run pausada → botão Resume → continua; copy funciona em msg e código.
- `dotnet test` verde; bUnit cobre collapse/watchdog/cards.
