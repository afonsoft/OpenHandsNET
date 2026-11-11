# SPEC — 142 · QA Agent com evidência (Understand→Setup→Exercise→Report)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `qa-agent-evidence` |
| Type | `Feature / Agent + Sandbox + SCM` |
| Stack | `.NET 10 · Playwright no sandbox (SPEC-131) · artifacts` |
| Repository | `afonsoft/sandcastle` |
| Status | `Delivered` |
| Ticket | `GAP-AGENT-QA-EVIDENCE` (docs/research/2026-11-competitive-gap-analysis.md §G3) |
| Depends on | `18` (browser tool), `131` (Playwright+chrome-devtools na imagem), `34` (artifacts), `140`/`141` |
| Reference | OH QA plugin (`qa-changes`, 4 fases + PASS/FAIL/PARTIAL); Devin "Testing & Video Recordings" — conceitos apenas |
| Touches | `src/Sandcastle.{Application,Infrastructure,Api,Client}`, `runtime/`, `tests/` |

## 1. User Story

**As a** revisor de PR
**I want** que um agente de QA execute o software alterado (browser, CLI,
API) e anexe ao PR um relatório com veredicto e evidência (screenshots,
vídeo curto, comandos+outputs)
**So that** eu aprovo com confiança sem baixar a branch — paridade com o
QA agent do OpenHands e com os vídeos de teste do Devin. Hoje o
Sandcastle só produz screenshots pontuais; não existe pipeline de QA.

## 2. Scope (RF)

### RF-001 — Trigger

- Automático: `pull_request` opened/synchronize em repo inscrito
  (`QaEnabled` na connection) **ou** novo automation trigger
  `scm_event:pull_request` (reusa RF-001 da SPEC-140).
- Manual: `POST /api/v1/qa/runs {repository, pr_number}` + slash `/qa`
  no composer + card "Run QA" na página da conversa cujo PR existe.
- Dedup por `(repo, pr, head_sha)` — mesmo modelo do SPEC-140.

### RF-002 — Pipeline (4 fases — `IQaRunner`)

1. **Understand**: lê diff + título/descrição → classifica mudança
   (`feature|bugfix|refactor|config`) → extrai entry points
   (rotas API, páginas, comandos CLI) → plano de teste mínimo
   (`qa_plan` persistido).
2. **Setup**: sandbox novo (ou reuse do sandbox da conversa quando o PR
   veio dela) → clone head SHA → build/install conforme
   `sandcastle.yaml`/detecção → registra estado do CI do PR.
3. **Exercise**: executa o plano com browser tool (Playwright —
   screenshots por passo), `exec` (CLI/API curl), start de servers;
   para bugfix: tenta reproduzir no base branch e verificar no PR.
   Bail-out: `Qa:MaxSteps=30`, `Qa:MaxMinutes=30` — desiste declarando
   o que tentou.
4. **Report**: veredicto `PASS|FAIL|PARTIAL` + evidência + resumo →
   comentário estruturado no PR + `sandcastle/qa` commit status
   (`completed` = pass/fail pelo veredicto? — pass-condition
   `completed` default: status é "terminou", não "aprovou"; opt-in
   `passed`).

### RF-003 — Evidência

- Screenshots por passo (já existe no browser tool) → `IArtifactStore`
  com ACL do PR/conversa.
- **Vídeo**: captura da sessão Playwright (CDP screencast ou
  ffmpeg/xvfb no runtime — decisão de implementação: preferir
  `page.video()` do Playwright já incluído na imagem; fallback
  screenshots→gif). Duração alvo ≤ 2 min, anotações de fase
  (overlay textual simples nos keyframes, se viável — senão log de
  timestamps `qa_steps`).
- Artefatos linkados no comentário do PR (URLs assinadas/public-share
  existente — **não** upload binário para o provider; o comentário leva
  links + até 4 imagens inline via issue-asset quando suportado).
- `qa_steps`/`qa_evidence` records listando comandos, exit codes,
  artefato, timestamp.

### RF-004 — Fora de escopo

- Performance/load testing · QA em mobile/desktop real · vídeo 60fps ·
  anotações avançadas de vídeo · QA sem PR (exploratório) — fase 2.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| POST | `/api/v1/qa/runs` | `{repository, pr_number}` → `{qa_run_id}` (202) |
| GET | `/api/v1/qa/runs` | filtros `repository, pr, verdict` |
| GET | `/api/v1/qa/runs/{id}` | run + plan + steps + evidence links |

## 4. Data model

- `scm_connections` + `QaEnabled`, `QaReposJson`.
- `qa_runs` (id, repo, pr, head_sha, verdict, classification,
  plan_json, conversation_id?, created/finished).
- `qa_steps` (id, run_id, n, action, target, outcome, artifact_id?,
  duration_ms).
- Artifacts via `IArtifactStore` (kind `qa_screenshot`/`qa_video`).

## 5. NFR / Security

- QA sandbox com egress allowlist do blueprint; secrets via
  `SecretNamesJson` — nunca no prompt.
- Veredicto é advisory: `sandcastle/qa` status nunca required por
  default.
- Evidência pode conter dados sensíveis do app testado → artifacts
  respeitam ACL org/user; links no PR são públicos apenas se o repo é
  público **e** share explícito habilitado (default: links exigem auth
  do Sandcastle).
- Custos: run QA conta usage do owner; `Qa:MaxDailyRunsPerRepo` (30).

## 6. Task Plan

1. `qa_runs`/`qa_steps` + connection flags (migrations duplas).
2. `IQaRunner` fases 1-2 (plan + setup) — reusa pipeline SPEC-140/141.
3. Fase 3: driver Playwright + exec + step recorder.
4. Fase 4: veredicto + comentário + status + evidência artifacts.
5. Vídeo (page.video → artifact) + links no comentário.
6. UI: QA tab/card na conversa + findings viewer.
7. Testes: unit (plan classify, veredicto), integração com app fixture
   (mini web app em tests/), provider fake, golden OpenAPI.

## 7. Deliverables

PR elegível ganha relatório QA com veredicto PASS/FAIL/PARTIAL, passos
executados e evidência (screenshots + vídeo) anexada — revisor aprova
sem checkout.

## 8. Referências

- `docs/research/2026-11-competitive-gap-analysis.md` §2.2/§2.3/§4 G3.
- OH `docs.openhands.dev/openhands/usage/use-cases/qa-changes` +
  plugin `OpenHands/extensions` (conceitos apenas).
- Devin `docs.devin.ai/work-with-devin/testing-and-recordings`
  (conceitos apenas).
- `docs/specs/18_browser_tool_and_switching_spec.md`,
  `131_sandbox_browser_playwright_spec.md`,
  `34_streaming_deltas_artifacts_spec.md`.

## 9. Guardrails

- QA nunca edita a branch nem o PR além do comentário/status.
- Bail-out honesto: PARTIAL relata o que tentou (não "PASS" por falta
  de tentativa).
- Vídeo/evidência dentro do mesmo trust boundary dos artifacts.

## 10. DoD

- PR com web app fixture → QA run → comentário com PASS + screenshots +
  vídeo artifact acessível com ACL.
- Bugfix: reproduz no base + confirma no PR quando aplicável.
- Dedup por head_sha; caps respeitados; status postado.
- Suite verde; migrations duplas; golden OpenAPI.
