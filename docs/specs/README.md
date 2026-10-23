# docs/specs/ — SPECs entregues (arquivo)

SPECs SDD concluídas e arquivadas, conforme convenção `.specs/README.md`
(`Done`/`Delivered` → mover para `docs/specs/` com `Status: Completed`).
Arquivado em 2026-10-19 pela SPEC-65 (gap-analysis).

| SPEC | Título | Entrega (PR/commit) |
|---|---|---|
| `01_architecture_overview` | SPEC — 01 · Architecture Overview | #15 |
| `02_event_stream_spec` | SPEC — 02 · Event Stream | #16 |
| `03_runtime_sandbox_spec` | SPEC — 03 · Runtime & Sandbox | #17 |
| `04_rest_and_signalr_api_spec` | SPEC — 04 · REST API & SignalR | #18 |
| `05_devin_ui_integration_spec` | SPEC — 05 · Devin-Style UI (Blazor WASM) | 71fea84 |
| `06_cli_spec` | SPEC — 06 · OpenHandsNET CLI | 8984132 |
| `07_mcp_integration_spec` | SPEC — 07 · MCP Integration (Model Context Protocol) | 3407b6d |
| `08_enterprise_security_rbac` | SPEC — 08 · Enterprise: Security, Auth, Orgs, Quota & Audit | becf690 |
| `09_agent_engine_spec` | SPEC — 09 · Agent Engine (LLM Loop & Tools) | #19 |
| `10_persistence_efcore_spec` | SPEC — 10 · Persistência EF Core (Schema & Migrations) | #20 |
| `11_skills_hooks_spec` | SPEC — 11 · Skills & Hooks | #22 |
| `12_git_scm_integration_spec` | SPEC — 12 · Git & Source-Control Integration | #21 |
| `13_devops_observability_spec` | SPEC — 13 · DevOps, Deployment & Observability | d80f536 |
| `14_enterprise_operations_spec` | SPEC — 14 · Enterprise Operations & Sandbox Hardening | d488336 |
| `15_ui_mobile_and_smart_autonomy_spec` | SPEC — 15 · Mobile UX + Smart Autonomy + Credential Forwarding | f112987 |
| `16_persistent_sandbox_lifecycle_spec` | SPEC — 16 · Persistent Sandbox Lifecycle (sleep / wake / GC) | 65acb49 |
| `17_runtime_image_blueprint_spec` | SPEC — 17 · Runtime Image (Ubuntu) + Blueprint Provisioning + Sandb | #40 |
| `18_browser_tool_and_switching_spec` | SPEC — 18 · Browser Tool (Playwright) + Conversation Switching | #42-45 |
| `19_egress_domain_allowlist_spec` | SPEC — 19 · Egress Domain Allowlist (proxy sidecar) | #47 |
| `20_plan_enforcement_billing_spec` | SPEC — 20 · Plan Enforcement & Usage Metering | #48 |
| `21_client_completion_spec` | SPEC — 21 · Client Completion (Admin UI, Monaco, Tailwind v4) | #49 |
| `22_context_engine_compaction_spec` | SPEC — 22 · Context Engine, Compaction & Resumable Snapshots | #56 |
| `23_omniroute_model_catalog_routing_spec` | SPEC — 23 · OmniRoute Model Catalog, Multi-Model Routing & Turn Rec | #58 |
| `24_agent_activity_protocol_spec` | SPEC — 24 · Sandbox-Agent Activity Protocol & Run Observability | #59 |
| `25_tools_skills_discovery_spec` | SPEC — 25 · Tool Surface, Repo Discovery & Skill Federation | #60 |
| `26_ui_ux_modernization_spec` | SPEC — 26 · UI/UX Modernization (Themes, Icons, Chat, Command Surfa | #73 |
| `27_cli_opencode_comparative_spec` | SPEC — 27 · CLI & Agent-Harness Comparative (opencode + peers) | pesquisa |
| `28_subagents_task_tool_spec` | SPEC — 28 · Subagent Orchestration (`task` tool) | #70 |
| `29_tool_permissions_spec` | SPEC — 29 · Tool Permissions (`allow`/`deny`/`ask`) & Plan Mode | #67 |
| `30_centralized_memory_spec` | SPEC — 30 · Centralized Memory (org + project) | #68 |
| `31_cli_parity_spec` | SPEC — 31 · CLI Parity (models / resume / stats / mcp / skills / ex | #72 |
| `32_upstream_enterprise_gap_analysis` | SPEC — 32 · Upstream Gap Analysis (Enterprise / Cloud / Agent-SDK) | pesquisa |
| `33_agent_hardening_spec` | SPEC — 33 · Agent Hardening (SecurityAnalyzer + by-ref secrets + re | #69 |
| `34_streaming_deltas_artifacts_spec` | SPEC — 34 · Streaming Deltas + External Artifact Store | #71 |
| `35_runtime_api_split_spec` | SPEC — 35 · Runtime-API Split (sandbox orchestration out-of-process | 85dcc05 |
| `36_distributed_cache_backplane_spec` | SPEC — 36 · Distributed Cache + SignalR Backplane (Valkey/Redis) | #83/#84 |
| `37_kubernetes_runtime_spec` | SPEC — 37 · Kubernetes Runtime Provider (pods + Sysbox + PVC) | #79 + #120 |
| `38_sandbox_host_routing_spec` | SPEC — 38 · Sandbox Host Routing (subdomain | path mode) | 85dcc05 |
| `39_eval_harness_spec` | SPEC — 39 · Eval Harness (`openhandsnet eval`) | #85 |
| `40_sandbox_agent_server_surfaces_spec` | SPEC — 40 · Agent Server Surfaces (OpenAI Gateway + User-Facing San | #87-#98 |
| `41_stream_chat_ux_spec` | SPEC — 41 · Stream/Chat UX (Reasoning, Watchdog, Tool Cards, Copy/R | #99/#107 |
| `42_ide_workspace_spec` | SPEC — 42 · IDE Workspace Tab (Explorer + Monaco + SCM + Search + T | #108 |
| `43_checkpoints_revert_spec` | SPEC — 43 · Checkpoints + Revert (Workdir Snapshots) | #101/#109 |
| `44_attention_inbox_spec` | SPEC — 44 · Attention Inbox (Conversas Aguardando Você) | #110 |
| `45_composer_spec` | SPEC — 45 · Composer (Mentions, Slash Commands, Drafts, Uploads) | #111 |
| `46_i18n_spec` | SPEC — 46 · i18n (JSON Locales + LocalizationService) | #112 |
| `47_automations_spec` | SPEC — 47 · Automations (Schedules + Hook Triggers + Routine Tool) | #113 |
| `48_enterprise_security_hardening_spec` | SPEC — 48 · Enterprise Security Hardening (Org Policies + Resume St | #118/#119 |
| `49_kubernetes_runtime_completion_spec` | SPEC — 49 · Kubernetes Runtime Completion (Warm Pool + Sleep/Wake + | #120 |
| `50_byor_llm_spec` | SPEC — 50 · BYOR LiteLLM (Bring-Your-Own-Router) | #121 |
| `51_openapi_contract_spec` | SPEC — 51 · Public OpenAPI Contract (Spec + Snapshot + Docs) | #122 |
| `52_github_app_scm_spec` | SPEC — 52 · GitHub App + SCM Provider Abstraction | #126 |
| `53_scm_providers_spec` | SPEC — 53 · SCM Providers: GitLab, Bitbucket, Azure DevOps | wave B/C |
| `54_chatops_slack_spec` | SPEC — 54 · ChatOps: Slack App (mention, slash, thread sync) | wave B/C |
| `55_ticketing_triggers_spec` | SPEC — 55 · Ticketing Triggers: Linear + Jira → Conversas | #129 |
| `56_public_share_download_spec` | SPEC — 56 · Public Share + Trajectory Download | #125 |
| `57_service_users_spec` | SPEC — 57 · Service Users + Scoped Keys + Impersonation | #130 |
| `58_scim_idp_groups_spec` | SPEC — 58 · SCIM 2.0 + IdP Group → Org/Role Mapping + Usage Tiers | #131 |
| `59_edge_security_hardening_spec` | SPEC — 59 · Edge Security: IP Allowlist + Input Guard + Per-Actor R | #133 |
| `60_repo_indexing_ask_mode_spec` | SPEC — 60 · Repo Indexing + Ask Mode (read-only Q&A) | #136 |
| `61_session_insights_spec` | SPEC — 61 · Session Insights (post-run analysis) | #137 |
| `62_managed_conversations_spec` | SPEC — 62 · Managed Conversations (`delegate` tool + coordinator wa | #138 |
| `63_knowledge_pipeline_spec` | SPEC — 63 · Knowledge Pipeline (suggestions + review + dedup) | #140 |
| `64_browser_profiles_spec` | SPEC — 64 · Browser Auth Profiles (storage state + site cookies/TOT | #141 |
| `68_nextgen_improvements_spec` | Devin Web & Upstream OpenHands Next-Gen Improvements | #123/#124 |
| `69_enterprise_sandbox_hardening_spec` | SPEC-69: Enterprise Sandbox Hardening, Live Handover & Session Time-Tr | #134 |

## Entregues em 2026-10-19 (gap-analysis 2026-10-19 / Epic #146)

| SPEC | Título | Entrega | Issue |
|---|---|---|---|
| 65 | Specs hygiene | `0cd88c5` — arquivo `docs/specs/`, renumbering 68/69, índices | #147 |
| 66 | Quality gates | `dba6ea0` — `.editorconfig`, lint gate CI, job sonarcloud gated, badges | #148 |
| 67 | Harness structure | estrutura `.claude/` completa + `docs/architecture/` + CLAUDE.md seções | #149 |

## Entregues em 2026-10-20 (gap-analysis 2026-10-19 layout / Epic #192 + SPEC-78)

| SPEC | Título | Entrega | Issue |
|---|---|---|---|
| 75 | Layout shell — mobile drawer, rail, a11y | PR #196 | #193 |
| 76 | Chat run UX — status pill, git chip, sub-conversas | PRs #196/#197 | #194 |
| 77 | IDE workbench — VS Code web no sandbox | PR #197 | #195 |
| 78 | Integration tests — hardening flakes CI | PRs #201/#204 | #199 |

## Entregues em 2026-10-20 (gap-analysis 2026-10-20 / Epic #206)

| SPEC | Título | Entrega | Issue |
|---|---|---|---|
| 79 | Org-scoped resources — `org_id` escopa queries | PR #212 | #207 |
| 80 | OpenAPI fuzz fidelity — security/params/401 | PR #215 | #208 |
| 81 | k8s-e2e — `cluster_name: kind` | PR #213 | #209 |
| 82 | Spec lifecycle hygiene — arquivo + guard | PR #214 | #210 |

## Entregues em 2026-10-22 (gap-analysis 2026-10-22 — docs enterprise upstream)

| SPEC | Título | Entrega |
|---|---|---|
| 83 | Troubleshooting runbook — doc operacional (support bundle, componente→sintoma) | PR #226 |
| 84 | Deployment sizing — peak sandboxes → VM/node pools | PR #226 |
| 85 | External PostgreSQL — PG ≥16.4 + UTF8 + divergência topologia | PR #226 |
| 86 | `Sandbox:DockerRuntime` — sysbox-runc fora de k8s (HostConfig.Runtime + testes) | PR #226 |
| 87 | Helm image loader — DaemonSet pre-pull opt-in | PR #226 |
| 88 | Conversation cleanup — remove container + PVC ao excluir/arquivar | PR #228 |

## Entregues em 2026-10-22 (análise `afonsoft/open-webui` — Epic #231)

| SPEC | Título | Entrega |
|---|---|---|
| 91 | Untrusted-input envelope — `<untrusted_input>` em prompts SCM/tickets/chatops | PR #238 |
| 90 | Composable system-prompt contributors — T1/T2/T3, SDD surface, delegation policy | PR #239 |
| 95 | Declarative agent catalog — `*.agent.md` em camadas + `agent_catalog` tool | PR #240 |
| 93 | Perf/loadtest harness — mock LLM OpenAI-compat + rps/conversations/subagents | PR #242 |
| 94 | WASM boot resilience — `boot.js` + `/framework-assets` mirror (proxies corporativos) | PR #243 |
| 92 | Client drift guards — css-classes, form-a11y, i18n parity/placeholders + baseline ratchet | PR #241 |
