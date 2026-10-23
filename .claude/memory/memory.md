# memory.md — estado corrente (max 100 linhas)

## Sessão 2026-10-23 — Epic #231 COMPLETO (6/6 SPECs)

- **Merged + arquivadas**: #232/SPEC-91 (PR #238), #233/SPEC-90 (#239),
  #234/SPEC-95 (#240), #236/SPEC-93 (#242), #237/SPEC-94 (#243),
  #235/SPEC-92 (#241 — workflow diff revisado e aprovado pelo usuário).
- Epic #231 fechado. SPECs 90-95 todas em `docs/specs/` (`e72facb`).
- Issue **#244** criada: retire client drift baseline (19 css, 54 a11y,
  ~1720 chaves i18n zh-CN, 3 placeholders).
- Golden OpenAPI: regenerar com `OPENHANDSNET_UPDATE_GOLDEN=1` quando
  endpoint público novo entrar.
- Baseline SPEC-92 congelou drift real: 19 classes, 54 a11y, ~1720
  chaves i18n, 3 placeholders → issue de follow-up pendente.
- `dotnet format` exige using-groups separados por linha em branco e
  sem newline final (`insert_final_newline=false`).

## Sessão 2026-10-22 (cont.) — CONCLUÍDA

- Retomada da grizzled-cocoa: memória commitada (`18a12ed`) + SPECs
  90-95 **Approved** com issues — Epic **#231**, slices #232-237
  (`87b66f8`). Ordem: #232(91)→#233(90)→#234(95, dep.90)→#235/236/237.
- #235 (SPEC-92) toca `.github/workflows/` → revisão humana.

## Sessão 2026-10-22 — CONCLUÍDA

- **PR #230 mergeado** (`41ca7ca`, merge commit) — `main` sincronizada.
- Análise `afonsoft/open-webui` (clone local HEAD `553dfe1`) → 6 SPECs
  em `.specs/` (90-95), índice atualizado:
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

## Sessão 2026-10-22 (anterior, mesmo dia)

- PRs #225-#229 mergeados: SPECs 83-88 entregues (runbook, sizing,
  postgres ext, sysbox runtime, image-loader DS, chrome SDK, cleanup
  PVC k8s) + Lote C Sonar (S3267/S3358/S1192 + avulsos).
- SPEC-89 Draft: defer Lote D Sonar (S3776/S107/S3903/S1075/S127/S3218).

## Fatos operacionais

- `scripts/check-spec-lifecycle.sh` → ok (nenhuma spec terminal em `.specs/`)
- SPECs ativas: 70-74 (Approved, Epic #152), 90-95 (Approved, Epic #231), 89 (Draft)
- Próximos números livres: 96+
- SonarCloud: `https://sonarcloud.io/summary/overall?id=afonsoft_OpenHandsNET&branch=main`
- Bloqueio persistente: #148 — `SONAR_TOKEN` secret + `SONAR_ENABLED=true`
- Clone open-webui p/ re-auditoria: `/home/ubuntu/repos/open-webui`
