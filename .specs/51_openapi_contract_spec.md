# SPEC — 51 · Public OpenAPI Contract (Spec + Snapshot + Docs)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `openapi-contract` |
| Type | `Feature / API + Tooling` |
| Stack | `.NET 10 · Microsoft.AspNetCore.OpenApi · golden snapshot` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Done` |
| Ticket | [#117](https://github.com/afonsoft/OpenHandsNET/issues/117) |
| Depends on | `04` (API surface — toda a superfície até 47) |
| Reference | upstream `docs.openhands.dev` OpenAPI (search-events extract já em `docs/reference/openhands-upstream/`) |
| Touches | `src/OpenHandsNET.Api` (Program + endpoint metadata), `tests/` (snapshot), `docs/api/` |

## 1. User Story

**As a** consumidor externo da API do OpenHandsNET (CLI próprio,
integrações, avaliadores)
**I want** um documento OpenAPI gerado do código vivo + um snapshot
golden que detecta drift de contrato em PR
**So that** a superfície `/api/v1` (≈30 grupos de endpoints até
SPEC-47) é documentável e estável — hoje não existe `MapOpenApi`/
swagger; o único contrato formal são os golden fixtures de eventos
(V1).

**Contexto:** Minimal APIs geram OpenAPI via
`Microsoft.AspNetCore.OpenApi` (nativo .NET 10, sem dependência
nova). Complemento: snapshot golden da spec normalizada (drift guard
igual ao event-contract), docs de referência e UI de exploração
opcional (Scalar — mais leve que SwaggerUI e nativo ao pacote).

## 2. Scope (RF)

### RF-001 — Documento OpenAPI gerado

- `builder.Services.AddOpenApi()` + `app.MapOpenApi("/openapi/{documentName}.json")`
  — documento `v1` cobrindo todos os `MapGroup("/api/v1/*")`.
- Metadados por endpoint onde faltam: `.WithSummary`, `.WithDescription`,
  `.Produces<T>`/`Accepts<T>` nos handlers principais (sweep dos grupos
  existentes — mínimo: summary 1 linha por endpoint).
- Internal/hub-only: `MapHub` e endpoints runtime-agent não entram
  (tag ou exclusão por path `/api/internal/*`).

### RF-002 — Golden snapshot de contrato

- Teste de integração: `GET /openapi/v1.json` → normaliza (ordena
  paths, remove servers/version noise) → compara com
  `tests/golden/openapi-v1.json` (mesmo padrão do event-contract).
- Drift = fail com diff legível; refresh via env
  `OPENHANDSNET_UPDATE_GOLDEN=1` (mesmo mecanismo existente ou novo
  equivalente documentado).

### RF-003 — Explorer + docs

- `app.MapScalarApiReference()` (ou equivalente) atrás de
  `Api:OpenApiExplorer=true` (default true em Development, false em
  produção — opt-in).
- `docs/api/README.md` (novo índice): link p/ openapi.json + como
  gerar client (`openapi-generator`, `kiota`).

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| GET | `/openapi/v1.json` | documento gerado |
| GET | `/scalar/v1` | explorer (opt-in) |

## 4. Data model

Nenhum.

## 5. NFR / Security

- Explorer off por default em produção; o JSON em si é a contract
  público — não vaza nada além do que endpoints já aceitam.
- Nenhum schema com secret em exemplo (descriptions auditadas).

## 6. Task Plan

1. `AddOpenApi` + `MapOpenApi` + tags — build.
2. Sweep de `WithSummary`/`Produces` nos endpoints (batch por arquivo).
3. Golden snapshot test + mecanismo de refresh.
4. Explorer opt-in + `docs/api/README.md`.

## 7. Deliverables

`/openapi/v1.json` vivo, snapshot golden + drift guard, explorer
opt-in, docs de consumo.

## 8. Referências

- `docs/reference/openhands-upstream/search-events-openapi.md` —
  baseline de formato upstream.
- Microsoft Learn: `Microsoft.AspNetCore.OpenApi` (.NET 10),
  Scalar.AspNetCore.

## 9. Guardrails

- Sem dependência nova além do pacote nativo (+ Scalar opcional —
  versão fixa ≥7 dias).
- Snapshot é contrato: qualquer endpoint novo/removido altera o diff
  — revisão humana obrigatória no refresh (documentado no test).

## 10. DoD

- `/openapi/v1.json` responde com paths/schemas dos endpoints atuais.
- Snapshot detecta endpoint novo/removido (teste do teste: alterar
  rota quebra o golden).
- Explorer só sobe com flag.
- Suite verde; SPEC `Done`; docs atualizadas.
