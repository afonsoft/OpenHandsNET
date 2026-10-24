# SPEC — 96 · Sonar rescan truth — reconciliar counts reais

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `sonar-rescan-truth` |
| Type | `Quality / tooling` |
| Status | `Approved` |
| Ticket | gap-analysis 2026-10-23 |
| Depends on | — |
| Touches | `.sonar_devin_auto_fix/`, issues #154–156, SPECs 72–74 |

## 1. User Story

**As a** mantenedor, **I want** counts reais do SonarCloud (não do
snapshot 19/10), **so that** o backlog #152 é right-sized e as SPECs
72–74 fecham com evidência.

## 2. Contexto

Snapshot local (19/10) provado stale — issues já fixas via PRs #225–247
seguem listadas. Live API 23/10: CODE_SMELL BLOCKER 1 / CRITICAL 191 /
MAJOR 1317 / MINOR 404 / INFO 4; VULN+BUG 108.

## 3. Requisitos

- RF-001: script `tools/sonar-rescan.py` — `api/issues/search` por
  severidade+regra → JSON + delta vs board.
- RF-002: reconciliar `SONAR_FIX_TODO_BOARD.md` (drop stale, add novos).
- RF-003: atualizar #154/#155/#156 com counts reais + veredito por regra.
- RF-004: critério de fechamento das SPECs 72/73/74 reescrito p/ live.

## 4. Acceptance

- [ ] Board reflete live (issues novas incluídas).
- [ ] #154–156 atualizadas com delta real.
- [ ] SPEC-74 auditada contra live.

## Resultado — 2026-10-23

- `--board` regenera `SONAR_FIX_TODO_BOARD.md` (1574 issues abertas).
- **Correção**: contagens sem `resolved=false` incluem resolvidas —
  backlog real = 1541 smells + 15 vulns + 18 bugs.
- `sonar-project.properties` é **ignorado pelo dotnet-sonarscanner**;
  exclusões efetivas vivem em `ci.yml` — alinhadas nesta entrega
  (~1100 findings de `docs/architecture/runtime-architecture.html`
  gerado + monaco/xterm vendored saem do backlog no próximo scan).

- [x] Board reflete live (issues novas incluídas).
- [x] #154–156 atualizadas com delta real.
- [x] SPEC-74 auditada contra live.
