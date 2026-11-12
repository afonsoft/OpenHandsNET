# SPEC — 143 · Code Scans (`/scan` → fan-out → findings → fix sessions)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `code-scans` |
| Type | `Feature / Agent orchestration + Security` |
| Stack | `.NET 10 · delegate (SPEC-62) · EF Core · SignalR` |
| Repository | `afonsoft/sandcastle` |
| Status | `Done` |
| Ticket | `GAP-SCAN-FINDINGS` (docs/research/2026-11-competitive-gap-analysis.md §G4) |
| Depends on | `62` (delegate), `52` (SCM/PR), `60` (indexação — réusável p/ partição) |
| Reference | Devin Code Scans / Security 2.0 (findings pipeline, Agentic MapReduce) — conceitos apenas |
| Touches | `src/Sandcastle.{Application,Infrastructure,Api,Client}`, `tests/` |

## 1. User Story

**As a** engenheiro/security lead
**I want** disparar `/scan find <problema> in @repo` (ou pela página
Scans) e receber uma lista de achados triados — cada um podendo virar
uma sessão de correção com PR
**So that** Sandcastle faz o que Devin Security 2.0/Code Scans faz:
varredura multi-repo com orquestração em paralelo e fila de findings.
Hoje `SwarmOrchestrator` é um stub (`Task.Delay`) e não existe
entidade de achados.

## 2. Scope (RF)

### RF-001 — Setup do scan

- Slash `/scan <descrição>` no composer com `@repo` mencionado, ou
  `POST /api/v1/scans {repositories[], goal, profile?, effort?}` +
  card de confirmação na UI (repos + foco) antes de criar — como o
  setup card do Devin.
- `profile`: builtin `security` (threat model leve → achados de
  vulnerabilidade), `performance`, `dead-code`, `coverage-gaps`,
  `a11y`, `custom` (goal livre).
- `effort`: `low|medium|high` → limites de filhos/tokens
  (`Scan:EffortMatrix`: low=4 filhos, medium=12, high=24;
  budget total por esforço via quotas SPEC-20).

### RF-002 — Orquestração (substitui o stub)

- `ScanOrchestrator` real sobre `IDelegationService`: sessão-mãe
  (conversa `agent_type: scan-coordinator`) particiona o repo
  (por diretório via index SPEC-60 — ou `git ls-tree` no clone)
  → cria filhas via `delegate` com prompt por partição
  (`Scan:MaxSiblings` respeita `Delegation:MaxSiblings`; lotes em
  fila quando > cap).
- Filha: analisa sua partição com tools read-only (+ `task` interno se
  preciso), emite findings estruturados via saída JSON validada
  (schema `scan_finding_v1`); mãe agrega, deduplica por
  `(path,start_line,kind)` + LLM dedup opcional (role `analysis`).
- Segurança: filhas **não** podem commitar/push no scan — somente
  ler (tool permissions `read-only` profile; credentials read scope).

### RF-003 — Findings entity + pipeline

- `scan_findings`: id, scan_id, org, repo, path, start/end_line,
  `kind` (security|performance|dead_code|coverage|a11y|custom),
  `severity` (critical|high|medium|low|info), `confidence`,
  title, description, remediation_hint, `status`
  `open|reviewed|dismissed`, `remediation_state`
  `unassigned|assigned|pr_open|merged`, conversation_id?, pr_url?,
  `payoff` (high|medium|low), created/updated.
- Estados do pipeline (Devin-parity): Unassigned → Assigned (sessão
  de correção iniciada) → PR open → merged (reconcilia via webhook
  `pull_request` closed/merged ou poll).
- Bulk actions: marcar reviewed/dismissed/open; "Fix with Sandcastle"
  (single ou bulk) → cria conversa de remediação por finding
  (ou agrupada por arquivo) via pipeline de start-task.

### RF-004 — UI + agendamento

- Sidebar "Scans": lista de scans (status, findings count por
  severidade) → página do scan com findings agrupados por
  remediation_state + filtros (repo/severity/status/kind) + bulk
  select. Findings tab também no card da sessão-mãe.
