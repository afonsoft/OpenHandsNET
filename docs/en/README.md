# Documentation — OpenHandsNET

Documentation index (English). Versão em português na raiz: [`../README.md`](../README.md).

## Product

- [comparison.md](comparison.md) — OpenHandsNET vs OpenHands (OSS/Cloud/Ent)
  vs Devin Web, in depth.
- [automations.md](automations.md) — scheduled routines + webhook triggers +
  `routine` tool (SPEC-47).
- [eval.md](eval.md) — `openhandsnet eval` headless + asserts + `llm_judge`
  (SPEC-39).
- [cli.md](cli.md) — `openhandsnet` CLI/TUI (SPEC-06/31).

## Features (UI)

- [features/chat-ux.md](features/chat-ux.md) — streaming with reasoning,
  heartbeat, tool cards, pause/resume (SPEC-41).
- [features/ide-workspace.md](features/ide-workspace.md) — IDE workbench
  (explorer + Monaco + SCM + search) (SPEC-42).
- [features/checkpoints.md](features/checkpoints.md) — per-turn snapshots +
  revert with drift guard (SPEC-43).
- [features/attention-inbox.md](features/attention-inbox.md) — inbox of
  conversations waiting on you + `ask_user` (SPEC-44).
- [features/composer.md](features/composer.md) — @mentions, /slash, drafts,
  uploads (SPEC-45).
- [features/i18n.md](features/i18n.md) — `LocalizationService`, fallback,
  parity, selector (SPEC-46).
- [ui/ux.md](ui/ux.md) — design system, themes, components (SPEC-26).
- [ui/admin.md](ui/admin.md) — admin pages (SPEC-21).

## Integrations and enterprise (pt-BR docs)

- [features/scm.md](../features/scm.md) — GitHub App + GitLab/Bitbucket/Azure
  DevOps, verified webhooks, workspace PRs (SPECs 52–53).
- [features/chatops.md](../features/chatops.md) — Slack app: mention/slash →
  session, thread sync, auto-triage (SPEC-54).
- [features/tickets.md](../features/tickets.md) — Linear/Jira triggers →
  start-task + write-back (SPEC-55).
- [features/service-users.md](../features/service-users.md) — scoped `svc_*`
  identities + impersonation (SPEC-57).
- [features/knowledge.md](../features/knowledge.md) — suggestion queue,
  human review, auto-groom (SPEC-63).
- [features/browser-profiles.md](../features/browser-profiles.md) — encrypted
  storage state, site cookies, TOTP (SPEC-64).

## Deployment

- [deployment/local.md](deployment/local.md) — local quickstart.
- [deployment/kubernetes.md](deployment/kubernetes.md) — Helm + Sysbox
  (completion approved in SPEC-49).
- [deployment/enterprise.md](deployment/enterprise.md) — enterprise stack:
  auth, orgs, multi-replica, sizing.
- [deployment/security.md](deployment/security.md) — secrets, egress,
  analyzer, policies (SPEC-48).
- [deployment/dind.md](deployment/dind.md) — Docker-in-Docker.
- [deployment/surfaces.md](deployment/surfaces.md) — sandbox-agent surfaces
  (terminal/VSCode/preview/ACP) (SPEC-40).
- [deployment/egress-allowlist.md](deployment/egress-allowlist.md) — Squid
  sidecar domain allowlist (SPEC-19).
- [deployment/external-postgres.md](deployment/external-postgres.md) —
  managed Postgres (SPEC-14).
- [deployment/postgres.md](deployment/postgres.md) — SQLite↔PostgreSql
  provider switch.
- [deployment/custom-images.md](deployment/custom-images.md) — runtime
  blueprints (SPEC-17).
- [deployment/billing.md](deployment/billing.md) — plans, quotas, usage,
  BYOR LiteLLM (SPEC-20/50).
- [deployment/sandbox-sizing.md](deployment/sandbox-sizing.md) — sizing
  profiles and limits.
- [deployment/troubleshooting.md](deployment/troubleshooting.md) —
  operations runbook: support bundle, component→symptom, first-conversation
  checks (SPEC-83).

## API

- [api/openai-gateway.md](api/openai-gateway.md) — sandbox-agent
  `/v1/chat/completions` gateway (SPEC-40).

## Internal reference

- [../reference/upstream-baseline.md](../reference/upstream-baseline.md) —
  upstream repo SHAs analyzed.
- [../reference/openhands-upstream/](../reference/openhands-upstream/) —
  official docs extracts (cloud-api, OpenAPI events, cli).
- `.specs/` (repo root) — SDD SPECs, primary source of truth.
- `migration-plan/` — original research + roadmap.
