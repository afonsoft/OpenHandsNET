# Session Memory — 2026-10-16 — SPECs 41-47 (open-webui wave)

## Scope
7 SPECs da análise `afonsoft/open-webui` (MIT — port permitido) →
OpenHandsNET, escritas, aprovadas e vinculadas a issues.

## Key state
- Branch/PR: `feature/spec-41-47-openwebui-fronts` → PR #106.
- Issues: #99 SPEC-41, #100 SPEC-42, #101 SPEC-43, #102 SPEC-44,
  #103 SPEC-45, #104 SPEC-46, #105 SPEC-47 (labels feature+todo).
- SPECs: `.specs/41_stream_chat_ux` … `47_automations` (Status Approved).

## Decisions (travadas c/ usuário)
- IDE = Monaco editável (não CodeMirror/LSP — defer).
- Chat = SignalR (não SSE do fork); reasoning via `DeltaFrame.channel`
  + `ActionEvent.ThinkingBlocks` (campos Domain já existem, sem produtor).
- Checkpoints = git-dir `/workspace/.ohn-checkpoints` + alternates via
  `ExecAsync`; sem fallback manifesto; 409 run_active/drift.
- Attention = projeção persistida (migration ×2) + push SignalR, sem poll.
- Composer: anexos = upload `/workspace/uploads` + path ref; voice fora.
- i18n = JSON LocalizationService (port), pt-BR default, parity=xUnit test.
- Automations: exec = `POST /app-conversations`; calendar grid defer.

## Facts úteis
- Sandbox-agent já tem `PUT /files` + `/exec/stream`; Api só faz proxy GET.
- `GitService.CommitAsync` existe mas sem endpoint (`POST /{id}/git/commit`
  é da SPEC-42).
- `ConversationRecord` não tem `pending_action` persistido (SPEC-44
  adiciona `NeedsAttention*`).
- `Pause` existe no hub; `Resume` não (SPEC-41 adiciona).
- Migrations sempre gerar nos 2 contextos (SQLite + Postgres).

## Next
Merge PR #106 → executar na ordem `41 → (42 ∥ 43 ∥ 44 ∥ 45 ∥ 46 ∥ 47)`
(45 usa superfícies de 41; 42 embebe CheckpointList de 43; 46 sweep por
último). Via `execute-specs`/`tdd-spec` por issue.

## SPEC-45 (PR #111)
- ChatComposer compartilhado Home/ChatPane; @mention varre workspace via
  `find` no /exec (exclui .git/.ohn-checkpoints); /slash = builtins +
  skills da conversa (GET /{id}/skills) com $ARGUMENTS/$1..$N.
- Upload: POST /{id}/uploads multipart `file` ≤10MB → ISandboxExecutor
  .WriteFileBytesAsync (b64 chunked, MAX_ARG_STRLEN-safe); override em
  SandboxAgentExecutor via PUT files(b64)+decode; DockerSandboxExecutor
  cai pro chunked quando content >48KB (bug latente SPEC-42 corrigido).
- ReadFormAsync sem boundary → 500: endpoint envelopa em try/catch →
  400 problem-details.
- Fixtures bUnit antigos quebraram com ChatComposer (ToastService/Api/
  JSInterop.Loose) — registrar nos ctor de ConversationComponentTests
  e HomePageTests.
- CI flakes conhecidos: testes E2E Docker (sandbox_not_running / poll
  deadline) falham sob carga do runner e passam em rerun — SPEC-44 PR
  #110 precisou 2 reruns.

## SPEC-46 (i18n) — PR #112
- Worktree `ohn-spec46`, branch `feature/spec46-i18n` (baseada na main pós-45).
- `LocalizationService` port do open-webui: fallback active→en→pt→key, `{{name}}`,
  `ohn.locale`, `ohnI18n.setLang`, init antes do `RunAsync`, `Changed` no MainLayout.
- Sweep: 44 razor → chaves `area.element`; lookups dinâmicos (`L[label]`, `L[tip]`,
  `kind_{k}`). Settings ganha dropdown de idioma (RF-004 → `PUT prefs {locale}`).
- 277 chaves en/pt completas + 94 chaves de chrome nos 6 idiomas best-effort
  (resto cai no fallback en-US por design).
- Gotchas Razor: `value="@code"` vira diretiva @code (renomear var); `@inject`
  dentro de `@* *@` não compila; `attr="@L["k"]"` OK (expressão implícita c/ indexer).
- `L10nStub.Register(this)`: fixture bUnit serve JSONs reais via DiskHandler;
  asserts de texto migrados p/ pt-BR (default). Parity test cobre manifesto,
  keyset en=pt, sem chaves alienígenas e placeholders iguais.
