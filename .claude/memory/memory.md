# memory.md — estado corrente (max 100 linhas)

## Sessão 2026-10-22 — CONCLUÍDA

- **PR #230 mergeado** (`41ca7ca`, merge commit) — `main` sincronizada.
- Análise `afonsoft/open-webui` (clone local HEAD `553dfe1`) → 6 SPECs
  `Draft` em `.specs/` (90-95), índice atualizado:
  - 90: pipeline componível de system prompt (`ISystemPromptContributor`
    + Order + tolerância; tiers T1-T3; SDD surface; delegation policy)
  - 91: envelope `<untrusted_input>` p/ conteúdo externo em prompts —
    `GitHubWebhookService.BuildPrompt:597` injeta `CommentBody` cru
  - 92: drift guards client (css-classes, form-a11y, i18n parity +
    placeholders) — 8 locales sem check
  - 93: perf/loadtest harness — mock LLM OpenAI-compat + loadtest
    (rps/conversations/subagents, SignalR não SSE)
  - 94: WASM boot resilience — `boot.js` autostart=false +
    `/framework-assets/{stem}/{ext}` (`?enc=b64`) p/ proxies corporativos
  - 95: catálogo `*.agent.md` em camadas + `agent_catalog` tool +
    `subagent_type` validado (dep: SPEC-90)
- Gate mantido: SPECs Draft → aprovação antes de `execute-specs`.

## Sessão 2026-10-22 (anterior, mesmo dia)

- PRs #225-#229 mergeados: SPECs 83-88 entregues (runbook, sizing,
  postgres ext, sysbox runtime, image-loader DS, chrome SDK, cleanup
  PVC k8s) + Lote C Sonar (S3267/S3358/S1192 + avulsos).
- SPEC-89 Draft: defer Lote D Sonar (S3776/S107/S3903/S1075/S127/S3218).

## Fatos operacionais

- `scripts/check-spec-lifecycle.sh` → ok (nenhuma spec terminal em `.specs/`)
- SPECs ativas: 70-74 (Approved, Epic #152), 89-95 (Draft)
- Próximos números livres: 96+
- SonarCloud: `https://sonarcloud.io/summary/overall?id=afonsoft_OpenHandsNET&branch=main`
- Bloqueio persistente: #148 — `SONAR_TOKEN` secret + `SONAR_ENABLED=true`
- Clone open-webui p/ re-auditoria: `/home/ubuntu/repos/open-webui`
