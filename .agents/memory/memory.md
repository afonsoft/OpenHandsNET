# memory.md — estado corrente (max 100 linhas)

## Sessão 2026-11-12 — SPEC-143 code-scans (issue #408)

- Branch `feature/spec-143-code-scans`. Pipeline completo: `/scan` +
  `POST /api/v1/scans` (202 + coordinator conv) → `ScanWorker`/
  `IScanDispatcher` (channel) → `ScanOrchestrator` fan-out read-only por
  partições de diretório (`ScanPartitioner` bin-pack por file count,
  ignora bin/obj/node_modules/.git/.vs/.idea/dist/build) → filhos
  `agent_type=scan-worker` via `IDelegationService` (herda
  `ScanPermissions.ReadOnly`, depth+sibling cap só-ativos) → coleta
  MessageEvents → `ScanFindingParser` (`scan_finding_v1` fenced block +
  trailing-JSON fallback, caps + injection hygiene) → `ScanDeduplicator`
  (repo+path+line-proximity+kind; severidade > descrição > confidence) →
  persist `scans`/`scan_findings` com redaction. Fix explícito:
  `POST /scan-findings/{id}/fix` (assign-first → 409 dupe, conversa
  scan-fix com escrita/PR) + `fix-bulk` cap; reconciliação
  assigned→pr_open→merged (`ScmPullRequest.Merged` adicionado aos 4
  providers). Automation `kind=scan` (`scan_config_json`, default "task"
  backfill) dispara `IScanService` (run succeeded imediato — lifecycle
  próprio). Permissões `scans.run`/`scans.manage`/`scans.findings`
  (member run+findings, admin manage). `ModelRole.Analysis`. Dual
  migrations SQLite+Postgres ok. UI `/scans` + `/scans/{id}` + sidebar +
  slash `/scan` + i18n 61 keys ×8. `SwarmOrchestrator` stub deletado.
- **Bug real encontrado pelos testes**: `string.Compare(id.ToString(), cursor)`
  é quebrado no SQLite — Microsoft.Data.Sqlite persiste/bindinga Guid como
  TEXT **uppercase**, cursor encoda `ToString()` **lowercase** → a linha-
  cursor duplica na página seguinte (binário collation `UPPER < lower`).
  Fix em `ScanService`: `string.Compare(s.Id.ToString().ToLower(), id)`
  com `id.ToLowerInvariant()` (LOWER() monótono, consistente nos 2
  providers — uuid PG serializa lowercase). **Mesmo padrão latent-bug em
  AppConversationService.cs:184, ReviewService.cs, QaService.cs,
  AutofixService.cs** — follow-up fora do escopo SPEC-143.
- Verde: 48 unit Scans + 5 endpoint integration + 6 automation-scan;
  build 0/0; format clean; guards i18n/css/a11y/mcp-golden OK.
- Pendente fim: suite full, commit/push/PR, labels #408, archive spec.

## Sessão 2026-11-11 (cont. 4) — SPEC-146 multi-repo-conversations (issue #411)

- **WIP herdado** (sessão anterior, uncommitted em `main`, ~2.1k linhas)
  movido p/ `feature/spec-146-multi-repo` (mesma HEAD) e revisado por
  inteiro: `selected_repositories` (hard-cap 4 via `MultiRepoOptions`,
  `RepositorySelection.Normalize` → 400 `RepositorySelectionException`);
  `MultiRepoLayout`/`MultiRepoCloner` (clone paralelo — primário
  obrigatório, secundário tolerated → state event `repositories`);
  `RepoCloneCredentialResolver` (conexão SCM→token→fallback por host;
  askpass/extraHeader, nunca argv); `IGitService` +`repoPath?` (só
  `/workspace/*`); `/workspace/repo` → symlink primário; git_* tools
  `repo` arg + `create_pull_request.repository`; PR por repo
  (`pull_requests_json` agrega; legado espelha primário; erros
  400/403/409); dual migrations sqlite+pg; prompt lista mounts;
  RepoContextLoader multi-root; UI chips+badges (★) + i18n ×8 sorted;
  CLI `--repo` repetível; MCP `selected_repositories`; Git `?repo=`.
- Verde: build 0/0, unit 953/953 (~30 novos Spec146), client 147/147,
  sandbox 10/10, integration 262/263 (1 flake ambiental
  FrameworkAssets "client aborted" — passou isolado), format clean,
  5 drift guards OK, golden regen.
