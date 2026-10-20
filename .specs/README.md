# .specs/ — SPECs SDD/ADD do OpenHandsNET

SPECs (Spec-Driven Development) do port do ecossistema **OpenHands** para
**.NET 10 / Blazor WASM**, seguindo o workflow afonsoft (`write-specs` →
`create-issues` → `execute-specs`) e o template SDD de `afonsoft/open-webui`.

O projeto opera em **AI-Driven Development (ADD)**: as SPECs SDD são o
artefato-fonte de verdade, escritas *para* agentes e executadas *por*
agentes — `execute-specs` é o caminho primário, com revisão humana nos
gates (aprovação de SPEC, merge).

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
| `70_sonar_vulnerabilities_spec.md` | SonarCloud — 52 vulnerabilities (16 regras) | `Approved` | #157 (Epic #152) |
| `71_sonar_bugs_spec.md` | SonarCloud — 32 bugs (7 regras) | `Approved` | #153 (Epic #152) |
| `72_sonar_critical_code_smells_spec.md` | SonarCloud — 121 smells BLOCKER/CRITICAL | `Approved` | #154 (Epic #152) |
| `73_sonar_major_code_smells_spec.md` | SonarCloud — 243 smells MAJOR | `Approved` | #155 (Epic #152) |
| `74_sonar_minor_code_smells_spec.md` | SonarCloud — 331 smells MINOR/INFO | `Approved` | #156 (Epic #152) |
| `75_layout_shell_mobile_a11y_spec.md` | Layout shell — mobile drawer, rail, a11y | `Done` | #193 (Epic #192) |
| `76_chat_run_ux_spec.md` | Chat run UX — status pill, git chip, sub-conversas | `Done` | #194 (Epic #192) |
| `77_ide_workbench_deepen_spec.md` | IDE workbench — VS Code web no sandbox | `Done` | #195 (Epic #192) |
| `78_integration_test_hardening_spec.md` | Integration tests — hardening flakes CI | `Done` | #199 |
| `79_org_scoped_resources_spec.md` | Segurança — org_id escopa queries | `Approved` | — |
| `80_openapi_fuzz_fidelity_spec.md` | OpenAPI fuzz — nightly schemathesis verde | `Approved` | — |
| `81_k8s_e2e_kind_cluster_spec.md` | k8s-e2e — nome do cluster kind | `Approved` | — |
| `82_spec_lifecycle_hygiene_spec.md` | Spec lifecycle — arquivar + fechar slices | `Approved` | — |

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
