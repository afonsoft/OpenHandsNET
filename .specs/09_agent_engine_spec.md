# SPEC — 09 · Agent Engine (LLM Loop & Tools)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `agent-engine` |
| Type | `Feature` |
| Stack | `.NET 10 · C# 14 · Semantic Kernel` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Done` (PR [#19](https://github.com/afonsoft/OpenHandsNET/pull/19)) |
| Ticket | `[A CRIAR]` (Epic [#1](https://github.com/afonsoft/OpenHandsNET/issues/1)) |
| Depends on | `02_event_stream_spec`, `03_runtime_sandbox_spec` |
| Feeds | `04_rest_and_signalr_api_spec` (invocado pelo start-task worker / send-message) |

## 1. User Story

**As a** usuário do OpenHandsNET
**I want** um motor de agente que recebe mensagens, chama o LLM e executa tools no sandbox
**So that** a conversa realmente produza trabalho — comando a comando, arquivo a
arquivo — e não apenas registre eventos. **Este é o coração do produto.**

**Problem context:** o upstream implementa o agente no SDK
(`openhands.sdk.agent` + `openhands.sdk.llm` + `openhands.sdk.tool`) e o expõe via
agent-server dentro do sandbox. No OpenHandsNET o agente-worker é **.NET
in-process** (decisão SPEC 03): um `AgentRunner` por conversa que orquestra
`IAgentLlmProvider` → tool calls → `ISandboxExecutor` → eventos. Sem esta SPEC,
SPECs 02–04 produzem uma API bonita que nunca responde.

## 2. Scope

**In scope:**
- `IAgentLlmProvider` abstrato: `ChatStreamAsync(messages, tools, options)` →
  streaming de completions (text deltas + tool calls + usage).
- `SemanticKernelLlmProvider` (impl primária): OpenAI-compat endpoint
  (`LLM__BASE_URL` + `LLM__API_KEY` + `LLM__MODEL`), function calling nativo SK.
- `AgentRunner` (1 por conversa): loop ReAct `message → LLM → tool calls →
  observations → LLM → …` até `Finish`/`Interrupt`/erro/`max_iterations`.
- Tool set built-in (paridade upstream `sdk/tool/builtins`):
  `bash` (exec via ISandboxExecutor), `file_read`, `file_write`, `file_edit`,
  `think`, `task_tracker`, `finish`, `browser` (stub → `NotImplementedObservation`
  até Playwright de SPEC 03), `git_commit` (SPEC 12), `invoke_skill` (SPEC 11).
- Tool-call → eventos: `ActionEvent` (request) + `ObservationEvent` (result) com
  `parent_id` correto (tool call é filho da LLM request; sub-agente = filho do
  ActionEvent delegador).
- Streaming: `StreamingDeltaEvent` por token/chunk + `TokenEvent` por completion
  (`LLMCompletionLogEvent` opcional debug).
- `execution_status` state machine: `idle|running|finished|error|stuck|
  waiting_for_confirmation` via `ConversationStateUpdateEvent`.
- **Confirmation mode**: `confirmation_mode=true` → ações marcadas `risky`
  pausam o loop (`waiting_for_confirmation`) até `ConfirmAction(approved)` do hub;
  reject → `UserRejectObservation` + loop continua.
- Pause/Interrupt: `InterruptEvent`/`PauseEvent` interrompem o loop com graça
  (finish tool-call corrente, salva cursor).
- `max_iterations` (default 250) + `max_budget_usd?` → `stuck` ao estourar.
- Skills: system prompt montado com `ISkillService` (SPEC 11) + `SystemPromptEvent`.
- `ICondenser` stub (interface + no-op MVP; LLM condensation = fase 2).

**Out of scope:** ACP agent (`acp_agent.py`), critic model, multi-agent/sub-agents
paralelos (`parallel_executor`), LLM router/fallback chains, vision, condensation
LLM real — todos fase 2/3 (interfaces deixadas prontas).

## 3. Technical Context

**Upstream (evidência de design, MIT):**
- `sdk/agent/agent.py` — loop principal; `sdk/agent/base.py`, `stream_context.py`.
- `sdk/llm/{llm,message,llm_response,streaming}.py` — provider layer LiteLLM-equiv.
- `sdk/tool/{tool,schema,registry,defaults}.py` + `tool/builtins/{finish,think,
  invoke_skill,switch_llm,vision_inspect,classify_and_switch_llm}.py`.
- `sdk/event/user_action.py` — mapeamento tool→ActionEvent; `conversation_state.py`.
- Regra: eventos são a única fonte de verdade; o runner **consome e produz**
  eventos (nunca estado paralelo).

**Where:**
- `Application/Agent/` — `IAgentLlmProvider`, `IAgentRunner`, `AgentOptions`,
  `AgentTool` (definição + handler), `ToolCallContext`.
- `Infrastructure/Agent/` — `SemanticKernelLlmProvider`, `AgentRunner` (loop),
  `ToolRegistry`, `Tools/*` (handlers sobre `ISandboxExecutor`).
- `Infrastructure/HostedServices/AgentRunnerHost.cs` — ciclo de vida dos runners.

**Files to read before implementing:**
- `.specs/02_event_stream_spec.md` · `.specs/03_runtime_sandbox_spec.md`
- `migration-plan/research/api-inventory.md` §6 (agent-server roles).

## 4. Requirements

### RF-001: IAgentLlmProvider
```csharp
public interface IAgentLlmProvider
{
    string ModelId { get; }
    IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<ToolSchema> tools,
        LlmOptions options, CancellationToken ct);
    Task<ModelInfo> GetModelInfoAsync(CancellationToken ct);
}
// LlmStreamChunk = TextDelta | ToolCallDelta | CompletionEnd(usage, finish_reason)
// LlmMessage = { role: system|user|assistant|tool, content, tool_calls?, tool_call_id? }
```
- Config: `LLM__MODEL` (ex. `gpt-5-mini`, `anthropic/claude-sonnet`),
  `LLM__BASE_URL` (OpenAI-compat: OpenAI, Azure, Ollama, LiteLLM, `afonsoft/LLMRouter`),
  `LLM__API_KEY`, `LLM__TIMEOUT`, `LLM__MAX_RETRIES`.
- Sem chave configurada → `MockLlmProvider` dev/test (responde scriptado,
  **jamais** em produção — bloquear quando `deployment_mode != oss` sem flag).

### RF-002: AgentRunner loop (ReAct)
1. `StartAsync(conversationId)` — valida sandbox RUNNING, monta `system_prompt`
   (persona + skills + workspace + tools) → `SystemPromptEvent`.
2. Mensagem do usuário → `MessageEvent(source=user)` → `execution_status=running`.
3. Itera: `ChatStreamAsync` → deltas (`StreamingDeltaEvent`) → tool calls
   completos → para cada um `ActionEvent(source=agent)` → dispatch →
   `ObservationEvent` → append tool message → próxima iteração.
4. Termina em: `finish` tool → `finished`; `InterruptEvent` → `idle`;
   erro → `AgentErrorEvent`/`ConversationErrorEvent` → `error`;
   iteração > `max_iterations` → `stuck`.
5. `TokenEvent` por completion com `{prompt_tokens, completion_tokens, cost_usd?}`.
- **Single writer:** um `AgentRunner` ativo por conversa (`ConcurrentDictionary
  + SemaphoreSlim`); `send-message` em `running` → enqueue `PendingMessage`
  (paridade upstream `pending_messages`).

### RF-003: Tools built-in
| Tool | Handler | Risky? |
|---|---|---|
| `bash` | `ISandboxExecutor.ExecAsync` (ou BashSession p/ persistente) | sim (allowlist config) |
| `file_read` | `ReadFileAsync` | não |
| `file_write`/`file_edit` | `WriteFileAsync` (diff patch `file_edit`) | sim |
| `task_tracker` | `ITaskTrackerService` (checklist → `ConversationStateUpdateEvent.plan`) | não |
| `think` | no-op (raciocínio interno → `ActionEvent`) | não |
| `git_commit` | `IGitService.CommitAsync` (SPEC 12) | sim |
| `browser` | Playwright `BrowserService` (SPEC 03 RF-005) ou stub | sim |
| `invoke_skill` | `ISkillService` (SPEC 11) | não |
| `finish` | encerra loop com `final_message` | não |
| `mcp__{server}__{tool}` | `IMcpClientService.CallToolAsync` (SPEC 07) | por servidor |

- JSON Schema das tools derivado dos records (`ToolSchema {name, description≤1024, input_schema}`).
- Timeout por call (default 120s, `bash` longo 600s); cancelamento via `ct`.

### RF-004: Confirmation mode
- `Conversation.ConfirmationMode=true` → antes de executar tool `risky`:
  `ConversationStateUpdateEvent(execution_status=waiting_for_confirmation,
  pending_action=ActionEvent.id)` → aguarda `ConfirmAction` (hub, SPEC 04).
- `approved` → executa; `rejected` → `UserRejectObservation` (motivo) → segue.
- Timeout de confirmação configurável (default 30min) → `InterruptEvent`.

### RF-005: Event mapping exato
- Tool request → `ActionEvent{tool_name,args,thought?}` `parent_id`=último delta/message.
- Tool result → `ObservationEvent{tool_name,output,exit_code?,file_path?,screenshot?}` `parent_id`=ActionEvent.id.
- Erro do agente → `AgentErrorEvent{error,recoverable}`; erro de plataforma → `ServerErrorEvent`.
- Todo evento via `IEventService.AppendAsync` (fan-out automático → hub).

### RF-006: Provider switch / gateway
- `AgentLlm__Provider=semantic_kernel` (default) | `openai_compat` (HttpClient direto,
  para endpoints sem suporte SK) — ambas atrás de `IAgentLlmProvider`.
- Gateway recomendado p/ múltiplos modelos: LiteLLM/`afonsoft/LLMRouter` via `LLM__BASE_URL`.

**Invariants:** estado só muda por eventos; runner é idempotente em reconnect
(resume a partir do último evento); secrets nunca entram no prompt
(`{{secret:NAME}}` resolvido só no env do sandbox); prompt montado no servidor,
nunca no client.

## 5. API Contract

Nenhum endpoint novo. Consumido por:
- `POST /app-conversations` (start-task worker → READY → `run` inicial se `initial_message` presente).
- `POST /{id}/send-message` (`run=true` → `AgentRunner.Enqueue`).
- Hub `ConfirmAction`, `Interrupt`, `Pause`.

## 6. Critérios de Aceite

- [ ] Conversa criada com `initial_message` → loop roda até `finish` → `execution_status=finished` (MockLlm + sandbox fake).
- [ ] Tool call `bash` executa de fato no sandbox fake/Docker e `ObservationEvent` carrega stdout/exit_code.
- [ ] Streaming: `StreamingDeltaEvent` emitidos antes da `ActionEvent` final.
- [ ] `confirmation_mode` pausa em `waiting_for_confirmation`; approve executa, reject emite `UserRejectObservation`.
- [ ] `InterruptEvent` interrompe entre iterações; `send-message` durante run → pending e processa após `finish`/idle.
- [ ] `max_iterations` → `stuck`; falha de LLM → `AgentErrorEvent` + retry exponencial (1x).
- [ ] Secrets não aparecem no payload enviado ao LLM (teste de redaction).

## 7. Task Plan

1. `IAgentLlmProvider` + `LlmMessage/LlmStreamChunk/ToolSchema` + `MockLlmProvider` (verify: unit tests).
2. `SemanticKernelLlmProvider` (chat streaming + function calling + usage) (verify: contract test com endpoint fake SSE).
3. `ToolRegistry` + tools built-in sobre `ISandboxExecutor` (verify: tool tests).
4. `AgentRunner` loop + state machine + event mapping (verify: loop E2E com MockLlm).
5. Confirmation mode + pending messages + interrupt/pause (verify: scenario tests).
6. `AgentRunnerHost` + wiring com start-task/send-message (verify: integration).
7. Redaction de secrets + condenser stub (verify: security test).

## 8. Deliverables — arquivos a criar/modificar

```text
src/OpenHandsNET.Application/Agent/IAgentLlmProvider.cs
src/OpenHandsNET.Application/Agent/{LlmMessage,LlmStreamChunk,LlmOptions,ToolSchema,
  ModelInfo,AgentOptions}.cs
src/OpenHandsNET.Application/Agent/IAgentRunner.cs           # Start/Enqueue/Confirm/Interrupt/Pause
src/OpenHandsNET.Application/Agent/IToolRegistry.cs          # catálogo estático + mcp__*
src/OpenHandsNET.Application/Agent/ITaskTrackerService.cs    # planner checklist
src/OpenHandsNET.Application/Agent/ICondenser.cs             # no-op MVP
src/OpenHandsNET.Infrastructure/Agent/SemanticKernelLlmProvider.cs
src/OpenHandsNET.Infrastructure/Agent/OpenAiCompatLlmProvider.cs
src/OpenHandsNET.Infrastructure/Agent/MockLlmProvider.cs     # dev/test
src/OpenHandsNET.Infrastructure/Agent/AgentRunner.cs         # loop ReAct
src/OpenHandsNET.Infrastructure/Agent/ToolRegistry.cs
src/OpenHandsNET.Infrastructure/Agent/Tools/{BashTool,FileReadTool,FileWriteTool,
  FileEditTool,TaskTrackerTool,ThinkTool,GitCommitTool,BrowserTool,
  InvokeSkillTool,FinishTool,McpToolBridge}.cs
src/OpenHandsNET.Infrastructure/Agent/SystemPromptBuilder.cs # persona+skills+workspace
src/OpenHandsNET.Infrastructure/HostedServices/AgentRunnerHost.cs
src/OpenHandsNET.Infrastructure/Agent/SecretRedactor.cs      # mascaramento p/ prompt/logs
tests/OpenHandsNET.UnitTests/Agent/{AgentRunnerLoopTests,ToolRegistryTests,
  ConfirmationModeTests,SecretRedactorTests}.cs
tests/OpenHandsNET.IntegrationTests/Agent/AgentRunnerE2ETests.cs # MockLlm+sandbox fake
```

## 9. Referências e links

### Internas (repo)
- `.specs/02_event_stream_spec.md` — eventos produzidos/consumidos.
- `.specs/03_runtime_sandbox_spec.md` — `ISandboxExecutor` (execução real das tools).
- `.specs/04_rest_and_signalr_api_spec.md` — gatilhos (send-message, ConfirmAction).
- `migration-plan/research/stack-research.md` — decisão `IAgentLlmProvider`+SK aprovada.

### Upstream — código-fonte (baseline `acca181`, MIT)
- Loop do agente: https://github.com/OpenHands/software-agent-sdk/blob/acca181/openhands-sdk/openhands/sdk/agent/agent.py
  (+ `base.py`, `stream_context.py`, `response_dispatch.py`, `parallel_executor.py` no mesmo dir)
- LLM layer: https://github.com/OpenHands/software-agent-sdk/tree/acca181/openhands-sdk/openhands/sdk/llm
  (`llm.py`, `message.py`, `llm_response.py`, `streaming.py`, `fallback_strategy.py`, `router/`)
- Tools: https://github.com/OpenHands/software-agent-sdk/tree/acca181/openhands-sdk/openhands/sdk/tool
  (`tool.py`, `schema.py`, `registry.py`, `defaults.py`, `builtins/` — `finish.py`, `think.py`,
  `invoke_skill.py`, `switch_llm.py`)
- Contexto/system prompt: https://github.com/OpenHands/software-agent-sdk/tree/acca181/openhands-sdk/openhands/sdk/context
- Condenser: …/sdk/event/condenser.py + `openhands-sdk/openhands/sdk/context/condenser/` se existir
- Exemplos de uso do SDK: https://github.com/OpenHands/software-agent-sdk/tree/acca181/examples

### Bibliotecas / framework
- Semantic Kernel overview: https://learn.microsoft.com/semantic-kernel/overview/
- SK function calling: https://learn.microsoft.com/semantic-kernel/concepts/ai-services/chat-completion/function-calling/
- SK streaming: https://learn.microsoft.com/semantic-kernel/concepts/ai-services/chat-completion/
- Microsoft.Extensions.AI `IChatClient` (abstração alternativa avaliável na impl):
  https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai
- LiteLLM (gateway OpenAI-compat): https://docs.litellm.ai/
- `System.Threading.Channels` (fila de mensagens/runner): https://learn.microsoft.com/dotnet/core/extensions/channels
- Hosted services: https://learn.microsoft.com/aspnet/core/fundamentals/host/hosted-services

## 10. Guardrails de execução (Devin)

- O runner **nunca** muta `Conversation` direto — só via eventos (única fonte de verdade).
- Um `AgentRunner` por conversa; segunda mensagem → pending queue (paridade `pending_messages`).
- Tool `risky` em `confirmation_mode` **sempre** pausa antes de executar — sem exceção.
- Secrets: resolver `{{secret:NAME}}` apenas no env do sandbox; redactor no prompt
  e em qualquer log/evento que possa carregar output (`SecretRedactor` central).
- `MockLlmProvider` bloqueado fora de dev/test (`deployment_mode==oss` + flag
  `AgentLlm__AllowMock=true`); nunca em cloud/self_hosted.
- Timeouts e `max_iterations` sempre aplicados — loop nunca roda sem bound.
- Sem dependência de pacote pré-release (SK versões estáveis >7 dias).

## 11. Definition of Done

- [ ] Loop completo MockLlm→tool→observation→finish verde em integration test.
- [ ] Confirmation/interrupt/pending/state machine testados (cenários §6).
- [ ] Streaming + TokenEvent com usage real do provider.
- [ ] Secrets redacted em prompt/output (teste dedicado).
- [ ] `dotnet test` verde; SPEC `Done`; issue fechada pelo PR.
