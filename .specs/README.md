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
| `89_sonar_deferred_refactors_spec.md` | Sonar — defer Lote D (S3776/S107/S3903/S1075) | `Draft` | gap-analysis 2026-10-22 |
| `90_system_prompt_contributors_spec.md` | Agent runtime — pipeline componível de system prompt (contributors + tiers T1-T3 + SDD + delegação) | `Approved` | #233 (Epic #231) |
| `92_client_drift_guards_spec.md` | Client/CI — drift guards (css-classes, form-a11y, i18n parity/placeholders) | `Approved` | #235 (Epic #231) |
| `93_perf_loadtest_harness_spec.md` | Tooling — mock LLM OpenAI-compat + loadtest (rps/conversations/subagents) | `Approved` | #236 (Epic #231) |
| `94_wasm_boot_resilience_spec.md` | Infra — boot.js autostart=false + `/framework-assets` mirror (proxies corporativos) | `Approved` | #237 (Epic #231) |
| `95_declarative_agent_catalog_spec.md` | Agent runtime — catálogo `*.agent.md` em camadas + `agent_catalog` tool (dep: SPEC-90) | `Approved` | #234 (Epic #231) |

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
