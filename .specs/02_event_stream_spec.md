# SPEC — 02 · Event Stream

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `event-stream` |
| Type | `Feature / API` |
| Stack | `.NET 10 · C# 14 · EF Core` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
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
