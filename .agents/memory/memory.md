# memory.md — estado corrente (max 100 linhas)

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

- SPECs ativas: **149** (model-router), **150** (cli-handoff) —
  Approved; **139** (brand assets) Draft.
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
