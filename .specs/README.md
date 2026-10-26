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
| `70_sonar_vulnerabilities_spec.md` | SonarCloud — 52 vulnerabilities (16 regras) | `Completed` | PRs #159/#160 · #157 (Epic #152) |
| `71_sonar_bugs_spec.md` | SonarCloud — 32 bugs (7 regras) | `Completed` | PRs #159/#160 · #153 (Epic #152) |
| `72_sonar_critical_code_smells_spec.md` | SonarCloud — 121 smells BLOCKER/CRITICAL | `Approved` | #154 (Epic #152) |
| `73_sonar_major_code_smells_spec.md` | SonarCloud — 243 smells MAJOR | `Approved` | #155 (Epic #152) |
| `74_sonar_minor_code_smells_spec.md` | SonarCloud — 331 smells MINOR/INFO | `Approved` | #156 (Epic #152) |
| `89_sonar_deferred_refactors_spec.md` | Sonar — defer Lote D (S3776/S107/S3903/S1075) | `Draft` | gap-analysis 2026-10-22 |
| `92_client_drift_guards_spec.md` | Client/CI — drift guards (css-classes, form-a11y, i18n parity/placeholders) | `Completed` | PR #241 · #235 (Epic #231) |
| `114_light_theme_ui_refresh_spec.md` | Client — default light theme + UI/UX refresh (Open-WebUI/Devin look) | `Draft` | pedido usuário 2026-10-26 |
| `115_sandbox_access_proxy_hardening_spec.md` | Segurança — sandbox alcançável só via API proxy (contrato/testes/docs) | `Draft` | pedido usuário 2026-10-26 |
| `93_perf_loadtest_harness_spec.md` | Tooling — mock LLM OpenAI-compat + loadtest (rps/conversations/subagents) | `Completed` | PR #242 · #236 (Epic #231) |
| `94_wasm_boot_resilience_spec.md` | Infra — boot.js autostart=false + `/framework-assets` mirror (proxies corporativos) | `Completed` | PR #243 · #237 (Epic #231) |
| `95_declarative_agent_catalog_spec.md` | Agent runtime — catálogo `*.agent.md` em camadas + `agent_catalog` tool (dep: SPEC-90) | `Completed` | PR #240 · #234 (Epic #231) |

## Arquivo morto — `docs/specs/`

SPECs entregues ficam em [`docs/specs/`](../docs/specs/README.md), com
`Status: Completed` e evidência de entrega (PR/commit) no índice —
incluindo 65/66/67 (2026-10-19), 75–78 (2026-10-20) e 83–87 (2026-10-22).
Guard: `scripts/check-spec-lifecycle.sh` falha se uma SPEC com status
terminal permanecer aqui (SPEC-82).

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

### Leva 2026-10-23 (gap-analysis)

| SPEC | Título | Status | Ticket |
|---|---|---|---|
| 96 | sonar-rescan-truth | Completed → docs/specs | #248 (verif. exclusões) |
| 97 | fuzz-findings | Completed → docs/specs | #218 (verif. nightly) |
| 98 | i18n-locale-slices | Completed → docs/specs | #250/#244 |
| 99 | spec89-deferred-split | Completed → docs/specs | gap-20261023 |
| 100 | epic-hygiene | Completed → docs/specs | gap-20261023 |

### Filhos SPEC-89 (defer Lote D)

| SPEC | Título | Status | Issue |
|---|---|---|---|
| 101 | s3776-cli-cognitive | Completed → docs/specs | PR #263 |
| 102 | s3776-infra-agent | Completed → docs/specs | PR #264 |
| 103 | s3776-endpoints | Approved | Epic #152 |
| 104 | s107-many-params | Approved | Epic #152 |
| 105 | s3218-s127-shadow-loop | Approved | Epic #152 |
| 106 | s1075-s3903-uris-ns | Approved | Epic #152 |
| 113 | desktop-surface | Approved | roadmap Devin-parity |

### Leva 2026-10-24 (gap-analysis — Epic #265) — entregue

SPECs 107–110 entregues via PRs #271–#274 e arquivadas em `docs/specs/`.
