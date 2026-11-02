# memory.md — estado corrente (max 100 linhas)

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