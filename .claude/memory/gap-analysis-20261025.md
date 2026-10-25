# Gap-analysis 2026-10-25 (pós-merge SPECs 107-110)

## Vereditos
- Candidatos: 6 | Confirmados: 2 novos | Bloqueados (gate humano): 2 | Já rastreados: 4

## Confirmados → SPECs Draft
- SPEC-111 `AgentRunner.LoopAsync` (CC~210, deferido da 107 — único S3776 grande restante)
- SPEC-112 fuzz round 2 — nightly 40177735051 (pós-#255) ainda falha: 89 failed,
  16 "Server error", `POST /authenticate` aceita auth inválida → alimenta #218

## Bloqueado (ação humana)
- **Sonar board congelado**: job sonarcloud exige `SONAR_ENABLED=true` (repo var)
  + `SONAR_TOKEN` (secret) — ambos ausentes → scans skipados; contagens live
  (S3776×52, S7761×848, VULN×4, BUG×8) são PRÉ-merge e não verificáveis.
- Consequência: **#248** (verificar exclusões docs/monaco) e todo o epic #152
  ficam não-verificáveis até o secret ser configurado.

## Já rastreados
- SPECs 103–106 Approved (filhos da 89) — não implementadas ainda
- SPEC-74/#156 minor smells (S8970×144 live, stale)
- #218 aberto (agora alimentado pela SPEC-112)
- #218/#248 pendentes de verificação externa

## Housekeeping
- Epic #265 fechado (filhos entregues)
- SPECs 72/73 seguem `.specs/` com status Approved — arquivar após verificação
  sonar (bloqueada) ou marcar parcial
- PR #275: arquivamento 107–110
