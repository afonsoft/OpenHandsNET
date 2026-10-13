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
- `Status`: `Draft` → `Approved` → `Done` (mover para `docs/specs/` ao entregar).
- Documentação em pt-BR; código/endpoints em inglês.
- **Regra de licença**: upstream `enterprise/` e `OpenHands-Cloud/` são Polyform —
  as SPECs reproduzem conceitos/contratos públicos apenas (ver
  `migration-plan/research/risk-assessment.md`). Links para esses repos são
  sempre marcados "conceitos apenas".

## Índice / capability map

| SPEC | Domínio | Depende de | Issue |
|---|---|---|---|
| `01_architecture_overview.md` | Solução .NET, Clean Architecture, DI, hosting | — | [#2](https://github.com/afonsoft/OpenHandsNET/issues/2) |
| `02_event_stream_spec.md` | Modelo de eventos V1 (`kind`, ULID, `parent_id`) + event store | 01 | [#3](https://github.com/afonsoft/OpenHandsNET/issues/3) |
| `03_runtime_sandbox_spec.md` | `ISandboxService`, Docker.DotNet, workspace | 01 | [#4](https://github.com/afonsoft/OpenHandsNET/issues/4) |
| `04_rest_and_signalr_api_spec.md` | API `/api/v1` (app-conversations, events, sandboxes…) + SignalR | 02, 03 | [#5](https://github.com/afonsoft/OpenHandsNET/issues/5) |
| `05_devin_ui_integration_spec.md` | UI Blazor multi-pane estilo Devin (chat+terminal+browser+diff) | 04 | [#6](https://github.com/afonsoft/OpenHandsNET/issues/6) |
| `06_cli_spec.md` | `openhandsnet` CLI (Spectre.Console): login, cloud, serve | 04 | [#7](https://github.com/afonsoft/OpenHandsNET/issues/7) |
| `07_mcp_integration_spec.md` | MCP client + MCP OAuth + tool-call router | 04 | [#8](https://github.com/afonsoft/OpenHandsNET/issues/8) |
| `08_enterprise_security_rbac.md` | OIDC, API keys `sk-*`, orgs, quota, billing, auditoria | 04 | [#9](https://github.com/afonsoft/OpenHandsNET/issues/9) |
| `09_agent_engine_spec.md` | Loop ReAct: `IAgentLlmProvider` (SK), tools, confirmation mode | 02, 03 | [#10](https://github.com/afonsoft/OpenHandsNET/issues/10) |
| `10_persistence_efcore_spec.md` | DbContext único, schema, migrations SQLite/Postgres | 01 | [#11](https://github.com/afonsoft/OpenHandsNET/issues/11) |
| `11_skills_hooks_spec.md` | Skills (triggers/invoke) + hooks de ciclo de vida | 02, 10 | [#12](https://github.com/afonsoft/OpenHandsNET/issues/12) |
| `12_git_scm_integration_spec.md` | Clone, changes/diff/log, `git_commit`, credenciais | 03, 10 | [#13](https://github.com/afonsoft/OpenHandsNET/issues/13) |
| `13_devops_observability_spec.md` | Dockerfile, compose profiles, CI, Serilog+OTel | 01 | [#14](https://github.com/afonsoft/OpenHandsNET/issues/14) |
| `14_enterprise_operations_spec.md` | Sizing de sandbox, DinD+egress, PG externo, doctor/support-bundle, warm pool, usage | 03, 06, 08, 10, 13 | `[a criar]` |

Build order: `01 → (02 ∥ 03 ∥ 10 ∥ 13) → (04 ∥ 09 ∥ 11 ∥ 12) → (05 ∥ 06 ∥ 08) → 07 → 14`.

## Fluxo

`write-specs` → aprovação (gate) → `create-issues` → `execute-specs`/`tdd-spec`
→ `qa-analyst` → `code-review-and-quality` → PR/merge.

## Fontes de verdade

- `migration-plan/research/` — análise do upstream (evidência `file:line`).
- `docs/reference/openhands-upstream/` — extratos das docs oficiais
  (cloud-api, search-events OpenAPI, cli/local-setup).
- `docs/reference/upstream-baseline.md` — SHAs dos repos upstream
  (permalinks `blob/<sha>` usados nas SPECs).
- Baseline upstream: clones `OpenHands/*` (HEAD ~2026-10-12).