- Deferido documentado: Ask citations `repo:` prefix (degrade →
  primário; indexer já per-(repo,branch)).

## Sessão 2026-11-11 (cont. 3) — SPEC-145 repo-wiki-mcp ENTREGUE (issue #410)

- **WIP herdado** (sessão anterior, uncommitted) revisado por inteiro e
  entregue: **PR #420 merged** (`d1650b0`, squash), issue #410 closed +
  done. Spec arquivada `docs/specs/` (commit `075f2be` direto em main).
- `IRepoContentProvider` (clone shallow via conexão SCM + GIT_ASKPASS
  fora de argv | local fallback) → `RepoIndexer` indexa a árvore real
  por branch + persiste `CommitSha`; webhook `push` (GH+GL) re-indexa
  (`Indexing:AutoRefreshOnPush`) e marca wiki pages stale (diff-aware
  por `source_files`).
- Wiki: `wiki_runs`/`wiki_pages` (migrations duplas) + `WikiRunWorker`
  (fila in-process) + `WikiRunner` (effort 5/15/40, `.sandcastle/wiki.json`,
  citações validadas contra índice → permalinks; inválida → stale).
  UI `/wiki` (árvore+markdown+mermaid lazy, i18n ×8). Ask: permalinks
  nas citações + `Wiki:AskEnrichment`. MCP: `sc_wiki_structure`/
  `sc_wiki_contents`/`sc_ask` + `--http :PORT` (sk-* + CIDR allowlist;
  stdio default). Golden MCP 15 tools. Perms `indexing.manage`/`wiki.read`.
- Verde: build 0/0, unit 921/921 (33 Spec145), client 147/147, format
  clean, 6 drift guards, boot smoke OK. Integration serial nesta box:
  256/258 — 2 flakes ambientais conhecidos (Spec112 dev-login timeout,
  Spec42 etag) passam isolados (rerun = 2/2).
- Gotcha: `gh pr merge --auto` mergeou na hora (repo não exige check
  verde). CI main red por flake pré-existente
  `SandboxAgentTests.ExecStream_Sse_Chunks_And_Terminal_Exit` (SSE
  stdout/stderr coalescing; passa local 768ms; falhava em main antes
  do merge — fora do escopo, precisa quarantine/fix dedicado).

## Sessão 2026-11-11 — SPEC-142 QA agent com evidência (issue #407)

- **WIP herdado**: sessão anterior deixou a implementação INTEIRA
  uncommitted em `main` (~3.700 linhas novas); movida para
  `feature/spec-142-qa-evidence` via checkout (mesma HEAD).
