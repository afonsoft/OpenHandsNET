# Documentação — OpenHandsNET

Índice da documentação (pt-BR). Versão em inglês em [`en/`](en/README.md).

## Produto

- [comparison.md](comparison.md) — OpenHandsNET vs OpenHands (OSS/Cloud/Ent)
  vs Devin Web, em detalhe.
- [automations.md](automations.md) — routines agendadas + webhook triggers +
  tool `routine` (SPEC-47).
- [eval.md](eval.md) — `openhandsnet eval` headless + asserts + `llm_judge`
  (SPEC-39).
- [cli.md](cli.md) — `openhandsnet` CLI/TUI (SPEC-06/31).

## Features (UI)

- [features/chat-ux.md](features/chat-ux.md) — streaming com reasoning,
  heartbeat, tool cards, pause/resume (SPEC-41).
- [features/ide-workspace.md](features/ide-workspace.md) — workbench IDE
  (explorer + Monaco + SCM + search) (SPEC-42).
- [features/checkpoints.md](features/checkpoints.md) — snapshots por turno +
  revert com drift guard (SPEC-43).
- [features/attention-inbox.md](features/attention-inbox.md) — inbox de
  conversas aguardando você + `ask_user` (SPEC-44).
- [features/composer.md](features/composer.md) — @menções, /slash, drafts,
  uploads (SPEC-45).
- [features/i18n.md](features/i18n.md) — `LocalizationService`, fallback,
  parity, seletor (SPEC-46).
- [ui/ux.md](ui/ux.md) — design system, temas, componentes (SPEC-26).
- [ui/admin.md](ui/admin.md) — páginas administrativas (SPEC-21).

## Integrações e enterprise

- [features/scm.md](features/scm.md) — GitHub App + GitLab/Bitbucket/Azure
  DevOps, webhooks verificados, workspace PRs (SPECs 52–53).
- [features/chatops.md](features/chatops.md) — Slack app: menção/slash →
  sessão, thread sync, auto-triage (SPEC-54).
- [features/tickets.md](features/tickets.md) — Linear/Jira triggers →
  start-task + write-back (SPEC-55).
- [features/service-users.md](features/service-users.md) — identidades
  `svc_*` escopadas + impersonation (SPEC-57).
- [deployment/identity.md](deployment/identity.md) — SCIM 2.0 + grupos
  IdP→role/tier + usage tiers (SPEC-58).
- [features/knowledge.md](features/knowledge.md) — suggestion queue,
  human review, groom automático (SPEC-63).
- [features/browser-profiles.md](features/browser-profiles.md) — storage
  state criptografado, site cookies, TOTP (SPEC-64).

## Deployment

- [deployment/local.md](deployment/local.md) — quickstart local.
- [deployment/kubernetes.md](deployment/kubernetes.md) — Helm + Sysbox
  (conclusão aprovada na SPEC-49).
- [deployment/enterprise.md](deployment/enterprise.md) — visão enterprise:
  auth, orgs, multi-réplica, sizing.
- [deployment/security.md](deployment/security.md) — secrets, egress,
  analyzer, políticas (SPEC-48).
- [deployment/dind.md](deployment/dind.md) — Docker-in-Docker.
- [deployment/surfaces.md](deployment/surfaces.md) — surfaces do
  sandbox-agent (terminal/VSCode/preview/ACP) (SPEC-40).
- [deployment/egress-allowlist.md](deployment/egress-allowlist.md) — Squid
  sidecar por domínio (SPEC-19).
- [deployment/external-postgres.md](deployment/external-postgres.md) —
  Postgres gerenciado (SPEC-14).
- [deployment/postgres.md](deployment/postgres.md) — provider switch
  SQLite↔PostgreSql.
- [deployment/custom-images.md](deployment/custom-images.md) — blueprints de
  runtime (SPEC-17).
- [deployment/billing.md](deployment/billing.md) — planos, quotas, usage,
  BYOR LiteLLM (SPEC-20/50).
- [deployment/sandbox-sizing.md](deployment/sandbox-sizing.md) — sizing e
  limites.

## API

- [api/openai-gateway.md](api/openai-gateway.md) — gateway
  `/v1/chat/completions` no sandbox-agent (SPEC-40).

## Referência interna

- [reference/upstream-baseline.md](reference/upstream-baseline.md) — SHAs
  dos repos upstream analisados.
- [reference/openhands-upstream/](reference/openhands-upstream/) — extratos
  das docs oficiais (cloud-api, OpenAPI events, cli).
- `.specs/` (raiz do repo) — SPECs SDD/ADD, fonte primária de verdade
  (workflow agent-first: ver "AI-Driven Development" no README).
- `migration-plan/` — research + roadmap originais.
