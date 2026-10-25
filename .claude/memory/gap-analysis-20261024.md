# Gap Analysis — 2026-10-24 (run state)

## Phase 0 — Preconditions

- repo `afonsoft/OpenHandsNET` · branch `main` @ `ef6ad24` · clean ·
  branches pruned (só main local+remoto, 0 PRs abertas)
- `gh` auth OK · issues abertas: 8 (#152, #154, #155, #156, #218, #244,
  #248, #250)
- resume marker: **Phase 5 done — GATE pendente**

## Merges desta sessão (antes da análise)

- #257 de-DE · #258 es-ES · #259 fr-FR · #260 it-IT · #261 ja-JP (`ef6ad24`)
- i18n: 8 locales 451/451, baseline 0 — SPEC-98 completa
- Branches merged deletadas (remoto+local); repo limpo

## Phase 1–2 — AS-IS × TO-BE → candidatos

Fonte: `tools/sonar-rescan.py` live (resolved=false) 24/10:
BLOCKER 0 · CRITICAL 120 · MAJOR 1140 · MINOR 265 · INFO 1 ·
VULN 15 · BUG 18.

| Key | Veredito | Prioridade | Evidência |
|---|---|---|---|
| GAP-sonar-s3776-remainder | CONFIRMADO | P1 | 31 S3776 fora dos escopos 101–103 (SandboxAgent×3, TicketProviders×3, Client×5, Eval×2, Infra non-Agent ~15) + 4 residuais Agent |
| GAP-sonar-vuln-bug-triage | CONFIRMADO | P1 | VULN 15 (S5332×8 http cleartext, githubactions×4, S5693, S5542, k8s:S6865) + BUG 18 (S8949×11 fire-and-forget) — sem SPEC/issue |
| GAP-tests-integration-flakes | CONFIRMADO | P1 | ≥6 testes sandbox flakaram em ≥4 runs hoje (PR #257 ×2, #260, main 40128507179); SPEC-78 retry insuficiente |
| GAP-sonar-tail-rules | CONFIRMADO | P2 | S927×35 CRITICAL cli/ (param names) + python×5 + shelldre×14 + docker×4 + k8s×2 + cs leftovers ~20 — fora de 103-107 e #155/#156 |
| GAP-sonar-exclusion-effect | CONFIRMADO→#248 | P2 | `**/docs/**` e `wwwroot/lib` excluídos no ci.yml:354 desde 12:42 UTC; 5 análises de main depois; ~1030 issues ainda abertas em `runtime-architecture.html` + monacoInterop×9 + index.html×1 — exclusão não fecha issues existentes OU job não rodou c/ code=true. Verificar em #248 |
| GAP-lifecycle-delivered-not-closed | CONFIRMADO | P3 | SPECs 96/97/98/101/102 merged mas status ≠Done, não arquivadas; issues #218/#244/#248/#250 abertas apesar de entregues |
| GAP-sonar-S8970 | DUPLICADO | — | 144 MINOR — SPEC-74/#156 cobre explicitamente (linha 22/56/99) |
| GAP-fuzz-reverify | DUPLICADO | — | #218: fixes merged ~17h UTC; nightly 08:24 UTC anterior ao merge — verificação pendente no próximo nightly; parte do fechamento da #218 |
| OTel/metrics · eval-CI · sidebar/voz/anexos | REJEITADO | — | mantido das runs anteriores (sem TO-BE) |

## Phase 5 — Draft SPECs (GATE)

- `.specs/107_s3776-remainder_spec.md` — 31+4 S3776 não cobertos
- `.specs/108_sonar-vuln-bug-triage_spec.md` — VULN 15 + BUG 18
- `.specs/109_sonar-tail-rules_spec.md` — S927×35 + python/shell/docker/k8s/cs tail
- `.specs/110_integration-flake-hardening_spec.md` — flakes sandbox CI

## Housekeeping proposto (sem SPEC — operacional)

- Arquivar SPECs entregues 96/97/98/101/102 → `docs/specs/` + status Done
- Fechar issues entregues: #218 (após nightly confirmar), #244, #248
  (após verificar exclusões), #250
- `tools/sonar-live.json` — untracked; adicionar ao .gitignore

## Fatos operacionais novos

- Main CI teve failure 40128507179 (4 flakes distintos) — último run
  success 40127801756; ja-merge run ainda em progresso no snapshot.
- Exclusões `**/docs/**`/`monacoInterop`/`index.html`/`wwwroot/lib`
  entraram no ci.yml só hoje (17bf94b, PR #254).
- Sonar analyses main hoje: 17:26, 18:26, 20:01, 20:11, 20:31 UTC.