- Pipeline: webhook `pull_request` opened/synchronize (GH) +
  open/update (GitLab) → `QaService` (dedup repo+pr+head_sha, quota
  diária, `QaCovers` enrollment) → `QaRunWorker` → `QaRunner` 4 fases:
  Understand (LLM → `QaPlan` em plan_json), Setup (clone head exato +
  provision + sandcastle.yaml/detecção + app start/readiness),
  Exercise (exec+browser via `IQaBrowserDriver` — `qa-driver.mjs`
  Playwright no sandbox, page.video→webm; caps MaxSteps/MaxMinutes →
  PARTIAL honesto; bugfix reproduce-on-base), Report (verdict +
  comentário + `sandcastle/qa` advisory + artifacts `qa_screenshot`/
  `qa_video` auth'd em `/api/v1/qa/runs/{id}/artifacts/{ref}`).
- `scm_connections.qa_{enabled,repos_json,pass_condition}` + tabelas
  `qa_runs|qa_steps` (dual migrations já geradas); admin toggle
  `admin.scm_qa` ×8; `QaOptions` (MaxSteps/MaxMinutes/MaxDaily=30).
- Único fix meu: placeholders literais `{repo}/{diff}` removidos de
  `prompts/qa-plan.md` (contexto via user msg, como `pr-review.md`).
- Verde: build 0/0, unit 892/892 (37 novos), format clean, golden
  regen, drift guards OK. Deferidos: slash `/qa`+card (sem precedente
  — mesmo defer de SPEC-140), real-browser E2E em CI.

## Sessão 2026-11-10 (cont. 3) — SPEC-141 CI autofix loop ENTREGUE

- **PR #416 merged** (`a750cba`, squash), issue #406 closed auto +
  `in_pullrequest`; spec arquivada em `docs/specs/` (`0fbf6f9`,
  push direto em main seguindo precedente PR #415).
- Pipeline
  completa: webhook `check_run|check_suite|workflow_run|status` →
  `CiCheckEventParser` → `AutofixService` (política: falha, escopo repo,
  check-name glob, anti-loop app/ator/head_sha_out, fork, dedup
  head+check + 1 ativo/PR, quota diária) → `AutofixRunWorker` paralelo →
  `AutofixRunner` (log bounded+redacted → `CiFailureClassifier`
  lint|type|test|build fixáveis vs infra|timeout|flaky|permissão|segredo
  blocked → conversa `agent_type: autofix` → poll checks → verde/cap).
- Novos: `IScmProvider` +4 métodos (check runs/logs, workflow logs zip,
  commits/{sha}/pulls); `IGitService.ProvisionTokenAuthAsync` +
  `IGitHubClientFactory.GetInstallationTokenAsync` (push c/ credencial
  da conexão); `scm_connections.autofix_*` + tabelas
  `autofix_runs|autofix_attempts` (migrations SQLite+Postgres);
  endpoints `POST /scm/autofix`, `GET /scm/autofix-runs[/{id}]`,
  `POST …/cancel`, PATCH connections `autofix_*`; toggle admin + 8
  locales; `AutofixOptions` (MaxAttempts=3, MaxLogKb=256,
  PollSeconds=60, RunTimeoutMinutes=45).
- Gotchas: EF não traduz `IReadOnlySet.Contains` → `string[]`;
  `dotnet ef` precisa `--startup-project src/Sandcastle.Api`; Octokit
  `client.Connection.Get<T>` retorna `IApiResponse<T>` (usar `.Body`);
  `UntrustedInput` vive em `Infrastructure.Security`, `SecretRedactor`
  em `Infrastructure.Agent`.
- 40 testes novos `Spec141AutofixTests` (parser, webhook, política,
  classificador, runner green/red/cap/blocked/cancel) — 855 unit OK;
  integration: 4 flakes ambientais (timeout 100s dev-login sob carga)
  passam no rerun.

## Sessão 2026-11-10 (cont. 2) — SPEC-147 product-polish ENTREGUE

- **PR #413 merged** (`5905404`), issue #412 → in_pullrequest.
- 5 RFs: SlackTextSanitizer (ZWSP) no SlackChatSink + ChatOpsOptions
  AllowGroupMentions/org `chatops_allow_group_mentions`; GitAuthorResolver
  user→org→global + Co-authored-by (Git:CoAuthorUser, e-mail verificado);
  FilesTab .md Rendered/Source + .patch/.diff diff highlight (Monarch
  mínimo no monacoInterop); GET /usage/budget + card Settings>Usage;
  GET /indexing/status + badge Home + aba Indexing c/ trigger.
- Settings stores: `users.preferences_json` e `organizations.settings_json`
  (key-value, sem migration) — padrão adotado p/ novos settings.
- Gotchas: golden OpenAPI regen via `SANDCASTLE_UPDATE_GOLDEN=1` (só
  /usage/budget — grupo ask/indexing é ExcludeFromDescription); suite
  integration paralela flakea com timeouts HttpClient 100s em dev-login
  sob carga — rerun serial confirma verde (65/65).

## Sessão 2026-11-10 (cont.) — Competitive gap analysis → SPECs 140-147 Draft

- Relatório: `docs/research/2026-11-competitive-gap-analysis.md` (recriado;
  versão anterior nunca foi commitada).
- Pesquisa web 2026-11: OH Ent 0.70–0.103 (multi-repo conversations,
  Model Router, super-admin, Your Budget, automation git sync, service
  accounts, Bitbucket DC); Verification Stack (critic→review→QA+iterate);
  Devin (Review+AutoFix checks, Code Scans/Security 2.0 findings,
  stacked PRs, vídeo, handoff/plugins, DeepWiki+MCP, Outposts/RBAC).
- 8 SPECs Draft em `.specs/` (índice atualizado):
  140 pr-review-agent · 141 ci-autofix-loop · 142 qa-agent-evidence ·
  143 code-scans · 144 batch-refactor · 145 repo-wiki-mcp ·
  146 multi-repo-conversations · 147 product-polish (5 quick wins).
- Evidências código: `GitHubWebhookService` só @mention;
  `IScmProvider` sem CreateReview/logs; `SwarmOrchestrator`=stub;
  `RepoIndexer` lê cwd da API; `Sandcastle.Mcp` stdio-only 12 tools;
  `ChatOpsReporter` sem sanitize pings; `GitOptions` author só global.
- Decisões conservadoras: review comment-only, autofix cap 3,
  A2A fora da v1.0, stacked opcional GitHub-only (144).
- Gate: aguardando aprovação das SPECs → depois create-issues (Epic).

## Sessão 2026-11-06 — Epic #152 Sonar debt (tail)

- **SPEC-103 closed**: PRs #357/#358/#359/#360 merged (~95 handlers
  extraídos; Org CC105→~1, Scim CC102, AppConv CC75+24). Archive PR #361.
- **SPEC-104/105/106 closed**: PRs #352/#353/#354+#355+#356.
- Restante epic #152: S3776 fora de Endpoints (PermissionPolicy,
  Program.cs, Client×5, Eval×2, Infra ~30 incl. AgentRunner CC210),
  githubactions:S7679 (protegido), js:S1874 won't-fix, css S9423 excluído.
