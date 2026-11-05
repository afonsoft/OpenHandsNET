# SPEC — 128 · Context & memory DB-backed (granular + portável)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `context-memory-db` |
| Type | `Feature` |
| Status | `In review` |
| Ticket | #333 (umbrella #329, Epic #317) — débito SPEC-121 |
| Scope | `src/OpenHandsNET.Infrastructure/Agent/Tools/` (context/memory), EF Core entities + migration, `src/OpenHandsNET.Domain` |

## 1. User Story

**As a** agente em execuções longas, **I want** context-notes e memória
persistidos no banco por conversa (granulares, consultáveis por chave),
**so that** o estado sobreviva a trocas de sandbox e seja resumível/
inspecionável pela API — como as `context_*`/`memory_*` tools do
OpenWebUiCode.

## 2. Contexto / escopo

- SPEC-121 entregou `context_notes` **file-based** (`.openhands/context-notes.md`
  no sandbox) — funciona, mas some com o sandbox e não é granular.
- OWUIC persiste por conversa no DB: `context_save/get/list/delete`,
  `context_summary`, `memory_get/set/search` por chave.
- Mudança de schema → EF migration obrigatória (hard rule).

## 3. Requisitos

| ID | Requisito |
|---|---|
| RF-001 | Entidades `ConversationContextNote` (`id`, `conversation_id`, `title`, `body`, `created_at`) e `ConversationMemory` (`conversation_id`, `key`, `value`, `updated_at`) + migration EF Core. |
| RF-002 | Tools `context_save`, `context_get`, `context_list`, `context_delete`, `context_summary` — DB-backed, `ToolResult` estruturado. |
| RF-003 | Tools `memory_get`, `memory_set`, `memory_search` — key/value por conversa; search por substring/fts. |
| RF-004 | Manter `context_notes` (file-based) como alias/deprecado ou migrar leitura para DB com fallback ao arquivo (decisão na implementação — não quebrar runs existentes). |
| RF-005 | Limites: nota ≤ 32 KB, ≤ 200 notas/conversa, memória ≤ 8 KB/valor — erros estruturados (não truncar silenciosamente). |
| RF-006 | Cleanup: notas/memória deletadas em cascata ao remover conversa. |

## 4. Acceptance

- [ ] `context_save` + `context_get` roundtrip via tools (unit + integration).
- [ ] `memory_set`/`memory_search` por chave funciona entre runs da mesma conversa.
- [ ] Migration aplica limpa em SQLite e PostgreSQL.
- [ ] Delete de conversa remove notas/memória (cascade).
- [ ] Registro no `ToolRegistry` — deferred (`tool_search` carrega sob demanda).

## 5. API Contract

Sem endpoints REST novos. Entities seguem convenção V1 (`Id` ULID,
audit `CreatedAt`). JSON snake_case nos payloads das tools.

## 6. Task Plan

1. Entidades + `DbContext` + `dotnet ef migrations add`.
2. `BuiltinContext` / `BuiltinMemory` tools + registro deferred.
3. Bridge de migração file→DB para `context_notes` (se decidido).
4. Testes unitários + integração (roundtrip, cascade, limites).

## 7. Riscos / notas

- Dupla fonte de verdade (arquivo + DB) durante transição — definir
  precedência clara (DB vence; arquivo é fallback read-only).
- Postgres parity: testar migration nos dois providers.