- Suite local: 659/659. Issue #104 → in_pullrequest.

## SPEC-47 (automations) — branch `feature/spec47-automations`
- `IAppConversationService.CreateForUserAsync(userId, orgId, req)` — caminho
  de impersonação p/ triggers fora de HTTP (CreateCoreAsync compartilhado
  mantém plan enforcement + MessageEvent inicial).
- `AutomationSchedule.ComputeNextRun` puro: interval≥1 clamp, daily/weekly
  HH:mm UTC, weekday clamp 0..6 (7→Saturday, NÃO wraparound), once esgota.
- Records: `AutomationRecord` (IOrgScoped, NextRunAtTicks), `AutomationRunRecord`
  (StartedAtTicks — SQLite não ordena DateTimeOffset, mesmo padrão de
  NextRunAtTicks), `AutomationHookRecord` (sha256 token, exibido 1×,
  prefixo `ohn_hook_`). Migrations nos 2 contextos.
- `AutomationService`: CRUD owner-scoped, FireAsync (dedup running→skipped,
  once→Enabled=false, agent_diagnostic `automation:{id}` no stream, audit
  `automation_fired`/`automation_run_created`, quota→`error="quota: …"`),
  TickAsync (due + reconcile com ExecutionStatus da conversa).
- `AutomationScheduler` BackgroundService 30s + distributed lock
  `automation-scheduler` (padrão OrphanSweepService).
- Endpoints `/api/v1/automations` CRUD+run-now+runs+hooks;
  `POST /api/v1/hooks/{token}` anônimo → 202/404; `HookRateLimiter`
  in-memory (10/min token, 60/min IP, `Reset()` p/ testes).
- Gotcha EF: DELETE cascade manual via ExecuteDeleteAsync (runs+hooks).
- Tool `routine` builtin (Risky → gate confirmation-mode): list/create/
  enable/disable/delete do owner da conversa via AppDbContext+svc.
- UI: `/automations` + `/automations/{id}` + sidebar (icon clock);
  chaves i18n nos 8 locales (parity exige keyset ⊆ en-US).
- bUnit: pages precisam L10nStub + ToastService + Api stub.

## Docs/specs wave (PR #118, merged 860de66)

- SPECs 48–51 created **Approved** on `feature/specs-48-51-docs` (issues
  #114–#117): org security_policies + fail-closed resume; k8s warm-pool/
  sleep + production Helm + kind/sysbox runbook; BYOR LiteLLM; published
  OpenAPI + golden snapshot + Scalar. Build order `48 → (49 ∥ 50 ∥ 51)`.
- README rewritten (pt-BR) + `README.en.md`: comparison table vs
  OpenHands OSS/Cloud/Ent and Devin Web; legend "em SPEC-N" = approved.
- `docs/README.md` + `docs/en/README.md` indexes; `docs/comparison.md`
  + `docs/en/comparison.md`; pt-BR feature guides ×6 under
  `docs/features/`; full EN mirror under `docs/en/` (21 files).
- Status wording fixed: SPECs 48–51 described as approved roadmap, not
  delivered (k8s engine exists but warm-pool/Helm completion is SPEC-49).
- Next: implement SPECs in order — 48 first, then 49/50/51 in parallel.

## SPEC-48 — merged (PR #119, 06cc540)

security_policies por org (SQLite+Postgres migrations): presets
permissive|balanced|strict + overrides por campo (analyzer_override
pattern/llm/off, ask_mcp_tools, min_confirm_risk, resume_strict).
ISecurityPolicyService + IDistributedCache 60s + invalidação.
AgentRunner resolve policy 1× por run: analyzer override com fallback
diagnostic `security_analyzer_fallback`, floor min_confirm_risk,
mcp__* ask — `permissive` é opt-out explícito do piso MCP do guardrail.
GET|PUT /api/v1/organizations/{id}/security-policy (member read,
perm:edit_org_settings, PUT null = reset balanced, audit
security_policy_updated). AgentLlm:ResumeEnforcement open|strict
(alias legado StrictResume); org resume_strict vence global; strict =
resume_drift error + run Error, tools aditivas OK. UI /admin/orgs +
i18n 8 locales. Docs deployment/security pt-BR+en.
Testes: 705 local verdes (2 flakes Docker passam isolados).
CI: verde no rerun (2 flakes: Spec36 event bus cancel + SkillsHooks
Docker timing — padrão conhecido).
Próximo: SPEC-49 (k8s) ∥ SPEC-50 (BYOR) ∥ SPEC-51 (OpenAPI).
