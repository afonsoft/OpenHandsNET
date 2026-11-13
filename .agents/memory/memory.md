# memory.md — estado corrente (max 100 linhas)

## Sessão 2026-11-13 — SPEC-152 benchmark-adapters ENTREGUE (issue #446)

- **PR #449** (branch `feature/spec-152-benchmark-adapters`). Harness
  hermético de evals na pipeline real: `eval_task_v1` contract +
  suites `swe-lite` (embedded ~20 authored), `terminal-bench-2`
  (dataset dir + TomlReader próprio), `custom` (jsonl).
- `eval_runs`/`eval_results` (dual migrations); `EvalWorker` canal +
  `EvalRunner` por task (sandbox efêmero → setup_cmds → conversa
  `agent_type=eval` c/ model pin + route_override → checker → verdict
  + digest + tokens/cost). `IDbContextFactory` p/ paralelismo por task.
- eval_mode hermético no `AgentRunner`/`ConversationStartWorker`:
  sem SCM/ChatOps/ticket postbacks, confirm auto-approve, ask_user
  sintético, cap `Eval:MaxTurns`, sem creds GH.
- `/api/v1/evals` (create/dedup, list/get/results/cancel, estimate,
  scorecard) c/ `perm:evals.run` admin+; guardrails MaxTasks/estimate
  gate/daily cost cap. CLI `eval run|status|report|datasets fetch`
  (SPEC-39 local preservado). Golden OpenAPI regen.
- Testes: 49 unit + 8 integration; suites completas verdes.
- Lições chave: `IDbContextFactory` não vem de graça c/ `AddDbContext`;
  `DateTimeOffset` não compara no SQLite → `*Ticks` mirrors;
  ExecuteUpdateAsync condicional p/ cancel race; seed tracked +
  cross-ctx writes → `Entry().ReloadAsync()` em asserts.

## Sessão 2026-11-12 — SPEC-150 cli-handoff ENTREGUE (issue #430)

- **PR #433 merged** (squash `eaba487`). Handoff local↔cloud:
  `POST /conversations/handoff` (patch validado 400/oversize,
  SecretRedactor, start-task assíncrono — políticas intactas) →
  worker aplica no sandbox `git apply --3way`→`--reject` (.rej→stats,
  evento `handoff_applied`) → prompt com envelope SPEC-91.
  `GET /{id}/handoff-package` on-demand (diff sandbox vs base +
  untracked/blocklist/binário placeholder), cache TTL 60min em
  `HandoffPackageJson`, `?regenerate=1`, snapshot p/ sandbox morto,
  authz owner/org. CLI `handoff`/`pickup` (force-stash, reject→exit 2,
  `--pr` resolve conversa). Dual migration + golden OpenAPI.
- Testes: 37 unit + 9 integration novos; suite 1157/303/147/10 verde
  (2 flakes Spec42 passaram no rerun). Archive: `9960891`.
- Lições: `ef migrations add --no-build` usa Debug (stale c/ build
  Release → `--configuration Release`); `ef migrations remove`
  Postgres tenta conectar (`--force`); bool query non-nullable =
  required (400 — usar `= false`); 1º dev-user = superadmin.

## Sessão 2026-11-12 — SPEC-148 skills-marketplace ENTREGUE (issue #428)

- **PR #431 merged** (squash `22b6d6e`). Marketplace self-hostable:
  registry embedded `marketplace.json` (offline) + `RegistryUrl`
  (HTTPS) + `RegistryFile` + `AllowedHosts` host/CIDR, cache 5 min.
- Manifest: kinds `skill|mcp-server|agent|hook|bundle`, semver,
  `min_app_version`, files inline/remotos, `permissions_required`
  (nunca auto-granted — `permissions_accepted` explícito, senão 422),
  SHA-256 obrigatório p/ conteúdo remoto, bundle `entry_ids` c/ depth
  guard + `allow_builtin_override`.
