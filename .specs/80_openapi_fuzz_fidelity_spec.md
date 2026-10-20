# SPEC-80: OpenAPI fuzz fidelity — nightly schemathesis verde

| Campo | Valor |
|---|---|
| Status | Approved |
| Gap key | GAP-automation-openapi-fidelity |
| Type | Quality/Automation |
| Fonte | nightly run `39396545419` + issue #203 (`nightly-red`) + golden `tests/OpenHandsNET.IntegrationTests/Golden/openapi-v1.json` |

## User Story

Como mantenedor, quero o gate nightly `OpenAPI fuzz (schemathesis)`
verde — hoje falha por divergências do spec gerado (SPEC-51 entregou o
contrato + snapshot, mas o snapshot não valida semântica).

## Contexto / evidência

Run `39396545419` (2026-10-20, SHA `18805ac`): **237 failed + 8 errors**.
Classes de falha com evidência:

1. **Path params não declarados** — 6 paths no golden spec têm
   `{org_id}`/`{id}` no template sem a definição do parâmetro
   (orgs chat/ticket-connections ×4 — coberto pela SPEC-79; sandboxes
   `/{id}/settings/secrets{,/{name}}` ×2 — handlers `()`/`(name)` não
   recebem `id`, `SandboxEndpoints.cs:123-128`).
2. **Sem segurança documentada** — `components.securitySchemes: []`
   e **0/264 ops** declaram `401`, mas a API exige auth (API key/Bearer)
   → `Undocumented HTTP status code: 401` em massa.
3. **`404` vs `405`** em método não suportado — minimal API devolve
   404 para método errado; check `unsupported_method` espera 405.
4. **`POST /app-conversations/stream-start`** — `Read timed out`
   (endpoint SSE/streaming inadequado para fuzz).
5. **Crash do schemathesis 4.3.8** — `TypeError: unhashable type:
   'list'` na fase coverage (`generation/coverage.py:290`) →
   7 errors / 257 skipped. Bug da ferramenta, não do schema.

## Requisitos funcionais

1. **RF-001 — Path params**: declarar `id` nos handlers stub de
   `settings/secrets` (os de orgs vêm pela SPEC-79) + guard: teste de
   contrato que falha se path template tiver `{param}` não declarado.
2. **RF-002 — Auth documentada**: `securityScheme` (ApiKey header ou
   Bearer) no documento + `401` (e `403` onde aplicável) nas respostas
   das operações autenticadas — via `DocumentTransformer`/`Produces`.
3. **RF-003 — Fuzz scope**: excluir `stream-start` (e outros endpoints
   streaming) do run via `--exclude-path`/filtro; documentar o motivo.
4. **RF-004 — Check 405**: decidir e implementar — (a) middleware que
   retorna 405 em rota conhecida/método errado, ou (b) `--exclude-checks
   unsupported_method` com justificativa registrada na SPEC.
5. **RF-005 — Pin schemathesis**: fixar versão que não crashe
   (ex.: versão anterior estável validada) ou `--phases` sem coverage;
   workflow `.github/workflows/nightly-quality.yml` — revisão humana.
6. **RF-006 — Regenerar golden** após as mudanças de spec.

## Critérios de aceite

- [ ] `schemathesis run` verde no nightly (0 failures tolerados).
- [ ] Golden spec sem path param não declarado; securityScheme presente.
- [ ] Issue #203 fechada com o run verde referenciado.
