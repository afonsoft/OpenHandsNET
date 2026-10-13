# Migration Roadmap — OpenHandsNET

Port do ecossistema **OpenHands** (Python/React) para **.NET 10 / C# 14 / Blazor
WebAssembly**, entregando o conceito "cloud + enterprise" em código open-source
(MIT), com UX inspirada no **Devin Web** e componentes de chat/layout reusados de
`afonsoft/open-webui`.

> **Direção:** rewrite em novo repositório (`afonsoft/OpenHandsNET`), não Strangler
> in-place — não há sistema .NET legado; o "legado" é a referência upstream
> Python/React, analisada read-only. Fontes e baseline em
> `migration-plan/research/` e `docs/reference/openhands-upstream/`.

## Capability map

| SPEC | Domínio | Depende de | Fase |
|---|---|---|---|
| `.specs/01_architecture_overview.md` | fundação (sln, DI, EF, hosted services) | — | **P0** |
| `.specs/02_event_stream_spec.md` | modelo de eventos V1 + event store | 01 | **P1** |
| `.specs/03_runtime_sandbox_spec.md` | sandbox Docker + agent-worker | 01 | **P1** |
| `.specs/04_rest_and_signalr_api_spec.md` | API `/api/v1` + SignalR | 02, 03 | **P1** |
| `.specs/05_devin_ui_integration_spec.md` | Blazor WASM multi-pane | 04 | **P2** |
| `.specs/08_enterprise_security_rbac.md` | auth/orgs/api-keys/quota | 04 | **P2** |
| `.specs/06_cli_spec.md` | CLI Spectre.Console | 04 | **P2** |
| `.specs/07_mcp_integration_spec.md` | MCP client/host | 04 | **P3** |

Build order: `01 → (02 ∥ 03) → 04 → (05 ∥ 08 ∥ 06) → 07`.
Fase 3+ (fora das 8 SPECs): automações, ACP, canvas extensions, warm-pool K8s.

## Marcos

- **M1 (MVP local):** solução compila; `dotnet run` sobe Api+Client; criar
  conversa → sandbox Docker local → eventos fluem via SignalR → chat Devin-like
  básico (chat + terminal + files). SPECs 01–05.
- **M2 (Produto):** auth + API keys `sk-*`, orgs/quota, CLI (`ohnet login|cloud|serve`),
  MCP básico. SPECs 06–08.
- **M3 (Cloud/enterprise):** multi-tenant, deployment self-hosted (compose/Helm),
  warm-pool opcional, auditoria, OTel completo.

## Cross-cutting concerns

- **Auth**: OSS single-user JWT+`sk-*` (padrão open-webui-net); OIDC plugável (SPEC 08).
- **Persistência**: EF Core + SQLite default; PostgreSQL via provider switch.
- **Contrato**: REST compatível com V1 upstream onde fizer sentido como port;
  divergências documentadas em cada SPEC (seção API Contract → "Divergências").
- **Observabilidade**: OpenTelemetry + Serilog; `observability_*` fields do
  contrato upstream preservados.
- **Paridade**: testes de contrato golden (eventos JSON) contra fixtures derivadas
  do OpenAPI upstream (`docs/reference/openhands-upstream/search-events-openapi.md`).

## Epics

- [**[E10] OpenHandsNET — port .NET 10 do ecossistema OpenHands**](https://github.com/afonsoft/OpenHandsNET/issues/1) - `todo`
  - Slices: #2 (01), #3 (02), #4 (03), #5 (04), #6 (05), #7 (06), #8 (07), #9 (08)

## Questões em aberto

1. Banco default: **SQLite** (recomendado, paridade open-webui-net) vs PostgreSQL-first.
2. LLM: **Semantic Kernel** (recomendado; guia do usuário) vs reusar provider-layer
   do open-webui-net — abstração permite ambos.
3. GitHub App para auto-PR (cloud) — fase 3, requer app próprio `[A DEFINIR]`.
