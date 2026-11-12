# memory.md — estado corrente (max 100 linhas)

## Sessão 2026-11-12 — SPEC-144 batch-refactor (issue #409)

- Branch `feature/spec-144-batch-refactor` → **PR #423 OPEN**. Orquestração
  de refatoração em lotes: `POST /api/v1/refactors` (repositories[]+goal+
  strategy+mode) → conversa `refactor-planner` → `RefactorRepoAnalyzer`
  (C# usings+csproj refs, TS/JS e Python heuristics, dir fallback) →
  `RefactorBatchPlanner` (topo-sort determinístico, ciclos→mesmo nível,
  overlap independent→força stacked, **deps persistem batch numbers
  globais 1-based — bug corrigido com `startN`** p/ multi-repo) →
  `refactor_plan_v1` + `refactor_batches` → `awaiting_approval`.
- `BatchExecutor`: filhos `refactor-worker` via delegation, paralelismo
  limitado (default 5), verify/fix loop cap 5, logs redigidos + SHA-256
  digest, parser `refactor_batch_v1`, 1 PR/lote `refactor/{plan_id}/{n}`,
  **nunca auto-merge**. Webhook merged→desbloqueia dependentes; feedback
  PR→delegate_message vivo ou deferral; reconcile poll p/ webhooks
  perdidos + PRs fechados sem merge.
- Perms: `refactors.run` member+ (create/list/get/control),
  `refactors.approve` admin+ (gate start — role resolvida no endpoint,
  `RolePermissions.ForOrgRole`). Audit completo. UI `/refactors` +
  detalhe, sidebar `git-branch` (ícone `git` não existe — usar
  `git-branch`), i18n 55 keys ×8. Dual migrations SQLite+Postgres.
- Verde: unit 49/49 Refactors (1050 total), integration 7/7 endpoints
  (275 total), client 147, release build 0/0, format clean, golden
  OpenAPI regen (8 paths /refactors), guards i18n/css/razor OK.
- Lock files HotReload = restore noise → revertidos (mesmo padrão SPEC-143).
- SandboxAgent ExecStream SSE test = flake conhecido (passou isolado).
- Spec arquivada: `docs/specs/144_batch_refactor_spec.md` (Status
  Delivered); `.specs/README.md` linha 144 → `Delivered · PR #423 | #409`.

## Sessão 2026-11-12 — SPEC-143 code-scans ENTREGUE (issue #408)

- **PR #422 merged**. Pipeline `/scan` + `POST /api/v1/scans` (202 +
  coordinator) → `ScanWorker`/`IScanDispatcher` (channel) →
  `ScanOrchestrator` fan-out read-only por partições de diretório
  (`ScanPartitioner` bin-pack, ignora bin/obj/node_modules) → filhos
  `agent_type=scan-worker` via `IDelegationService` → `ScanFindingParser`
  (`scan_finding_v1`) → `ScanDeduplicator` → `scans`/`scan_findings`.
- Fix: `POST /scan-findings/{id}/fix` + `fix-bulk`; reconciliação
  assigned→pr_open→merged (`ScmPullRequest.Merged` nos 4 providers).
- Permissões `scans.run`/`scans.manage`/`scans.findings`. UI `/scans` +
  `/scans/{id}` + slash `/scan` + i18n ×8.
- **Bug real**: cursor pagination `string.Compare(id.ToString(), cursor)`
  quebrado no SQLite — Sqlite persiste Guid TEXT uppercase, cursor
  lowercase → linha-cursor duplicada. Fix `LOWER()` ambos os lados.
  **Latent em AppConversationService.cs:184 + Review/Qa/Autofix —
  follow-up pendente.**

## Sessões anteriores (resumo)

- SPEC-146 multi-repo-conversations → **PR #421 merged** (issue #411).
- SPEC-145 repo-wiki-mcp → **PR #420 merged** (issue #410).
- SPEC-142 QA agent evidência → **PR #418 merged** (issue #407).
- SPEC-141 CI autofix loop → **PR #416 merged** (issue #406).
- SPEC-140 pr-review-agent → **PR #414 merged** (issue #405).
- SPEC-147 product-polish → **PR #413 merged** (issue #412).
- feat/host-installer → **PR #402 merged**; PRs #399/#400/#401 sonar
  tail + `.claude/`→`.agents/` rename.

## Fatos operacionais

- SPECs ativas restantes: **139** (brand assets, Draft).
- `scripts/check-spec-lifecycle.sh` → specs terminais fora de `.specs/`.
- SonarCloud: `.../summary?id=afonsoft_OpenHandsNET&branch=main`;
  bloqueio #148 (`SONAR_TOKEN` + `SONAR_ENABLED=true`).
- Lock files `packages.lock.json`: `dotnet restore` bumpa HotReload
  transitivo — reverter como noise antes de commitar.
- SSE flake conhecido: `SandboxAgentTests.ExecStream_Sse_Chunks_*`.
- Lições: multicriteria `.sonarcloud.properties` NÃO suprime AutoScan
  (só NOSONAR); method-group handlers ganham tag auto do tipo (.NET 10);
  PR policy exige tests/ junto de prod .cs; renames S927 não tocam
  named-arg labels; `insert_final_newline=false` no .editorconfig.
- Histórico detalhado: `.agents/memory/{YYYYMMDD}-memory.md` (append-only).
