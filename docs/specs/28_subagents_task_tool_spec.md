# SPEC — 28 · Subagent Orchestration (`task` tool)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `subagents-task-tool` |
| Type | `Feature / Agent Core` |
| Stack | `.NET 10 · AgentRunner · ISandboxExecutor · SPEC-23 routing` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed` (PR #70) |
| Ticket | #61 |
| Depends on | `09` (agent engine), `22` (context), `23` (routing), `24` (activity), `25` (tools), `29` (permissoes) |
| Touches | `BuiltinTools`, `ToolRegistry`, `AgentRunner`, `IAgentRunnerHost`, `AgentLlmOptions` |

## 1. User Story

**As a** usuário
**I want** que o agente delegue exploração/pesquisa a subagentes com escopo
restrito de tools e contexto próprio
**So that** a janela principal não é poluída com dezenas de `file_read`/
`search` intermediários — o pai recebe só o resumo — e runs ficam mais
baratos (subagente pode rotear p/ modelo "fast" via SPEC-23).

Referência: "Batuta" do fork `alltomatos/opencode` (SPEC-27 RF-A) —
conceito apenas.

## 2. Scope (RF)

### RF-001 — Tool `task`

```json
{"description":"...", "prompt":"...", "subagent_type":"explore",
 "background":false}
```

`BuiltinTools.Task(IServiceScopeFactory)` — handler cria um run filho
**no mesmo sandbox** (executor compartilhado, mesma workspace):

| Preset | Tools permitidas |
|---|---|
| `explore` | `search`, `file_read`, `bash` (read-only vetado por padrão `deny` de mutação — ver SPEC-29), `think`, `finish` |
| `scout` | `web_fetch`, `search`, `file_read`, `think`, `finish` |
| `general` | tudo exceto `task`, `finish` do filho é o retorno |
| `plan` | somente read-only (`search`, `file_read`, `think`, `task_tracker`) |

`subagent_depth` (default 1): o filho não recebe a tool `task` —
evita recursão.

### RF-002 — Runner filho

`SubagentRunner` reusa `AgentRunner` internamente com:

- `IToolRegistry` filtrado por preset (novo `FilterToolRegistry`).
- `LLM` via `IModelRouter` role `Subagent` (fallback `Explore`,
  fallback `default`) — modelo "fast" quando configurado.
- Eventos do filho: mesmo event stream, mas **namespaced** —
  `parent_id = tool_call_id` do `task` + flag `subagent:true` num
  `ConversationStateUpdateEvent(key="subagent_run")` de abertura/fechamento.
  `run_progress`/`work_summary`/`files_changed` do filho carregam
  `parent_tool_call_id` (UI agrupa em collapse — SPEC-26).
- Resultado: `work_summary` do filho → `ToolResult` do `task`
  (cap 8KB via OutputPolicy) — **exploração não polui a janela**.

### RF-003 — Execução sync e background

- `background:false` (default): pai bloqueia até o filho terminar;
  observation = resumo + `subagent_run_id`.
- `background:true`: retorna imediato `{queued:true, task_id}`;
  filho roda em `Task.Run` com budget próprio; pai consulta via
  `task_status {task_id}` / `task_result {task_id}`.
  Cap: `MaxConcurrentSubagents = 3` por conversa.

### RF-004 — Budget e guardrails

- `AgentLlm:Subagents` : `MaxDepth=1`, `MaxConcurrent=3`,
  `MaxIterations=40`, `BudgetTokens` herdado do run (debita no mesmo
  `UsageRecord` da conversa — SPEC-20 já cobre).
- Filho morto por timeout/erro → `ToolResult.Error` com log tail —
  o pai decide retry (nunca retry automático infinito).
- `interrupt`/`pause` do usuário propaga p/ filhos ativos.

## 3. API Contract

Nenhum endpoint novo — tools `task`, `task_status`, `task_result` no
catálogo; evento `subagent_run` (state update) + fixtures golden.

## 4. Data model

Sem migration. `SubagentRun` em memória (`ConcurrentDictionary` por
conversa no `ISubagentHost` singleton) — não persiste (runs vivem no
escopo do processo; reinício = filhos órfãos reportados como `aborted`
no próximo `task_status`).

## 5. NFR / Security

- Tool-filter é **server-side** (`FilterToolRegistry`) — nunca instrução
  de prompt. Subagente `explore` não recebe `file_write` — impossível
  mutar por prompt injection.
- Secrets env vars: filho usa o mesmo executor — mesma postura de
  redação (SPEC-24).
- Contexto do filho: prompt + system próprio (compacto) — **não herda
  histórico do pai** (só recebe `prompt` + repo context).

## 6. Task Plan

1. `SubagentPresets` + `FilterToolRegistry` + `ISubagentHost`.
2. `BuiltinTools.Task/TaskStatus/TaskResult` + registro condicional
   (`subagent_depth` ≥1 → `task` no catálogo).
3. `SubagentRunner` (loop reuso) + eventos namespaced + summary→result.
4. Guardrails (depth/concurrency/budget) + interrupt propagation.
5. Testes: presets de filtro, sync run com Fake provider, depth=0 nega
   `task`, background status/result, interrupt propaga.

## 7. Deliverables

`Infrastructure/Agent/Subagents/*`, tools novas, fixtures `subagent_run`,
testes unitários.

## 8. Referências

- alltomatos/opencode "Batuta" (conceito — SPEC-27).
- claude-code subagents `.claude/agents` (conceito).

## 9. Guardrails

- Filho nunca tem `task` (depth cap); pai nunca espera > `MaxIterations`.
- Resumo do filho passa por OutputPolicy (cap 8KB).
- Permissões do filho ⊆ pai (SPEC-29 aplica como overlay).

## 10. DoD

- `task(subagent_type=explore)` roda em fake executor e retorna resumo.
- `task` com depth 0 retorna erro "subagents disabled".
- `task_status`/`task_result` cobrem o ciclo background.
- `dotnet test` verde; fixtures golden para `subagent_run`.
