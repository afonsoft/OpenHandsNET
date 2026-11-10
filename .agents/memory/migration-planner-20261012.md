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
  - 02 → #3 — #2 | 03 → #4 — #2 | 10 → #11 — #2 | 13 → #14 — #2
  - 04 → #5 — #3, #4 | 09 → #10 — #3, #4
  - 11 → #12 — #3, #11 | 12 → #13 — #4, #11
  - 05 → #6 — #5 | 06 → #7 — #5 | 08 → #9 — #5 | 07 → #8 — #5
- Build order: `01 → (02 ∥ 03 ∥ 10 ∥ 13) → (04 ∥ 09 ∥ 11 ∥ 12) → (05 ∥ 08 ∥ 06) → 07`.
- Labels canônicos criados no repo (epic/slice/feature/bug + status set).

## Rodada 2 — enriquecimento (mesmo commit posterior)

- SPECs 01–08 ganharam §8 Deliverables, §9 Referências (permalinks `blob/<sha>` +
  docs.openhands.dev + libs Microsoft/NuGet), §10 Guardrails/Devin, §11 DoD.
- Novas SPECs **Approved**: 09 Agent Engine (IAgentLlmProvider+SK, ReAct loop,
  confirmation mode), 10 Persistência EF (DbContext único, 20 tabelas, Sqlite/PG),
  11 Skills & Hooks, 12 Git/SCM (clone/diff/git_commit/askpass), 13 DevOps &
  Observability (Dockerfile, compose profiles, CI, OTel).
- Issues #10–#14 criadas com `slice`+`todo` e `blocked_by` reais; Epic #1 atualizado.

## Rodada 3 — correção SPEC-08 (re-escrita v2, status Draft)

- Usuário enviou análise do código real `enterprise/server/` (auth/authorization,
  routes/*) com 10 correções: Keycloak broker OIDC (não SAML/Okta direto),
  cookie `keycloak_auth` chunked, dual auth cookie+Bearer, API key org-binding
  (X-Org-Id > binding > current_org_id), RBAC permission-based (~30 perms,
  ROLE_PERMISSIONS), dois níveis de role (org_member.role_id vs user.role_id
  superadmin), invariante último super-admin → 409, device flow → API key 7d,
  provision-user + rollback, invites batch (admin não convida owner), audit =
  logging+PostHog (nossa AuditLog é melhoria).
- SPEC-08 reescrita fiel ao modelo real → **status Draft**; issue #9 `todo→backlog`
  aguardando re-aprovação.

## Correções vs guia do usuário (registradas)

- Eventos V1 (`kind`, ULID, `parent_id`) — não V0 (`id` int + action/args).
- Start-task assíncrono + `selected_repository` — não `conversation_id` síncrono.
- .NET 10 / C# 14 + Clean Architecture 5 projetos — não .NET 9 / Core-Runtime-Web.

## Pendências

- `[A DEFINIR]` domínio default do servidor cloud (SPEC 06: `app.openhandsnet.dev` placeholder).
- `[A DEFINIR]` billing provider (SPEC 08: Stripe atrás de `IBillingProvider`).
- Fase 3 fora do corte: automações, ACP, canvas extensions, warm-pool K8s, GitHub App.
- Commits locais: `ebf39a4` + tracking; **push não executado** (aguarda OK do usuário).