- `installed_skills` (scope `system|org|user`, unique
  scope/scope_id/entry_id, `meta_json` guarda materialização p/
  toggle/remove). Dual migration SQLite `20261112202947` + Postgres
  `20261112203107`. Precedência `user > org > system`; MCP owners
  `system → org → user → local`, builtin nunca sobrescrito.
- `MarketplaceService` pipeline: resolve→min-version→perms→materializa
  (skill→`SkillRecord source=marketplace` protegido das APIs de skill;
  agent→`.agent.md`; mcp→linha MCP por escopo; hook→`hook_configs`;
  bundle→recursivo). Toggle/remove desmaterializam; upgrade re-resolve.
- API `/api/v1/marketplace/*` (entries/{id}, installed, install,
  PATCH toggle, DELETE, upgrade/{id}); erros 403/409/422/502; perms
  `marketplace.install` member+ e `marketplace.manage` admin+; audit
  `marketplace.*`.
- UI: sidebar Marketplace, `MarketplacePage` (grid+installed tab,
  scope picker por permissão, botões gated p/ manage), `MarketplaceEntryPage`
  (readme/perms/changelog); i18n 49 keys ×8; CSS `.mkt-*` em app.css.
- CLI: `sandcastle marketplace list|install|remove|upgrade`
  (`--installed/--scope/--org/--version/--yes`). Seed: 12 entries.
- Verde: unit **1091** (mkt 28), integration **286** (mkt 11),
  client 147, sandbox 10; release build 0/0; format clean; golden
  OpenAPI regen; guards css/a11y/i18n×2/mcp OK. Lock noise revertido.
- Spec arquivada `docs/specs/148_*`; `.specs/README.md` →
  `Delivered · PR #431 | #428`. Issue #428 → done.

## Sessões anteriores (resumo)

- SPEC-144 batch-refactor → **PR #423 merged** (issue #409).
- SPEC-143 code-scans → **PR #422 merged** (issue #408).
- SPEC-146 multi-repo-conversations → **PR #421 merged** (issue #411).
- SPEC-145 repo-wiki-mcp → **PR #420 merged** (issue #410).
- SPEC-142 QA agent evidência → **PR #418 merged** (issue #407).
- SPEC-141 CI autofix loop → **PR #416 merged** (issue #406).
- SPEC-140 pr-review-agent → **PR #414 merged** (issue #405).
- SPEC-147 product-polish → **PR #413 merged** (issue #412).
- feat/host-installer → **PR #402 merged**; PRs #399/#400/#401 sonar
  tail + `.claude/`→`.agents/` rename.

## Fatos operacionais

- SPECs ativas: **139** (brand assets) Draft — 148/149/150 delivered.
- `scripts/check-spec-lifecycle.sh` → specs terminais fora de `.specs/`.
- SonarCloud: `.../summary?id=afonsoft_OpenHandsNET&branch=main`;
  bloqueio #148 (`SONAR_TOKEN` + `SONAR_ENABLED=true`).
- Lock files `packages.lock.json`: `dotnet restore` bumpa HotReload
  transitivo — reverter como noise antes de commitar (SPEC-148: bumps
  entraram no merge e foram revertidos no commit de archive).
- SSE flake conhecido: `SandboxAgentTests.ExecStream_Sse_Chunks_*`
  (raiz corrigida no PR #425, observar recorrência).
- Lições: multicriteria `.sonarcloud.properties` NÃO suprime AutoScan
  (só NOSONAR); method-group handlers ganham tag auto do tipo (.NET 10);
  PR policy exige tests/ junto de prod .cs; renames S927 não tocam
  named-arg labels; `insert_final_newline=false` no .editorconfig;
  JSON snake_case global (`SnakeCaseLower`) — anonymous DTOs com
  nomes explícitos p/ wire.
- Histórico detalhado: `.agents/memory/{YYYYMMDD}-memory.md` (append-only).
