# .specs/ — SPEC SDD do OpenHandsNET

SPECs (Spec-Driven Development) do port do ecossistema **OpenHands** para
**.NET 10 / Blazor WASM**, seguindo o workflow afonsoft (`.devin/skills/orchestrator`)
e o template de `afonsoft/open-webui`.

## Convenção

- Os 8 módulos seguem a numeração definida em
  `openhandsnet_full_specification_and_migration_guide.md` (raiz) (seção 8).
- Cada arquivo usa o template SPEC SDD: `0. Metadata` → `7. Task Plan`.
- `Status`: `Draft` → `Approved` → `Completed` (mover para `docs/specs/` ao entregar).
- Documentação em pt-BR; código/endpoints em inglês.
- **Regra de licença**: upstream `enterprise/` e `OpenHands-Cloud/` são Polyform —
  as SPECs reproduzem conceitos/contratos públicos apenas (ver
  `migration-plan/research/risk-assessment.md`).

## Índice / capability map

| SPEC | Domínio | Depende de |
|---|---|---|
| `01_architecture_overview.md` | Solução .NET, Clean Architecture, DI, hosting | — |
| `02_event_stream_spec.md` | Modelo de eventos V1 (`kind`, ULID, `parent_id`) + event store | 01 |
| `03_runtime_sandbox_spec.md` | `ISandboxService`, Docker.DotNet, agent-worker | 01 |
| `04_rest_and_signalr_api_spec.md` | API `/api/v1` (app-conversations, events, sandboxes…) + SignalR | 02, 03 |
| `05_devin_ui_integration_spec.md` | UI Blazor multi-pane estilo Devin (chat+terminal+browser+diff) | 04 |
| `06_cli_spec.md` | `openhandsnet` CLI (Spectre.Console): login, cloud, serve | 04 |
| `07_mcp_integration_spec.md` | MCP client + MCP OAuth + tool-call router | 04 |
| `08_enterprise_security_rbac.md` | OIDC, API keys `sk-*`, orgs, quota, billing, auditoria | 04 |

Build order: `01 → (02 ∥ 03) → 04 → (05 ∥ 06 ∥ 08) → 07`.

## Fluxo

`write-specs` → aprovação (gate) → `create-issues` → `execute-specs`/`tdd-spec`
→ `qa-analyst` → `code-review-and-quality` → PR/merge.

## Fontes de verdade

- `migration-plan/research/` — análise do upstream (evidência `file:line`).
- `docs/reference/openhands-upstream/` — extratos das docs oficiais.
- Baseline upstream: clones `OpenHands/*` (HEAD ~2026-10-12).
