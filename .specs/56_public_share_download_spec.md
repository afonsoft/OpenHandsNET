# SPEC — 56 · Public Share + Trajectory Download

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `public-share-download` |
| Type | `Feature / API + Frontend` |
| Stack | `.NET 10 · Blazor WASM · EF Core` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Delivered` |
| Ticket | `TBD` |
| Depends on | `04` (events API), `10` (EF), `41` (render de eventos) |
| Reference | competitive-analysis §6.1 (G5/G25); DeepWiki
  `OpenHands/OpenHands` — "Public Share" toggle + `/download` zip |
| Touches | `src/OpenHandsNET.{Application,Infrastructure,Api,Client}`, `tests/` |

## 1. User Story

**As a** usuário do OpenHandsNET
**I want** gerar um link público read-only de uma conversa e baixar a
trajetória completa em zip
**So that** compartilho resultados com quem não tem conta e arquivo
evidência — paridade com o Public Share e `/download` do OpenHands
Cloud. Hoje só existe `export` na CLI (SPEC-31) para quem tem acesso.

## 2. Scope (RF)

### RF-001 — Share link

- Entidade `conversation_shares`: `Id, ConversationId, Token (256-bit,
  hash SHA-256 armazenado), CreatedBy, ExpiresAt?, RevokedAt?`.
- `POST /api/v1/app-conversations/{id}/share` → `{ url }` (cria ou
  retorna ativo); `DELETE .../share` revoga; `GET .../share` status.
- `GET /api/v1/shared/{token}` → dados públicos da conversa:
  title, created_at, eventos **filtrados** (ver RF-003), artifacts
  marcados públicos. `GET /api/v1/shared/{token}/events?cursor=`.
- Sem auth; token na URL. Rate limit por IP (60 req/min default).

### RF-002 — Página pública

- Rota client `/share/{token}` — layout read-only reutilizando os
  renderers de evento da SPEC-41 (chat + tool cards), sem composer,
  sem ações. Banner "Shared conversation — read only".
- Meta tags (og:title/description) para unfurl.

### RF-003 — Filtro de segurança

- Eventos públicos excluem: conteúdo de `secrets`, `command_output`
  com redação aplicada (reusa `SecretRedactor`), eventos
  `internal/diagnostic`, endereços internos do sandbox
  (`agent_server_url`, tokens de preview).
- `streaming_delta` nunca publicado (efêmero); `command`/`observation`
  passam pelo redactor.

### RF-004 — Trajectory download

- `GET /api/v1/app-conversations/{id}/download` (auth normal) →
  `application/zip`: `events.jsonl` (todos, redacted), `metadata.json`
  (conversation, metrics, model, ticket/PR bindings), `workspace.diff`
  (git diff final), `summary.md` gerado.
- Streaming do zip (não materializa em disco); nome
  `openhandsnet-{id}-{slug}.zip`.
- Paridade CLI: `openhandsnet export` passa a consumir este endpoint.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| POST | `/api/v1/app-conversations/{id}/share` | → `{url,expires_at?}` |
| DELETE | `/api/v1/app-conversations/{id}/share` | revoga |
| GET | `/api/v1/shared/{token}` | público; metadados+page de eventos |
| GET | `/api/v1/shared/{token}/events` | público; cursor paging |
| GET | `/api/v1/app-conversations/{id}/download` | auth; `application/zip` |

Erros: `404` token inválido/expirado/revogado (indistinguível — não
enumerável) · `410` revogado (pode usar 404 também; escolha: 404 único)
· `429`.

## 4. Data model

`conversation_shares` (conversation_id FK, token_hash unique,
created_by, expires_at, revoked_at). Migration.

## 5. NFR / Security

- Token 256-bit aleatório; só hash armazenado; exibido 1× (como API key).
- Opt-out por org: `Shares:Enabled=false` bloqueia criação (admin).
- Filtro de eventos é camada separada e testada — regressão de leak
  = teste falho.
- Sem indexação (robots `noindex` na rota pública).

## 6. Task Plan

1. Entidade + endpoints share create/revoke/status + filtro público.
2. Rota `/share/{token}` no client (read-only renderer).
3. `download` zip (jsonl+metadata+diff+summary).
4. CLI export aponta p/ endpoint; docs.

## 7. Deliverables

Link público read-only com página dedicada; zip de trajetória por
API e CLI; filtros de redação garantidos por testes.

## 8. Referências

- `migration-plan/research/competitive-analysis-devin-openhands-cloud.md`.
- `.specs/34_streaming_deltas_artifacts_spec.md` (efêmero),
  `.specs/33_agent_hardening_spec.md` (`SecretRedactor`),
  `.specs/41_stream_chat_ux_spec.md` (renderers).

## 9. Guardrails

- Compartilhamento é só leitura; nenhuma mutação via token.
- Redação é defesa-em-profundidade: aplicar mesmo em campos já
  redacted no store.
- Download não inclui secret values nem `.git` objects internos de
  checkpoint (SPEC-43 usa git-dir separado — excluído).

## 10. DoD

- Share → página pública renderiza eventos filtrados; revogar → 404.
- Teste prova que um evento com secret nunca aparece no payload público.
- Zip contém os 4 artefatos; CLI `export` usa o endpoint.
- Migrations + golden OpenAPI verdes.

## 11. Delivered

- `Api/Endpoints/ShareEndpoints.cs` — share links públicos +
  download de trajectory.
- EF migration `spec56_conversation_shares`.
- PR: https://github.com/afonsoft/OpenHandsNET/pull/125
