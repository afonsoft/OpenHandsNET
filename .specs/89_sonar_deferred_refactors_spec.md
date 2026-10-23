# SPEC — 89 · Defer — refactors grandes do Sonar (S3776/S107/S3903/S1075/S127/S3218)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `sonar-deferred-refactors` |
| Type | `Analysis / Backlog (research spec)` |
| Stack | `.NET 10` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Superseded — see children 101-106` |
| Ticket | gap-analysis 2026-10-22 · Lote D |
| Depends on | SPECs 70–74 (sonar slices) |
| Touches | nada — backlog |

## 1. Contexto

Lote C (Lote E da triagem 2026-10-22) executou os smells mecânicos e
seguros (S3267, S3358, S1192, avulsos). Este SPEC **registra o defer**
dos refactors grandes que exigem SPEC própria e testes dedicados:

| Regra | Qtd | Por que defer |
|---|---|---|
| `S3776` | 74 | cognitive complexity — refactors por área (AgentRunner, endpoints, client) com testes antes/depois |
| `S107` | 29 | muitos parâmetros — exige design (extração de records/options) |
| `S3903` | 11 | tipos em namespace/folder errados — mover é breaking para consumers |
| `S1075` | 10 | URIs hardcoded — revisar quais são intencionais (internos) |
| `S127` | 3 | loop core do AgentRunner (iteration--) — semântica sensível |
| `S3218` | 2 | shadowing em Settings/CLI — rename pode quebrar flags |
| CSS/JS próprios | ~10 | app.css duplicatas/!important, monacoInterop optional-chain — risco visual; validar com e2e |

## 2. Recomendação

- Priorizar `S3776` nos 5 piores arquivos (AgentRunner, BuiltinTools,
  endpoints) em SPECs 90+ com testes de comportamento.
- `S1075`/`S3903`/`S3218`/CSS: marcar *won't fix* no re-scan com
  justificativa (internos/intencionais) — reabrir só com pedido real.

## 3. DoD

- SPECs 90+ criadas quando priorizadas; ou marcação won't-fix no
  SonarCloud após o re-scan (dependente do #148/SONAR_TOKEN).

## Child SPECs (SPEC-99 split, 2026-10-23)

| SPEC | Escopo | Regra |
|---|---|---|
| 101 | cli/ | S3776 |
| 102 | infra/agent | S3776 |
| 103 | api endpoints | S3776 |
| 104 | cross | S107 |
| 105 | cli/settings/agent | S3218 + S127 |
| 106 | cross | S1075 + S3903 |
