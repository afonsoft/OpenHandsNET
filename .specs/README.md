# .specs/ — SPEC SDD do OpenHandsNET

SPECs (Spec-Driven Development) do port do ecossistema **OpenHands** para
**.NET 10 / Blazor WASM**, seguindo o workflow afonsoft (`write-specs` →
`create-issues` → `execute-specs`) e o template SDD de `afonsoft/open-webui`.

## Convenção

- Os módulos 01–08 seguem a numeração definida em
  `openhandsnet_full_specification_and_migration_guide.md` (raiz) (seção 8);
  09–13 cobrem lacunas identificadas para execução autônoma (Devin).
- Cada arquivo: `0. Metadata` → `7. Task Plan` + `8. Deliverables` +
  `9. Referências` + `10. Guardrails` + `11. DoD`.
- `Status`: `Draft` → `Approved` → `Done` → **mover para `docs/specs/`**
  ao entregar (arquivo morto, `Status: Completed`). Apenas SPECs ativas
  (`Draft`/`Approved`/`In implementation`) permanecem aqui.
- Numeração única `NN_name_spec.md`; em colisão, renumera-se para o próximo
  número livre e registra-se a origem (ver 68/69 abaixo).
- Documentação em pt-BR; código/endpoints em inglês.
- **Regra de licença**: upstream `enterprise/` e `OpenHands-Cloud/` são Polyform —
  as SPECs reproduzem conceitos/contratos públicos apenas (ver
  `migration-plan/research/risk-assessment.md`). Links para esses repos são
  sempre marcados "conceitos apenas".

## Índice — SPECs ativas

| SPEC | Domínio | Status | Issue |
|---|---|---|---|

## Arquivo morto — `docs/specs/`

SPECs **01–69** entregues (69 documentos) estão em
[`docs/specs/`](../docs/specs/README.md), com `Status: Completed` e
evidência de entrega (PR/commit) no índice — incluindo 65/66/67
(arquivadas em 2026-10-19).

> **Renumbering (2026-10-19)**: `52_nextgen_improvements` → **68** e
> `53_enterprise_sandbox_hardening` → **69** para resolver colisões com
> `52_github_app_scm` e `53_scm_providers`. Referências de código foram
> atualizadas (`SPEC-53`→`SPEC-69` nos pontos enterprise).

## Fluxo

`write-specs` → aprovação (gate) → `create-issues` → `execute-specs`/`tdd-spec`
→ `qa-analyst` → `code-review-and-quality` → PR/merge → arquivar em
`docs/specs/`.

## Fontes de verdade

- `migration-plan/research/` — análise do upstream (evidência `file:line`).
- `docs/reference/openhands-upstream/` — extratos das docs oficiais
  (cloud-api, search-events OpenAPI, cli/local-setup).
- `docs/reference/upstream-baseline.md` — SHAs dos repos upstream
  (permalinks `blob/<sha>` usados nas SPECs).
- Baseline upstream: clones `OpenHands/*` (HEAD ~2026-10-12).
