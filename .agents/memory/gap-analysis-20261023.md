# Gap Analysis — 2026-10-23 (run state)

## Phase 0 — Preconditions

- repo `afonsoft/OpenHandsNET` · branch `main` @ post-`9416daf` · clean · branches pruned (só main)
- `gh` auth OK · issues abertas: 8 (#146, #148, #152, #154, #155, #156, #218, #244)
- CI main: verde (ci/security-scan/codeql/scorecard success)
- nightly-quality + k8s-e2e: success 22–23/10 (estáveis)
- resume marker: **Phase 2 done — GATE pendente**

## Phase 1 — AS-IS × TO-BE

| Tópico | AS-IS | TO-BE | Veredito |
|---|---|---|---|
| issue lifecycle | #232 done+open (fechada nesta run); labels #233–237 stale | slices fechadas + `done` | corrigido in-run |
| sonar critical (SPEC-72/#154) | S2699 fix; 44 stale; S3218/S3776→89 | rescan p/ fechar | gap-parcial |
| sonar major (SPEC-73/#155) | verified subset merged (PR #247); S1172×10/k8s/S1854/S2376/S6673 unverified | rescan p/ right-size | gap-parcial |
| sonar minor (SPEC-74/#156) | 331 issues — audit não concluído (amostras stale) | audit + rescan | gap |
| sonar deferred (SPEC-89) | Draft: S3776×74, S3218×2, S107×29, S127×3, S1075×10, S3903, CSS/JS | aprovar/fatiar por área | gap (gate) |
| fuzz findings (#218) | 35 ops `ignored_auth`, 10 `not_a_server_error`, rotas ambíguas | corrigir achados | gap |
| i18n parity (#244) | ~1720 chaves faltando; baseline congela; runtime fallback ok | tradução por locale | gap (fatiar) |
| SPEC-66 quality gates (#148) | format+lint+sonar rodam em CI/PRs | verificar aceitação | gap-auditar |
| gap-analysis 2026-10-19 (#146) | Epic aberto; filhos entregues? | auditar filhos p/ fechar | gap-auditar |
| OTel/metrics | ausente | sem TO-BE | sem gap (mantém rejeição) |
| eval p/ CI | lib sem job | sem TO-BE | sem gap (mantém) |
| branch hygiene | resolvido in-run | — | OK |
| CI/k8s/nightly | verdes | — | OK (gap de 20/10 sanado) |

## Phase 2 — Candidates → vereditos

| Key | Veredito | Prioridade | Evidência |
|---|---|---|---|
| GAP-sonar-rescan-truth | CONFIRMADO | P1 | snapshot 19/10 majoritariamente stale; counts reais desconhecidos — bloqueia fechamento de #154–156 |
| GAP-security-fuzz-findings | CONFIRMADO | P1 | issue #218: 35 ops sem auth declarada + 10 500s — aberta desde 20/10 |
| GAP-i18n-parity-translations | CONFIRMADO | P2 | issue #244: ~1720 chaves; fallback cobre runtime, baseline cobre CI — fatiar por locale |
| GAP-sonar-deferred-split | CONFIRMADO | P2 | SPEC-89 Draft: 118+ issues deferidas precisam de aprovação/slice por área |
| GAP-epic-152-closeout | CONFIRMADO | P3 | #154/#155/#156 aguardam rescan; SPEC-74 audit pendente |
| GAP-epics-stale-close | CONFIRMADO | P3 | #146/#148 podem estar obsoletos — auditar acceptance vs estado atual |
| OTel/metrics | REJEITADO | — | sem fonte TO-BE (3ª run) |
| sidebar/voz/anexos | REJEITADO (mantido) | — | runs anteriores |

## Phase 5 — Draft SPECs (GATE PENDENTE)

Pendentes de aprovação do usuário antes de escrever:

1. `90_rescan_truth` — disparar rescan SonarCloud + reconciliar #154–156/SPEC-74 counts
2. `91_fuzz_findings` — corrigir achados #218 (auth/500s por rota)
3. `92_i18n_locale_slices` — traduzir por locale (zh-CN primeiro, maior déficit)
4. `93_epic_hygiene` — auditar e fechar #146/#148 se obsoletos

## Notas operacionais

- **Commits sem trailer `Generated with [Devin]`** (preferência 20/10 — violada nos
  commits desta sessão; corrigir daqui pra frente).
- `.sonar_devin_auto_fix/` é gitignored (artefato local; board anotado).
- monaco/xterm = vendored → sonar issues não-corrigíveis; sugerir exclusions no
  sonar-project ou won't-fix formal.

## Execução (mesma sessão)

- SPECs 96–100 escritas + issues #248–252 criadas.
- Live Sonar 23/10: BLOCKER 1, CRITICAL 191 (S3776:79, S927:74, S3218:4,
  S4487:3, S2699:1, S1994:1, S3871:1…), MAJOR 1317 (S7761 js:850 —
  maioria monaco vendored, S9423 css:102, S3358:69, S108:54…),
  MINOR 404, INFO 4, VULN+BUG 108.
- **Correção**: vereditos 'stale' da sessão estavam errados nas regras
  grandes — linhas driftaram, issues persistem. Comments de correção
  postados em #154/#155.
- SPEC-96: PR #253 (tools/sonar-rescan.py) — aguarda CI.
- Pendente: implementar 97–100 na próxima sessão.

## Execução cont. (sessão 2)

- SPEC-100 entregue+arquivada: #146/#148 auditadas e fechadas; #252 closed.
- SPEC-99 entregue+arquivada: SPECs filhos 101-106 Draft em .specs/;
  SPEC-89 virou índice (Superseded); #251 closed.
- SPEC-96: PR #253 merged; board reconcile + SPEC-74 audit restantes (#248 aberta).
- Fila: #249 fuzz, #250 i18n, #248 reconcile, sonar backlog real
  (S3358:69, S108:54, S927:74) + SPECs 101-106 execução.
