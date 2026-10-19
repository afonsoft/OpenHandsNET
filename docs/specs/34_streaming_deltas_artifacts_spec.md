# SPEC — 34 · Streaming Deltas + External Artifact Store

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `deltas-artifacts` |
| Type | `Feature / Scale` |
| Stack | `.NET 10 · SignalR · S3-compatible` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed` (PR #71) |
| Ticket | #66 |
| Depends on | `02` (events), `24` (activity stream), `25` (artifact_ref) |
| Touches | `AgentRunner` (token emit), `ConversationsHub`, `OutputPolicy`, `ApiClient`/Client |

## 1. User Story

**As a** operador de instância busy
**I want** tokens em streaming **efêmero** (nunca persistidos) e
artefatos em object storage externo
**So that** o event store não guarda milhares de token-events por run
e artifact_ref sobrevive à remoção do sandbox.

Referências: upstream `DeltaFrame`/`StreamingDeltaBatcher` +
durable-supersedes-slot (SPEC-32 RF-01); OpenHands-Cloud filestore
S3/GCS/MinIO (RF-05).

## 2. Scope (RF)

### RF-001 — `streaming_delta` efêmero

- Novo "pseudo-kind" `streaming_delta` — **nunca entra no
  `IEventService`**: vai direto do runner ao hub via
  `IAgentActivityBus` (in-memory por padrão; `IAsyncEnumerable` por
  conversa). `{item_id, seq, text}`.
- O `ActionEvent`/`MessageEvent` durável carrega `item_id`; quando o
  durável chega, o client descarta o slot de deltas com o mesmo
  `item_id` (upstream parity: durable supersedes).
- Batcher no runner: flush a cada 16ms **ou** 64 bytes — reduz churn
  no hub.
- Replay (`GET /events`, `subscribe`) ignora deltas — só duráveis.
- `TokenEvent` persiste **só** como record de usage (já tem
  model/role) — remove-se o `text` do durável? **Não**: manter
  `TokenEvent` como hoje (usage accounting) mas parar de retransmitir
  tokens ao client como evento; o canal de texto é `streaming_delta`.

### RF-002 — `IArtifactStore`

```csharp
public interface IArtifactStore
{
    Task<string> PutAsync(Guid conversationId, string name,
        ReadOnlyMemory<byte> data, CancellationToken ct);
    Task<Stream?> OpenAsync(Guid conversationId, string artifactId,
        CancellationToken ct);
}
```

- `WorkspaceArtifactStore` (default): `/tmp/ohn-artifacts/` no sandbox
  — comportamento atual.
- `S3ArtifactStore` (opt-in `Artifacts:Provider=s3`): MinIO/AWS —
  `openhandsnet-artifacts/<conv>/<id>`; `ArtifactS3Options`.
- `OutputPolicy` usa `IArtifactStore`; observation ganha
  `artifact_ref` opaco (`ws:path` | `art:id`).
- `GET /api/v1/app-conversations/{id}/artifacts/{ref}` — resolve,
  stream, auth do dono; `Range` support p/ preview parcial.

### RF-003 — Lifecycle

Artifact morre com a conversa (delete em `DeleteConversation` —
hook já existente); TTL opcional `Artifacts:RetentionDays` para
sandbox removidos mas conversa viva (reaper diário simples).

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| GET | `/{id}/artifacts/{ref}` | stream do artifact (dono) |

SignalR: mensagem `streaming_delta` fora do stream de eventos
(método `Delta` no hub).

## 4. Data model

Sem migration. S3 keys derivadas de conversationId+artifact id (ULID).

## 5. NFR / Security

- Delta nunca vaza fora da conversa (bus por conversationId, auth no
  hub já existente).
- Artifact endpoint autoriza pelo dono; secrets redigidos **antes**
  do store (SecretRedactor no OutputPolicy já faz).
- S3 client com `ServerSideEncryption` quando suportado; presign
  opcional futuro.

## 6. Task Plan

1. `IAgentActivityBus` + hub method `Delta` + runner batcher
   (`DeltaBatcher`).
2. Client: slot por `item_id`, retire-on-durable.
3. `IArtifactStore` + workspace impl + S3 impl + endpoint + GC hook.
4. Testes: batcher flush, durable-supersedes no client, store put/get
   (fake S3 via `HttpMessageHandler` ou skip-CI sem credencial —
   marcar `[Trait("Category","S3")]` opt-in), endpoint auth.

## 7. Deliverables

`Agent/Deltas/*`, `Artifacts/*`, endpoint, client slot handling,
testes.

## 8. Referências

- upstream `StreamingDeltaBatcher` / `DeltaFrame` (conceito).
- OpenHands-Cloud filestore S3/GCS/MinIO (conceito).

## 9. Guardrails

- Delta **nunca** persistido (rule dura — o store V1 só tem duráveis).
- Artifact store default = workspace (zero deps novas por default).
- S3 provider é opt-in; nenhum segredo no nome do artifact.

## 10. DoD

- Run emite `streaming_delta` e o evento durável final bate no mesmo
  `item_id`; replay não contém deltas.
- `artifact_ref` gravado lê de volta via endpoint (workspace e S3-mock).
- `dotnet test` verde.
