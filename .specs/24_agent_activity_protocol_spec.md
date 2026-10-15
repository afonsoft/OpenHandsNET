# SPEC — 24 · Sandbox-Agent Activity Protocol & Run Observability

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `agent-activity-protocol` |
| Type | `Feature / Agent Runtime + UI` |
| Stack | `.NET 10 · Minimal API (sandbox agent) · SignalR · Blazor WASM` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Done` — PR #59 merged |
| Ticket | [#53](https://github.com/afonsoft/OpenHandsNET/issues/53) |
| Depends on | `03_runtime_sandbox_spec`, `17_runtime_image_blueprint_spec` (sandbox agent), `02_event_stream_spec` |
| Feeds | `26_ui_ux_modernization_spec` (renderers) |
| Touches | `OpenHandsNET.SandboxAgent/Program.cs`, `ISandboxExecutor`, `DockerSandboxExecutor`, `BuiltinTools`, `AgentRunner`, `TerminalTab`, `EventCard` |

## 1. User Story

**As a** usuário acompanhando uma run
**I want** ver o trabalho acontecendo — comandos rodando ao vivo, saída de
terminal em tempo real, arquivos mudando, progresso de ferramentas longas, e
um resumo humano ao final
**So that** a conversa deixa de ser "texto + spinner" e vira o feed de
atividade do agente (paridade Devin Web / oh-my-pi `onUpdate`).

**Problem context:**

- `openhandsnet-sandbox-agent` é um shim fino: `POST /exec` retorna só no
  fim (exit code + stdout completo). Um `dotnet test` de 5 minutos é um
  bloco opaco — sem saída incremental, sem heartbeat.
- O runner emite `ActionEvent`/`ObservationEvent` discretos; nada entre
  início e fim de uma tool call.
- `TokenEvent` cobre custo; não há eventos de progresso, arquivos tocados
  ou resumo final.
- `TerminalTab` existe mas alimenta-se da observação final, não de stream.

## 2. Scope (RF)

### RF-001 — Sandbox agent v2: exec streaming + processos

`OpenHandsNET.SandboxAgent/Program.cs` (novos endpoints, todos guardados
por `X-Session-Key` + confinamento existente):

| Method | Path | Body/Resp |
|---|---|---|
| POST | `/exec/stream` | `ExecRequest` → **SSE**: `data:{channel:"stdout"\|"stderr",text}` contínuo; `data:{type:"exit",exit_code,duration_ms,timed_out}` terminal. Mesmo `session_id`/`workdir`/`timeout` do `/exec`. |
| GET | `/exec/sessions` | lista de sessões bash vivas `{id, started_at, last_command?}` |
| GET | `/proc` | processos do workspace (`pid, cmd, cpu?, rss_kb?`) — best-effort via `/proc` |
| GET | `/activity` | ring buffer (últimos 200) das atividades do agente: execs, writes — **replay pós-reconnect** |

`ExecOnce`/`BashSession` ganham bomba de chunk → `Channel<OutputChunk>` em
vez de `StringBuilder` puro (cap de saída de 2MB **por canal** mantido — o
stream sinaliza `truncated:true` quando o cap estoura).

`POST /exec` continua existindo (compat) — implementado sobre o mesmo pump
agregando chunks.

### RF-002 — Host: `ISandboxExecutor` streaming

`ISandboxExecutor.ExecStreamingAsync(sandbox, command, timeout)` →
`IAsyncEnumerable<CommandOutputChunk>` + `CommandResult` terminal via
`CommandOutputChunk.Completed`-like record:

```csharp
public abstract record ExecChunk;
public sealed record OutputChunk(string Channel, string Text) : ExecChunk;
public sealed record ExecCompleted(CommandResult Result, long DurationMs,
    bool Truncated) : ExecChunk;
