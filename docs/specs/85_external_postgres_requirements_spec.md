# SPEC — 85 · External PostgreSQL — requisitos de versão/encoding

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `external-postgres-requirements` |
| Type | `Documentation` (fix) |
| Stack | `docs/` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed (PR #226, 4733e92)` |
| Ticket | `gap-analysis 2026-10-22` · `GAP-documentation-external-postgres-reqs` |
| Depends on | `14` RF-003 (external postgres doc) — entregue |
| Reference | `docs.openhands.dev/enterprise/external-postgres` (conceitos apenas) |
| Touches | `docs/deployment/external-postgres.md`, `docs/en/deployment/external-postgres.md` |

## 1. User Story

**As a** operador apontando o OpenHandsNET para um PostgreSQL existente
**I want** os requisitos de versão e encoding explícitos no doc
**So that** evito falha de migration/descobrir em runtime que o cluster
está em LATIN1 ou versão antiga — upstream exige **PostgreSQL ≥ 16.4**
e **encoding UTF8**; nosso doc cobre conexão/roles/extensões mas omite
os dois requisitos.

## 2. Escopo

In-scope:

- Seção "Requisitos" no topo do doc: **PostgreSQL ≥ 16** (alinhado ao
  mínimo upstream 16.4; EF Core 10/Npgsql suportam 16/17) e **UTF8**
  (`SELECT datname, pg_encoding_to_char(encoding) FROM pg_database;`
  + `CREATE DATABASE ... WITH ENCODING 'UTF8'`).
- Nota sobre topologia: upstream usa 5 databases por serviço
  (`openhands`, `keycloak`, `litellm`, `runtime_api_db`, `automations`);
  OpenHandsNET usa **um** database + `litellm` opcional no compose
  (`deploy/postgres-init/00-litellm-db.sh`) — documentar a diferença
  (seção "Divergência de contrato vs upstream" da hard rule 4).
- Mirror en.

Out-of-scope: mudança de código/schema, multi-database split.

## 3. Requisitos

1. Requisitos antes da seção de conexão (fail-fast para o leitor).
2. SQL de verificação de encoding copiável.
3. Divergência multi-DB→single-DB explicitada.

## 4. Task Plan

1. Editar `docs/deployment/external-postgres.md`. 2. Mirror en.

## 5. Deliverables

- Doc atualizado (pt+en).

## 6. Referências

- Upstream: `docs.openhands.dev/enterprise/external-postgres`.
- Repo: `docs/deployment/external-postgres.md`, `docker-compose.postgres.yaml`,
  `deploy/postgres-init/00-litellm-db.sh`.

## 7. Guardrails

- Doc-only; sem valores sensíveis; manter estilo/tabelas existentes.

## 8. DoD

- Versão ≥16 + UTF8 documentados com SQL de check; divergência de
  topologia registrada; mirror en sincronizado.
