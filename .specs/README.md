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
| `14_enterprise_operations_spec.md` | Sizing de sandbox, DinD+egress, PG externo, doctor/support-bundle, warm pool, usage | 03, 06, 08, 10, 13 | [#29](https://github.com/afonsoft/OpenHandsNET/issues/29) |
| `15_ui_mobile_and_smart_autonomy_spec.md` | Mobile UX (drawer, repo sem owner), autonomia smart/always-allow, repasse de credencial GH ao sandbox | 03, 04, 09, 12 | [#33](https://github.com/afonsoft/OpenHandsNET/issues/33) |
| `16_persistent_sandbox_lifecycle_spec.md` | Sleep/wake (pause·stop·resume), wake-on-message, lease por atividade, GC de workspace | 03, 10, 14 | [#36](https://github.com/afonsoft/OpenHandsNET/issues/36) |
| `17_runtime_image_blueprint_spec.md` | Imagem Ubuntu própria, sandbox-agent PID 1 (HTTP, deferred-init), blueprint `openhandsnet.yaml` + `.openhands/setup.sh`, SDK auto-detect, warm pool por imagem | 03, 12, 14, 16 | [#37](https://github.com/afonsoft/OpenHandsNET/issues/37) |
| `18_browser_tool_and_switching_spec.md` | Browser tool (Playwright no worker, screenshots→BrowserTab) + `switch_profile`/`switch_acp_model` reais | 03, 04, 09, 14 | [#42](https://github.com/afonsoft/OpenHandsNET/issues/42) ✅ |
| `19_egress_domain_allowlist_spec.md` | Egress `allowlist` real por domínio via proxy sidecar Squid dual-homed | 14 | [#43](https://github.com/afonsoft/OpenHandsNET/issues/43) |
| `20_plan_enforcement_billing_spec.md` | Enforcement de planos (conversas/dia, runs concorrentes, tokens/mês, sandbox-s/mês) + write-path de usage metering + plans CRUD admin | 08, 14 | [#44](https://github.com/afonsoft/OpenHandsNET/issues/44) |
| `21_client_completion_spec.md` | Admin UI (users/admins/audit/flags/plans/usage/support-bundle), Monaco (files+diff), pipeline Tailwind v4 | 05, 08, 14, 20 | [#45](https://github.com/afonsoft/OpenHandsNET/issues/45) ✅ |
| `22_context_engine_compaction_spec.md` | Context engine: `IContextMeter`, `LlmSummarizingCondenser`, pruning, triggers (threshold/mid-turn/overflow/manual), ledger + promotion | 02, 09 | [#51](https://github.com/afonsoft/OpenHandsNET/issues/51) ✅ #57 |
| `23_omniroute_model_catalog_routing_spec.md` | OmniRoute `/models` catalog, `IModelRouter` por role, retry/fallback/turn recovery, `llm_route` | 09, 22 | [#52](https://github.com/afonsoft/OpenHandsNET/issues/52) ✅ #58 |
| `24_agent_activity_protocol_spec.md` | Sandbox-agent v2 (`/exec/stream`, `/proc`, `/activity`), kinds `command_output`/`tool_progress`/`files_changed`/`run_progress`/`work_summary`/`agent_diagnostic` | 03, 17, 02 | [#53](https://github.com/afonsoft/OpenHandsNET/issues/53) ✅ #59 |
| `25_tools_skills_discovery_spec.md` | Repo discovery (`AGENTS.md`, `.openhands/skills`…), repo skills federadas, tools `search`/`file_read` v2/`file_edit` modes/`web_fetch`, MCP schema fidelity | 07, 09, 11, 24 | [#54](https://github.com/afonsoft/OpenHandsNET/issues/54) ✅ #60 |
| `26_ui_ux_modernization_spec.md` | Temas dark/light/system, icon set SVG, markdown+ToolCallCard, command palette, notificações, responsivo (ref: open-webui fork) | 05, 21, 24 | [#55](https://github.com/afonsoft/OpenHandsNET/issues/55) ✅ #73 |
| `27_cli_opencode_comparative_spec.md` | Análise comparativa CLI/agent-harness (opencode e pares) — backlog que originou 28–31 | 06, 09, 11, 22–25 | `TBD` (análise) |
| `28_subagents_task_tool_spec.md` | Orquestração de subagentes via tool `task` (fork/merge de contexto) | 09, 22–25, 29 | #61 ✅ #70 |
| `29_tool_permissions_spec.md` | Permissões por tool `allow`/`deny`/`ask` + plan mode | 09, 25 | #62 ✅ #67 |
| `30_centralized_memory_spec.md` | Memória centralizada (escopos org + projeto) | 22, 25, 29 | #63 ✅ #68 |
| `31_cli_parity_spec.md` | Paridade CLI: models/resume/stats/mcp/skills/export/attach/doctor preflight | 06, 20, 23–25 | #64 ✅ #72 |
| `32_upstream_enterprise_gap_analysis.md` | Gap analysis vs upstream (Enterprise/Cloud/Agent-SDK) — origem de 33–39 | 27 | `TBD` (análise) |
| `33_agent_hardening_spec.md` | `ISecurityAnalyzer` plugável, secrets by-ref, resume validation (`agent_contract`) | 09, 22, 23, 29 | #65 ✅ #69 |
| `34_streaming_deltas_artifacts_spec.md` | `streaming_delta` efêmero (batcher+hub) + `IArtifactStore` workspace/S3 | 02, 24, 25 | #66 ✅ #71 |
| `35_runtime_api_split_spec.md` | Runtime-API split: `ISandboxEngine` local/remote + `openhandsnet-runtime-api` | 03, 14, 16, 17 | #74 ✅ #79 |
| `36_distributed_cache_backplane_spec.md` | `IDistributedCache` Redis/Valkey + backplane SignalR + `ICoordinationBus`/`IDistributedCounter` (consumidores pendentes) | 04, 13, 14, 34 | #75 ◐ #79 |
| `37_kubernetes_runtime_spec.md` | `KubernetesSandboxEngine` (pods+Sysbox+PVC); Helm chart e doctor probe pendentes | 03, 14, 16, 17, 35 | #76 ◐ #79 |
| `38_sandbox_host_routing_spec.md` | Routing `path\|host` (`{id}-{port}.{domain}`) p/ surfaces do sandbox | 03, 04, 14, 35/37 | #77 ✅ #79 |
| `39_eval_harness_spec.md` | `openhandsnet eval` — runner headless + fixtures + scoring (parcial) | 06/31, 09, 22, 24, 28 | #78 ◐ #79 |
| `40_sandbox_agent_server_surfaces_spec.md` | Surfaces do agent-server: gateway `/v1/chat/completions`, terminal/VSCode web, preview tokens, ACP mínimo | 03, 04, 08, 17, 24, 34, 38 | #81 |


Build order: `01 → (02 ∥ 03 ∥ 10 ∥ 13) → (04 ∥ 09 ∥ 11 ∥ 12) → (05 ∥ 06 ∥ 08) → 07 → 14 → 15 → 16 → 17 → (18 ∥ 19 ∥ 20) → 21 → (22 → 23 ∥ 24) → 25 → (26 ∥ 27) → (29 → (28 ∥ 33) ∥ 30 ∥ 31 ∥ 34)`.

Enterprise wave: `(35 → (36 ∥ 37 ∥ 38) ∥ 39) → 40` — entregue parcialmente
no PR #79 (35/38 Done; 36/37/39 com pendências nos Status); 40 fecha os
surfaces user-facing do agent-server.

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