```

`DockerSandboxExecutor` consome o SSE do agente. Fallback: agente antigo
(sem `/exec/stream`, 404) → `ExecAsync` legado + um chunk único (feature
detect por versão do agente em `/alive`: `agent_version` ≥ `2`).

### RF-003 — Novos V1 event kinds (activity)

Todos `EventBase` (`id`, `kind`, `source`, `parent_id`), novos kinds:

- `command_output` — `{tool_call_id, action_id, channel:"stdout"|"stderr",
  text}` — chunks debounced pelo host (flush a 250ms/32KB, o que chegar
  primeiro). `source=agent`.
- `tool_progress` — `{tool_call_id, action_id, phase?, message,
  percent?, counters?}` — emitido por tools longas (bash envia linha de
  progresso, browser envia "navigating…", task_tracker já cobre planos).
- `files_changed` — `{tool_call_id?, paths:[{path,op:"create"|"modify"|
  "delete"}], source_tool}` — emitido após `file_write`/`file_edit`/após
  `bash` (diff rápido `git status --porcelain` quando o cwd é repo; cap 50
  paths).
- `run_progress` — `{iteration, max_iterations, phase:"thinking"|
  "tool_call"|"waiting_confirmation"|"finishing", elapsed_ms,
  tools_used:int, files_touched:int}` — um por iteração (barato; substitui
  o anterior na UI).
- `work_summary` — `{summary, files_changed:[…], commands_run:int,
  tests:{passed?,failed?}?, duration_ms}` — emitido pelo runner no
  `finish`: um LLM call barato (role `Summarizer`, SPEC-23) sobre as
  actions/observations da run + fallback determinístico (lista de
  arquivos+comandos) quando o summarizer falha.
- `agent_diagnostic` — `{severity:"info"|"warn"|"error", code, message}` —
  retries de LLM, fallback de modelo, agente desatualizado, output
  truncado. (Logs operacionais continuam Serilog; isto é o log **da run**
  visível ao usuário.)

Redação: todo `text`/`paths`/`summary` passa por `SecretRedactor.Redact`.

### RF-004 — Wiring no runner/tools

- `BuiltinTools.Bash`: usa `ExecStreamingAsync`; por chunk →
  `command_output` (debounce no `ToolCallContext.EventSink` — helper novo
  `ctx.EmitAsync(EventBase)`).
- `BuiltinTools.FileWrite/FileEdit`: após sucesso → `files_changed`.
- `bash` pós-run em repo: `git status --porcelain` (timeout 5s, falha
  silenciosa) → `files_changed` com paths reais.
- `AgentRunner`: `run_progress` por iteração; `agent_diagnostic` em retry/
  fallback (SPEC-23) e truncation; `work_summary` no finish (e em
  pause/interrupt: resumo parcial `phase:"interrupted"`).
- Buffer de replay: `IEventService` já persiste — replay de SSE vem de graça
  (a UI reconstrói o terminal paginando `command_output`).

### RF-005 — UI (mínimo viável nesta SPEC; polish na 26)

- `TerminalTab`: subscribe `command_output` por `tool_call_id` → xterm
  append incremental; agentes sem stream → comportamento atual.
- `EventCard`: `command_output` colapsa num bloco "terminal output" por
  action; `tool_progress` = barra/linha de status dentro do card da action;
  `files_changed` = chips `+path`/`~path`; `work_summary` = card verde de
  conclusão; `agent_diagnostic` = linha âmbar discreta.
- Header da conversa: `run_progress` → "iteração 12 · editando
  src/X.cs · 4m12s".

### RF-006 — Heartbeat/health do agente in-container

`GET /alive` passa a retornar `{status, state, agent_version, uptime_s,
workspace, sessions}` — `DockerSandboxService` expõe `agent_version` no
`/health/sandboxes` (doctor ganha check `agent_protocol`).

## 3. API Contract

Sandbox agent (interno, porta 8000, auth `X-Session-Key`): ver RF-001/006.
Backend: nenhum endpoint REST novo nesta SPEC — eventos fluem pelo canal
existente (`GET …/events` + SignalR `events` hub). Replayable: todos os
kinds novos persistem no event store.

Golden fixtures: `command_output`, `tool_progress`, `files_changed`,
`run_progress`, `work_summary`, `agent_diagnostic` — 1 fixture each +
`ExecStream` contract test do agente (WAF in-memory, sem Docker).

## 4. Data model

Sem migration — event store apenas. `Sandbox.AgentVersion` (string,
nullable) adicionada via migration para cache da versão detectada no init.

## 5. NFR / Security

- Stream SSE com bound: `command_output` máx ~4KB/evento pós-debounce;
  cap 2MB/canal por exec mantido (com `truncated:true`).
- Chunks passam por `SecretRedactor` **antes** de persistir (regexes
  existentes).
- `/proc` e `/activity` não expõem env vars.
- Debounce é lossy **só em coalescência** — nunca perde conteúdo (concatena).
- SSE reconnect: `Last-Event-ID` opcional → reemite do ring `/activity`.

## 6. Task Plan

1. Sandbox agent: pump de chunks, `/exec/stream`, `/alive` v2, `/proc`,
   `/activity` ring + testes WAF.
2. `ISandboxExecutor.ExecStreamingAsync` + `DockerSandboxExecutor` SSE +
   fallback por `agent_version` + testes.
3. `ToolCallContext.EmitAsync` + debouncer de `command_output`.
4. Novos records de evento (Domain/Events) + serializer kinds + fixtures.
5. Runner/tools: emissão (bash streaming, files_changed, run_progress,
   diagnostics, work_summary com fallback determinístico).
6. UI: TerminalTab streaming, EventCard renderers mínimos, header progress.
7. Doctor check + `Sandbox.AgentVersion` migration + doc
   `docs/agent/activity-protocol.md`.
8. Testes: e2e streaming com MockLlm; replay de `command_output`;
   work_summary fallback sem LLM.

## 7. Deliverables

- Sandbox agent v2 (`/exec/stream`, `/proc`, `/activity`, `/alive` v2).
- Executor streaming + events + runner wiring + UI mínima + docs.

## 8. Referências

- oh-my-pi `onUpdate` partial results + `renderCall`/`renderResult` —
  https://github.com/can1357/oh-my-pi (conceitos apenas).
- OpenHands upstream `CmdOutputObservation` streaming (conceitos apenas).
- Devin Web activity feed (conceito de UX).
- `src/OpenHandsNET.SandboxAgent/Program.cs` (shim atual).

## 9. Guardrails

- V1 only; kinds novos, nunca mutar payload de kinds existentes
  (campos opcionais ok).
- Compat: agente v1 (imagem antiga) continua funcionando via fallback.
- Sem secrets em chunks/summaries; caps de saída preservados.

## 10. DoD

- `bash` longo produz `command_output` incremental visível no TerminalTab.
- `finish` emite `work_summary` com arquivos e comandos (com e sem LLM).
- Reconnect do browser replay terminal + progresso corretamente.
- Agente v1 no sandbox antigo → fallback silencioso, run funciona.
- Fixtures verdes; `dotnet test` verde; imagem do sandbox-agent builda.