- Flakes conhecidos CI: SkillsHooksE2E, Spec43, Spec42, Spec36,
  AgentEngineE2E, SandboxAgent SSE — rerun resolve.

## Sessão 2026-11-04 — implementação SPECs 116-124 (Epic #317)

- **Branch**: `feature/spec-117-pwa-assets` (commits empilhados por fatia).
- **Commits entregues**:
  - `e5ee6f4` docs: SPECs 116-124 Approved (#318-#326)
  - `c417d1a` SPEC-117 (#320): manifest.webmanifest, service-worker.js
    (fw cache-first + shell network-first, /api+/hubs nunca cacheiam),
    favicon.svg + assets/icons/{192,512} + splash-dark + logo (PIL),
    metas PWA, SW registration, `lang` pt-BR+ohn.locale boot,
    removidos `lib/bootstrap` (~1.5MB, 0 refs) e `sample-data/`
  - `42758ad` SPEC-116 (#318): `Styles/app.tailwind.css` portado do
    OWUIC — @theme oklch gray ramp (750/850/925), custom-variant
    `dark:` → `[data-theme='dark']`, classes componentes completas
    (page/card-*/menu/modal-*/input/toggle/codeblock-*/ide-*),
    @tailwindcss/typography (npm devDep), js/codeblocks.js
    (`window.ohn.codeBlocks.enhance` em EventCard MessageEvent),
    themes/builtin.json 4 presets + ThemeCssBuilder (tokens→--ohn-*),
    ohnTheme.setCustom + ThemeService.LoadPalettes/SetPaletteAsync,
    re-apply pré-paint via ohn.theme.id/css. tailwind.css 22→111KB.
  - `11f9498` SPEC-118 slice (#321): menu ⋯ por conversa no sidebar
    (Copy link + Delete c/ confirm), icons dots/trash, 4 keys i18n.
- **Verificações**: Client build Release 0 warn/err; check-css-classes,
  check-form-a11y, i18n-parity (458 keys), i18n-placeholders — todos OK.
- **Pass 2 (continue)**: commits `e691f32` 118+119 (AttentionFeed no
  sb-head desktop; DialogService+DialogHost confirm/prompt acessível,
  ohn.trapFocus, delete via ConfirmAsync — substitui window.confirm),
  `4a00810` regen tailwind, `00f546d` 120 (ToolActivityMap+
  ToolArgsPreview+GitDiffRender ports; label de atividade no ActionEvent;
  DiffTab por arquivo; ChatNotificationsService+js opt-in; palette+
  notify toggles em Settings), `73fa636` 121 slice (BuiltinFileOps:
  file_list/grep/glob/apply_patch → 21→25 tools), `9ddd15f` i18n real
  ja/zh/es/fr/de/it + fix ThemeService ctor nos testes.
- **Verificado**: `dotnet build OpenHandsNET.slnx -c Release` 0/0;
  Client.Tests 86/86, UnitTests 663/663, IntegrationTests 235/235;
  guards css/a11y/i18n OK.
- **Pendente**: resto 121 (jobs/context/notebook/plan/worktree), 123
  (hooks/ruleset/judge/tool_search/agents embutidos), 122 (LSP), 124
  (MCP/ACP). SettingsModal (119) pendente. PR + archive ao final.
- **Gotchas**: `MapStaticAssets` serve SW/manifest ok; icons gerados
  via PIL (convert renderiza SVG em grayscale); `GetFromJsonAsync`
  precisa `using System.Net.Http.Json`; `confirm()` JS usado p/ delete.

## Sessão 2026-11-04 — gap-analysis vs OpenWebUiCode → 9 SPECs Draft

- **Leva gap-20261104**: 116 design-system (tokens oklch + classes
  componentes OWUIC), 117 PWA/assets (+remover `lib/bootstrap` morto),
  118 sidebar/rail/menu-por-conversa+bell, 119 DialogService/Host+
  SettingsModal, 120 tool-call cards/prompt cards/GitDiffRender,
  121 tools 21→~55 (file ops, apply_patch, jobs, context, memory,
  plan, worktree), 122 LSP no sandbox, 123 hooks+ruleset+judge+
  tool_search+agents/prompts embutidos, 124 `OpenHandsNET.Mcp`+ACP.
- Números livres confirmados: 116–124 usados. OWUIC embute
  `agents/*.agent.md`×4 — nosso catálogo (SPEC-95) embute 0.

## Sessão 2026-10-26 — SPEC-114/115 (tema claro + proxy sandbox)

- **SPEC-114** (#300): default light theme, Inter self-hosted, tokens
  refined — merged.
- **SPEC-115** (#301): `ResolveSurfaceUrl` extraído; em `internal`
  RoutingMode o client só recebe path same-origin do proxy (teste de
  contrato `Spec115SurfaceUrlTests`). Warning de boot se
  `PublishAgentPort`+`AgentHost` → 0.0.0.0. Guard
  `tools/check-no-direct-sandbox-url.sh` (sh, standalone — CI não
  alterado). Docs `docs/security/sandbox-networking.md` + `en/`.
- Gotcha: `BuildHostConfig` é static → primary-ctor `logger` não pode ser
  usado dentro (CS9105) — passei `ILogger?` como parâmetro.
- `dotnet format` exige final newline exata; e
  `tools/check-spec-status.py` não existe neste repo (é do open-webui).

## Sessão 2026-10-23 (cont. 3) — SPEC-98 i18n COMPLETO + stack de PRs

- **SPEC-98 entregue** (#250/#244): 6 PRs stacked **#256–#261**
  (zh→de→es→fr→it→ja). Baseline i18n-parity **0 signatures** —
  ratchet agora protege paridade total nos 8 locales (451 keys cada).
- **Stack sequencial main→#261**: #254(SPEC-96) → #255(SPEC-97) →
  #256(zh) → #257(de) → #258(es) → #259(fr) → #260(it) → #261(ja).
  **Merge estrito na ordem**; após cada squash o diff 3-dot do
  próximo encolhe sozinho. Branches i18n foram criados em cadeia
  (sem voltar à main) — formato correto p/ baseline compartilhado.
- **Gates pendentes**: #254/#255 tocam `.github/workflows/` →
  hard rule 6, revisão humana. SPECs **101–106** (filhos SPEC-89:
  S3776 cli/agent/endpoints, S107, S3218+S127, S1075+S3903) estão
  **Draft** — precisam aprovação antes de implementar.
- **SPEC-97** (#255): FallbackPolicy default-deny + AllowAnonymous
  nos públicos; 500→4xx (Guid.TryParse, JsonElement guards, OIDC
  404, secrets duplicado removido de SettingsEndpoints); nightly
  fuzz com cookie dev-login; 28 testes boundary; golden regen.
- Sonar live truth: queries exigem `resolved=false` (sem isso
  counts inflam ~350 issues resolvidas). Exclusões de docs/
  alinhadas ao ci.yml (dotnet-scanner ignora sonar-project.properties).
- `tools/sonar-live.json` é snapshot gerado — não commitar.

## Sessão 2026-10-23 — Epic #231 COMPLETO (6/6 SPECs)

- **Merged + arquivadas**: #232/SPEC-91 (PR #238), #233/SPEC-90 (#239),
  #234/SPEC-95 (#240), #236/SPEC-93 (#242), #237/SPEC-94 (#243),
  #235/SPEC-92 (#241 — workflow diff revisado e aprovado pelo usuário).
- Epic #231 fechado. SPECs 90-95 todas em `docs/specs/` (`e72facb`).
- Issue **#244** parcial (PR #245 merged `e3ae4aa`): css/a11y/placeholders
  zerados (baselines regenerados); **resta i18n-parity** ~1720 chaves —
  runtime já cai p/ en-US→pt-BR, slices por locale continuam na #244.
- Flake CI: integration tests docker (Spec42/43/AgentEngine) falham
  intermitente — `gh run rerun <id> --failed` resolveu 2× nesta sessão.
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

## Sessão 2026-10-22 (SPEC-72)

- SPECs 70/71 já entregues (PRs #159/#160) — arquivadas em docs/specs/ (cf49e92).
- SPEC-72 entregue: PR #246 merged (8c19236). Snapshot 19/10 stale —
  44/45 já resolvidas; S2699 real fix. S3218/S3776 → SPEC-89.
- SPEC-73: PR #247 — verified subset fix; ~200 stale/vendored/defer; rescan pend.
- SPEC-74: auditar staleness antes (mesmo snapshot stale).

## Sessão 2026-10-23 (SPEC-73)

- SPEC-73 auditada por regra: snapshot 19/10 majoritariamente stale
  (S3358 69→0, S108 54→2, S6966/S6580/S7688 já fixos).
- Fixes reais: S1117×2, S4581, S1121×2, S1118, S2743, S2925, S108×2,
  JS S7761/S1121/S1871, CSS S4666×3 — PR #247.
- S107/S127 → SPEC-89; monaco/xterm vendored; !important reduced-motion
  = justified; S1172×10 + S1854/S2376/S6673/k8s unverified (line drift).
- Lição: heurística bulk por regra > site-a-site quando snapshot é stale.

## Fatos operacionais

- `scripts/check-spec-lifecycle.sh` → ok (nenhuma spec terminal em `.specs/`)
- SPECs ativas: 72-74 (Approved, Epic #152 — 70/71 arquivadas), 89 (Draft)
- SPEC-72: PR #246 merged — S2699 fix; resto stale/defer (#154 aberto c/ nota)
- Próximos números livres: 96+
- SonarCloud: `https://sonarcloud.io/summary/overall?id=afonsoft_OpenHandsNET&branch=main`
- Bloqueio persistente: #148 — `SONAR_TOKEN` secret + `SONAR_ENABLED=true`
- Clone open-webui p/ re-auditoria: `/home/ubuntu/repos/open-webui`

## 2026-10-25 — SPEC-107 executada
- PR #270 (housekeeping) merged → SPECs 96-98/101-102 arquivadas, 107-110 Approved.
- SPEC-107 → **PR #271**: 34/35 sites S3776 decompostos; `AgentRunner.LoopAsync`
  (CC~210) deferido p/ fatia dedicada (core loop, alto blast radius).
- Filas: #266 SPEC-107 (em PR), #267 SPEC-108 (vuln/bug triage),
  #268 SPEC-109 (tail rules), #269 SPEC-110 (flakes).

## Estado atual (2026-10-24/25 — sessão gap-analysis followup)

**Estado final (2026-10-25):**
- Epic #265 COMPLETO: **PRs #271–#274 todas merged** (`b62647b`, `a6b50ba`,
  `99eb329`, `3dfb87a`). Issues #266–#269 fechadas.
- SPECs 107–110 arquivadas em `docs/specs/` (housekeeping chore).
- 0 PRs abertas; branches feat/* deletados (local+remoto).
- `tools/sonar-live.json` removido da árvore (artefato commitado por
  engano no batch 1 do #271 — dropado antes do squash).
- Pendente p/ próx. gap-analysis: `AgentRunner.LoopAsync` (CC~210)
  dedicado; #248 verificação de exclusões sonar docs/monaco; #218
  nightly pós-#255.

**Lições:** renames S927 NÃO podem tocar named-arg labels (`ct:` = nome
do param do callee); `insert_final_newline=false` no .editorconfig;
S107 fix via record-bundling; regex sobre chamadas multiline pode comer
args do CORPO — restringir à signature.


## 2026-10-25 — SPEC-111/112 implementadas

- **PR #278** `feat/spec-112-fuzz-round2`: dev-login email ausente→400; usage-override/webhooks JsonException→400; webhook conv desconhecida→404; mcp/tool-call server vazio→400; `GET /keys` DateTimeOffset→ordenação em memória (SQLite); OpenAPI documenta 503 legit (checkpoint/tool-call); nightly fuzz auth cookie→Bearer `sk-*` mint via POST /keys. 10/10 testes Spec112 verdes.
- **PR #279** `feat/spec-111-loop-decomposition`: `LoopAsync` (CC~210, ~890 linhas) → 
## SPEC-131 (2026-11-05) — sandbox browser
- PR #339 (branch feature/spec-131-sandbox-browser, 2 commits): runtime/Dockerfile
  com playwright@1.64.1 + chrome-devtools-mcp@1.12.0 + chromium em
  /opt/ohn/ms-playwright (/usr/local/bin/chromium, CHROME_PATH/PUPPETEER_EXECUTABLE_PATH);
  McpClientService.DockerExecArgs encaminha cfg.Env como docker exec -e (bug: era descartado);
  McpSettingsService.BuiltinServers + MergeBuiltin — chrome-devtools headless/isolated,
  override por mesmo nome. Docs em docs/mcp-server.md. 4 testes novos (706 unit).
- Pendente: merge #339 → arquivar SPEC-131 → fechar #338. Build real da imagem
  Docker (pesado) ficou como validação manual.
- DONE: PR #339 mergeado (bfa4d94), SPEC-131 arquivada via PR #340 (8bcca1f),
  issue #338 fechada, branches deletadas. main limpa.
- DONE: Dependabot #2 fixed via PR #341 (merge 494bc6e) — npm override
  postcss-selector-parser → 6.1.5 (CVE-2026-104844, transitiva de
  @tailwindcss/typography que pinna 6.0.10). npm audit 0 vulns.
- DONE: validação E2E SPEC-131 — `docker build` runtime OK (Chrome for Testing
  156.0.8078.4 em /opt/ohn/ms-playwright); como user openhands: chromium --version
  OK, playwright screenshot funcional, chrome-devtools-mcp responde initialize+
  tools/list via `docker exec -i` (caminho real do McpClientService). Imagem
  local: openhandsnet-runtime:spec131.
- #314: PR #342 mergeado (62bc0ba) — bare-400 rewrite cobre /api/**/oauth/**;
  388 leaks no run; issue aberta até nightly confirmar.


## 2026-11-10 — sonar tail + .agents rename (sessão atual)

- **PR #400 merged**: `.claude/` → `.agents/` (skills afonsoft/skills
  instaladas; `.claude/settings.json` retido p/ permissions.deny;
  sonar+gitleaks excluem `.agents/**` vendored).
- **PR #399 (sonar tail)**: 47 issues do epic #152 endereçadas —
  fixes reais (SCM delete org-scoped/IDOR, WebSearch endpoints via
  config `WebSearch:Endpoints:*`, EndpointTags.Api, extractions
  S3776, S1172/3358/1117/6667/2325/3220/108) + NOSONAR justificado
  (S107×16 SPEC-104, css reduced-motion, js execCommand, k8s SA).
  Aprendidos: method-group handlers ganham tag auto do tipo (.NET 10)
  → lambdas 1-linha preservam contrato; multicriteria do
  `.sonarcloud.properties` NÃO suprime no AutoScan → NOSONAR é o
  mecanismo real.
- **SPEC-139 draft** (brand assets) commitada em `.specs/` na branch.
- Epic #152: atualizar corpo/fechar após merge + rescan zerar.
- logs/openhandsnet-*.log removidos (local, untracked).
- SandboxAgentTests.ExecStream_Sse flake SSE-timing conhecido no CI.

## 2026-11-10 — session close
- PRs merged: #400 (.agents rename+skills), #399 (sonar tail 47), #401 (residual 5).
- Sonar main: 1 issue = k8s S6865 FP (SA bound via rbac.yaml). Pendente humano: wont-fix na UI (sem SONAR_TOKEN local).
- Epic #152: comentário com balanço postado; mantido aberto até o wont-fix.
- Lições: multicriteria .sonarcloud.properties NÃO suprime no AutoScan (só NOSONAR linha); method-group handlers criam tag auto do tipo (.NET 10) → lambdas 1-linha preservam OpenAPI; PR test policy exige tests/ junto de prod .cs.

## feat/host-installer (merged #402)
- `install.sh` na raiz: install/update/uninstall, valida+instala .NET SDK 10 e Docker,
  publish /opt/sandcastle, user+dirs, Playwright chromium, runtime image (pull||build),
  /etc/sandcastle/env, systemd unit autostart, health check /api/v1/ready.
- `update`: fetch+pull main → redeploy só se mudou (--force força); sem install → full install.
- Docs: seção Installer em host.md pt+en, index rows, README.md/en one-liner.
- Sonar pegou 23 issues shell → fix: [[ ]], --proto=https curl, args nomeados, case default.
