# SPEC — 02 · Event Stream

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `event-stream` |
| Type | `Feature / API` |
| Stack | `.NET 10 · C# 14 · EF Core` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
| Ticket | [#3](https://github.com/afonsoft/OpenHandsNET/issues/3) (Epic [#1](https://github.com/afonsoft/OpenHandsNET/issues/1)) |
| Depends on | `01_architecture_overview` |

## 1. User Story

**As a** desenvolvedor do núcleo OpenHandsNET
**I want** um EventStream fiel ao modelo V1 do upstream (união discriminada por
`kind`, IDs ULID/UUID, árvore via `parent_id`)
**So that** todo estado da conversa seja replay-auditável, paginável e
transmitível ao UI — o coração do sistema, como no OpenHands.

**Problem context:** o guia do usuário (seção 3.1) modela eventos no formato
**V0** (`id` inteiro, `action`/`args`, `observation`/`content`). A API V0 está
deprecada (remoção 2026-04-01); o modelo V1 real — verificado no OpenAPI
`search-events` e no código — usa `kind` como discriminador sobre 19 tipos,
`id` ULID/UUID, `source ∈ {agent,user,environment,hook}` e `parent_id`
(branching da árvore de eventos). Esta SPEC adota **V1**.

## 2. Scope

**In scope:**
- Hierarquia de records `Event` (`abstract record EventBase` + 19 tipos).
- Serialização JSON com discriminador `kind` (compatível com OpenAPI upstream).
- `IEventService`: `AppendAsync`, `SearchAsync` (filtros upstream), `CountAsync`, `GetBatchAsync`, `SubscribeAsync`.
- Event store EF Core append-only, paginação cursored (`next_page_id`).
- Publicação para realtime (SignalR, SPEC 04) e webhooks.

**Out of scope:** endpoints HTTP (SPEC 04), condensação LLM (fase 2), replay UI.

## 3. Technical Context

**Upstream (verificado):**
- `EventPage{items,next_page_id}`; filtros `kind__eq`, `timestamp__gte`,
  `timestamp__lt`, `sort_order=TIMESTAMP|TIMESTAMP_DESC`, `page_id`, `limit≤100`.
- 19 kinds: `ActionEvent, MessageEvent, ObservationEvent, AgentErrorEvent,
  UserRejectObservation, SystemPromptEvent, TokenEvent, StreamingDeltaEvent,
  ConversationStateUpdateEvent, ConversationErrorEvent, Condensation,
  CondensationRequest, CondensationSummaryEvent, InterruptEvent, PauseEvent,
  ServerErrorEvent, ACPToolCallEvent, HookExecutionEvent, LLMCompletionLogEvent`.
- Base: `id`(ULID str), `timestamp`, `source`, `parent_id?`, `kind`.

**Where:** `Domain/Events/*` (records), `Application` (`IEventService`, DTOs página),
`Infrastructure/Eventing/` (EF store, dispatcher).

## 4. Requirements

### RF-001: Modelo de eventos
- `abstract record EventBase { string Id; DateTimeOffset Timestamp; EventSource Source; string? ParentId; }` com `Id` = ULID/UUID7 string.
- Sub-records por kind com payload próprio: ex. `ActionEvent` carrega a ação do
  agente (`CmdRun`, `FileRead/Write`, `Browse`, `TaskTracker`, `Finish`…);
  `ObservationEvent` o resultado; `ConversationStateUpdateEvent` o status
  (`execution_status`, `agent_status`, `confirmation`).
- `[JsonPolymorphic(TypeDiscriminatorPropertyName="kind")]` + mapeamento explícito
  de cada kind ↔ nome upstream (string exata, ex. `"MessageEvent"`).

### RF-002: Event store
- Tabela `events`: PK `Id`, índices `(ConversationId, Timestamp)`, `(ConversationId, Kind)`;
  coluna `payload` JSONB-equivalente (JSON text em SQLite).
- Append-only: sem UPDATE/DELETE; ordenação determinística por timestamp+id.

### RF-003: IEventService
```csharp
public interface IEventService
{
    ValueTask<EventBase> AppendAsync(Guid conversationId, EventBase e, CancellationToken ct);
    Task<EventPage> SearchAsync(Guid conversationId, EventQuery q, CancellationToken ct);
    Task<long> CountAsync(Guid conversationId, EventQuery q, CancellationToken ct);
    Task<IReadOnlyList<EventBase>> GetBatchAsync(Guid conversationId, IReadOnlyCollection<Guid> ids, CancellationToken ct);
    IAsyncEnumerable<EventBase> SubscribeAsync(Guid conversationId, CancellationToken ct);
}
public sealed record EventQuery(string? KindEq, DateTimeOffset? TimestampGte,
    DateTimeOffset? TimestampLt, EventSortOrder Sort = EventSortOrder.Timestamp,
    string? PageId = null, int Limit = 100);
```

### RF-004: Fan-out
- Todo append dispara: (a) persistência, (b) `IEventBus` in-process → SignalR,
  (c) webhook dispatcher assíncrono com retry/backoff (payload = evento serializado).

### RF-005: Compat golden
- Fixtures JSON (derivadas do OpenAPI) desserializam para os records corretos e
  resserializam de forma byte-equivalente (teste Verify/golden).

**Invariants:** nenhum estado muda fora do EventStream; `parent_id` preserva a
árvore (sub-agentes = filhos do `ActionEvent` delegador); eventos são imutáveis.

## 5. API Contract

Implementado por SPEC 04 — referência upstream a honrar:
`GET /api/v1/conversation/{conversation_id}/events/search|count|`.

## 6. Critérios de Aceite

- [ ] Round-trip JSON dos 19 kinds preserva `kind` + payload (golden tests).
- [ ] `SearchAsync` respeita `kind__eq`, faixa de timestamp, `sort_order`, `limit≤100` e `page_id` cursored sem duplicar item.
- [ ] 10k eventos/conv persistidos e paginados < 200ms por página (SQLite, benchmark).
- [ ] Subscribe entrega eventos na ordem de append, em tempo real (teste de canal).

## 7. Task Plan

1. Records + discriminador `kind` + serialização (verify: golden tests).
2. EF entity + `EventStore` append-only + índices (verify: integration test).
3. `EventService` com search/count/batch/subscribe (verify: unit tests).
4. `IEventBus` in-memory channel + webhook dispatcher skeleton (verify: ordering test).

## 8. Deliverables — arquivos a criar/modificar

```text
src/OpenHandsNET.Domain/Events/EventBase.cs           # abstract record + kind discriminator
src/OpenHandsNET.Domain/Events/{Action,Message,Observation,AgentError,
  UserRejectObservation,SystemPrompt,Token,StreamingDelta,
  ConversationStateUpdate,ConversationError,Condensation,
  CondensationRequest,CondensationSummary,Interrupt,Pause,
  ServerError,AcpToolCall,HookExecution,LlmCompletionLog}Event.cs
src/OpenHandsNET.Domain/Events/EventSource.cs         # enum agent|user|environment|hook
src/OpenHandsNET.Domain/Events/EventJsonConverter.cs  # [JsonPolymorphic kind] + snake_case
src/OpenHandsNET.Application/Events/IEventService.cs  # Append/Search/Count/GetBatch/Subscribe
src/OpenHandsNET.Application/Events/EventQuery.cs     # record + EventSortOrder + EventPage
src/OpenHandsNET.Application/Events/IEventBus.cs      # fan-out in-process (Channel<T>)
src/OpenHandsNET.Infrastructure/Eventing/EventStore.cs        # EF append-only
src/OpenHandsNET.Infrastructure/Eventing/EventDispatcher.cs   # hosted service → bus/webhooks
src/OpenHandsNET.Infrastructure/Eventing/WebhookDispatcher.cs # retry/backoff skeleton
tests/OpenHandsNET.UnitTests/Events/EventSerializationTests.cs    # round-trip 19 kinds
tests/OpenHandsNET.UnitTests/Events/Golden/                       # fixtures .json (Verify)
tests/OpenHandsNET.IntegrationTests/EventStoreTests.cs            # paging/ordering real DB
```

## 9. Referências e links

### Internas (repo)
- `migration-plan/research/api-inventory.md` §3 — contrato events search/count.
- `docs/reference/openhands-upstream/search-events-openapi.md` — extrato OpenAPI (schema
  dos 19 kinds, `EventPage`, `sort_order`, `limit≤100`).

### Upstream — código-fonte (baseline `acca181`, MIT)
- Modelo base: https://github.com/OpenHands/software-agent-sdk/blob/acca181/openhands-sdk/openhands/sdk/event/base.py
- Tipos/kinds: https://github.com/OpenHands/software-agent-sdk/blob/acca181/openhands-sdk/openhands/sdk/event/types.py
- `user_action` (Message/Interrupt/etc.): https://github.com/OpenHands/software-agent-sdk/blob/acca181/openhands-sdk/openhands/sdk/event/user_action.py
- `conversation_state` (state update): https://github.com/OpenHands/software-agent-sdk/blob/acca181/openhands-sdk/openhands/sdk/event/conversation_state.py
- `condenser` (Condensation*): https://github.com/OpenHands/software-agent-sdk/blob/acca181/openhands-sdk/openhands/sdk/event/condenser.py
- `token`/`streaming_delta`/`llm_completion_log`: …/event/token.py, …/streaming_delta.py, …/llm_completion_log.py
- `hook_execution`, `acp_tool_call`, `conversation_error`, `security`: ids. no dir …/event/
- **Política de compat de eventos** (deprecated handlers permanentes, golden fixtures):
  https://github.com/OpenHands/software-agent-sdk/blob/acca181/openhands-sdk/openhands/sdk/AGENTS.md
- Fixtures de teste upstream: https://github.com/OpenHands/software-agent-sdk/tree/acca181/tests/sdk/event/

### Documentação oficial
- Events search (OpenAPI): https://docs.openhands.dev/openhands/usage/cloud/api-reference/events/search-events
- Cloud API (contexto V1): https://docs.openhands.dev/openhands/usage/cloud/cloud-api

### Bibliotecas / framework
- `System.Text.Json` polymorphism: https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/polymorphism
- ULID (Cysharp): https://github.com/Cysharp/Ulid
- Verify (snapshot/golden): https://github.com/VerifyTests/Verify
- `System.Threading.Channels`: https://learn.microsoft.com/dotnet/core/extensions/channels
- EF Core value conversion JSON: https://learn.microsoft.com/ef/core/modeling/value-conversions

## 10. Guardrails de execução (Devin)

- `kind` discrimina por **string exata** do upstream (ex.: `"StreamingDeltaEvent"`),
  serializada `snake_case` nos demais campos (`parent_id`, `event_id`…).
- Eventos **imutáveis** e **append-only**: nunca UPDATE/DELETE na tabela `events`.
- Nenhum estado muda fora do EventStream (invariante central do upstream).
- IDs ULID/UUID7 string (ordenação lexicográfica == temporal); nunca inteiros.
- Fixtures golden: derivar do OpenAPI upstream; **não copiar** payloads do enterprise.
- Compat: desserialização tolerante a campos extras (política upstream de backward-compat).

## 11. Definition of Done

- [ ] Golden tests dos 19 kinds verdes (round-trip byte-equivalente de campos-chave).
- [ ] `SearchAsync` honra `kind__eq`/timestamps/`sort_order`/`page_id`/`limit≤100`.
- [ ] Store EF append-only + índices; benchmark 10k eventos/conv < 200ms/página.
- [ ] `SubscribeAsync` entrega em ordem; dispatcher dispara bus + webhook skeleton.
- [ ] `dotnet test` verde; SPEC `Done`; issue fechada pelo PR.
