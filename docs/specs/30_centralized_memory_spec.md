# SPEC — 30 · Centralized Memory (org + project)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `centralized-memory` |
| Type | `Feature / Agent Core` |
| Stack | `.NET 10 · EF Core · workspace files` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed` |
| Ticket | #63 |
| Depends on | `22` (context — digest sobrevive compaction), `25` (skills pipeline), `29` (permissão `ask` p/ promote) |
| Touches | `DbContext`, `BuiltinTools`, `RepoContextLoader`, `ToolRegistry` |

## 1. User Story

**As a** usuário recorrente
**I want** que o agente lembre decisões/convenções entre conversas —
no nível do projeto (versionável no repo) e da org (transversal)
**So that** não repito contexto a cada sessão e o agente "aprende".

Referências: alltomatos/opencode "Breniac" memory (SPEC-27 F2),
upstream `.openhands/memory/` (`agent_context.load_memory`),
claude-code `/memory` + `CLAUDE.md` auto-load.

## 2. Scope (RF)

### RF-001 — Dois escopos

| Escopo | Storage | Visibilidade |
|---|---|---|
| `project` | `.openhands/memory/*.md` no workspace do repo | quem lê o repo |
| `org` | tabela `MemoryRecords` (org_id, key, text, updated_by, audit) | membros da org |

Project = versionável (o agente comita se autorizado); org = auditável
(admin UI + `audit_log` de escrita).

### RF-002 — Tool `memory`

```json
{"op":"remember"|"recall"|"forget"|"promote", "scope":"project"|"org",
 "key":"...", "text":"..."}
```

- `remember` → escreve markdown datado (`YYYY-MM-DD-<slug>.md`) no
  workspace (project) ou `MemoryRecords` (org — requer `ask` via SPEC-29
  na primeira vez por sessão).
- `recall` → grep nos dois escopos, resposta capped (OutputPolicy).
- `forget` → remove (org: soft-delete + audit).
- `promote` → project→org com `ask` obrigatório.

### RF-003 — Digest no contexto

`MemoryDigestContributor` no `RepoContextLoader`/`SystemPromptBuilder`:
seção `## Memory` com até N entradas mais recentes de cada escopo
(cap 4KB, ordenado por `updated_at`). É **estável entre turns** —
não entra no pruning da SPEC-22 (fica acima do boundary).

Conteúdo de memória é untrusted (repo) → citado, nunca privilegiado
(mesma regra das repo skills).

### RF-004 — Endpoints

- `GET /api/v1/conversations/{id}/memory` — merged view.
- `GET /api/v1/orgs/{id}/memory` + `DELETE …/{key}` — admin org.
- `MemoryRecord` auditado (`audit_log` de write/delete).

## 3. API Contract

`GET /{id}/memory`, org CRUD; tool `memory`; state event
`memory_digest` quando o digest muda.

## 4. Data model

Migration: `memory_records(id, org_id, key, text, created_by,
updated_at, deleted_at)` + índice `(org_id, key)`.

## 5. NFR / Security

- Org memory exige role member+; escrita auditada.
- `remember`/`promote` em org passam por `ask` (SPEC-29 `memory` rule).
- Digest capado; secrets nunca entram — `SecretRedactor` no `remember`.

## 6. Task Plan

1. `MemoryRecord` + migration + `IMemoryService`.
2. `memory` tool + endpoints + audit.
3. Digest contributor + injeção no prompt (estável).
4. Testes: remember project/org, recall, promote ask-gated, digest
   render, audit trail.

## 7. Deliverables

`Infrastructure/Memory/*`, `Domain/Identity/MemoryRecord`, tool, endpoints,
migration, testes.

## 8. Referências

- alltomatos/opencode Epic E11 "Breniac" (conceito).
- upstream `.openhands/memory/` + `agent_context.load_memory`.
- claude-code `/memory`.

## 9. Guardrails

- Memória é dados untrusted → sempre citada no prompt.
- Org memory: sem `ask` por conversa configurado, escreve bloqueado.
- Nenhuma memória sobrescreve regra do system prompt.

## 10. DoD

- `memory remember` project → arquivo no workspace; org → DB + audit.
- Digest aparece no system prompt de runs subsequentes e sobrevive
  a compaction.
- `dotnet test` verde.
