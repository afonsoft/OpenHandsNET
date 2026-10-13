# migration-planner run — 2026-10-12

## Resumo

Port do ecossistema OpenHands para .NET 10/Blazor WASM (`afonsoft/OpenHandsNET`),
modo rewrite-into-new-repo. Fontes clonadas (baseline em
`docs/reference/upstream-baseline.md`): OpenHands (Canvas), software-agent-sdk,
OpenHands-CLI, enterprise (Polyform — conceitos), OpenHands-Cloud (Polyform),
automation, afonsoft/open-webui (referência .NET). Docs oficiais lidas:
cloud-api, search-events OpenAPI, cli/cloud, local-setup, enterprise.

## Decisões aprovadas pelo usuário

- Banco: **SQLite default** + provider switch p/ PostgreSQL (enterprise).
- LLM: **`IAgentLlmProvider` abstrato + Semantic Kernel** primário; gateway OpenAI-compat.
- SPECs 01–08 **Approved**; Issues publicados.

## Tracking

- Epic: **E10** → https://github.com/afonsoft/OpenHandsNET/issues/1
- Slices (SPEC → Issue → blocked_by):
  - 01 → #2 — (raiz)
  - 02 → #3 — #2 | 03 → #4 — #2
  - 04 → #5 — #3, #4
  - 05 → #6 — #5 | 06 → #7 — #5 | 08 → #9 — #5 | 07 → #8 — #5
- Labels canônicos criados no repo (epic/slice/feature/bug + status set).

## Correções vs guia do usuário (registradas)

- Eventos V1 (`kind`, ULID, `parent_id`) — não V0 (`id` int + action/args).
- Start-task assíncrono + `selected_repository` — não `conversation_id` síncrono.
- .NET 10 / C# 14 + Clean Architecture 5 projetos — não .NET 9 / Core-Runtime-Web.

## Pendências

- `[A DEFINIR]` domínio default do servidor cloud (SPEC 06: `app.openhandsnet.dev` placeholder).
- `[A DEFINIR]` billing provider (SPEC 08: Stripe atrás de `IBillingProvider`).
- Fase 3 fora do corte: automações, ACP, canvas extensions, warm-pool K8s, GitHub App.
- Commits locais: `ebf39a4` + tracking; **push não executado** (aguarda OK do usuário).