- `GET /api/v1/orgs/{org}/scans` e
  `GET /api/v1/orgs/{org}/scan-findings` (filtros `scan_id`,
  `repository`, `severity`, `status`, `q` textual, cursor `after`/
  `first≤200`) — paridade com a API v3 do Devin.
- Auto-scan: automation com `schedule` existente + `type=scan`
  (reusa `AutomationService` — `AutomationKind: task|scan`).

### RF-005 — Fora de escopo

- Threat model interativo do Security Swarm · profiles custom por org ·
  benchmark contra GitHub Advisory DB · scans cross-repo correlacionados
  (chained exploits) · findings via SARIF import — fase 2.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| POST | `/api/v1/scans` | `{repositories[], goal, profile?, effort?}` → 202 `{scan_id, conversation_id}` |
| GET | `/api/v1/scans` · `/api/v1/scans/{id}` | lista/detalhe + counts |
| GET | `/api/v1/scans/{id}/findings` | paginado + filtros |
| GET | `/api/v1/orgs/{org}/scan-findings` | org-wide |
| PATCH | `/api/v1/scan-findings/{id}` | `{status}` |
| POST | `/api/v1/scan-findings/{id}/fix` | → remediation conversation |
| POST | `/api/v1/scan-findings/fix-bulk` | `{ids[]}` |

Erros: `404` scan/finding · `409` fix já em andamento · `403` quota.

## 4. Data model

- `scans` (id, org, created_by, repos_json, goal, profile, effort,
  status, coordinator_conversation_id, stats_json, created/finished).
- `scan_findings` (RF-003); índices `(org,status)`, `(scan_id)`,
  `(repo,severity)`, `(path)`.

## 5. NFR / Security

- Permissões: `perm:scans.run` (start), `perm:scans.manage` (schedule/
  delete), `perm:scans.findings` (triagem) — papéis existentes recebem
  defaults sensatos (member run+findings, admin manage).
- Achados de segurança são sensíveis: findings `kind=security` visíveis
  só para roles com `perm:scans.findings` (já coberto) — documentar.
- Fan-out limitado por effort + quotas de concorrência da org;
  filhas herdam policies do pai (SPEC-62 RF-005).
- Conteúdo do repo é untrusted → envelope SPEC-91; findings passam por
  `SecretRedactor` antes de persistir.

## 6. Task Plan

1. `scans`/`scan_findings` + permissions + migrations duplas.
2. `ScanOrchestrator` real (partição → delegate → coleta/dedup) —
   remove/substitui `SwarmOrchestrator` stub (deprecated path).
3. Skill `scan-worker` builtin (prompt de análise por partição +
   saída JSON `scan_finding_v1`).
4. Endpoints + `/scan` slash + UI (lista, findings, bulk, fix).
5. Remediation sessions + reconciliação `pr_open`/`merged`.
6. Automation kind=scan (schedule).
7. Testes: unit (dedup, particionamento, estados), integração com
   repo fixture, golden OpenAPI + `check-mcp-tools-golden` se MCP.

## 7. Deliverables

`/scan` e API criam scans que rodam em paralelo via delegate, produzem
findings triáveis e despacham correções com PR — o "Security Swarm"
open-source, sobre infra já existente.

## 8. Referências

- `docs/research/2026-11-competitive-gap-analysis.md` §2.3/§4 G4.
- Devin `work-with-devin/code-scans` + `security-2-0` +
  API `organizations-code-scans-findings` (conceitos apenas).
- `docs/specs/62_managed_conversations_spec.md` (delegate),
  `60_repo_indexing_ask_mode_spec.md` (partição), `29_tool_permissions_spec.md`.

## 9. Guardrails

- Filhas de scan são read-only — nenhum commit/push no scan.
- Nunca auto-fix sem ação explícita do usuário ("Fix with Sandcastle").
- `SwarmOrchestrator` stub é removido ou redirecionado — não manter
  duas orquestrações.

## 10. DoD

- `/scan` cria sessão-mãe + filhas via delegate (teste integração) e
  findings persistidos com schema validado.
- Findings API com filtros/cursor; bulk actions; fix cria conversa e
  marca `assigned → pr_open`.
- Automação `type=scan` dispara no schedule.
- Suite verde; migrations duplas; golden OpenAPI.
