# SPEC-112 — Fuzz findings round 2 (schemathesis pós-SPEC-97)

| Campo | Valor |
|---|---|
| Status | `In Review` |
| Origem | gap-analysis 2026-10-25 — nightly `40177735051` (2026-10-25, sha `ef6ad24d`, **pós-#255**) |
| Severidade | P1 |
| Issue relacionada | #218 (permanece aberta — findings residuais) |

## 1. Contexto

O nightly `OpenAPI fuzz (schemathesis)` rodou **depois** do merge do #255
(SPEC-97) e ainda falhou: **173 passed / 89 failed**. Evidências do log:

- `POST /api/v1/authenticate` — check **"API accepts invalid authentication"**
  reprova (a classe central de findings da #218).
- **16 ocorrências de "Server error"** — 5xx em vez de 4xx em inputs
  malformados/incompletos (ex.: `POST /api/v1/mcp/tool-call` → 502
  "mcp server '' não configurado"; demais endpoints POST/PUT/DELETE com
  bodies fuzzados).
- Demais falhas precisam de triagem caso-a-caso (o job exclui
  `unsupported_method,positive_data_acceptance,status_code_conformance,
  negative_data_rejection`).

## 2. Escopo

1. **Reproduzir localmente**: rodar o job de fuzz (ou extrair a lista de
   casos do log `40177735051`) e classificar cada falha em:
   - bug real (500 em input rejeitável → mapear p/ 4xx com ProblemDetails);
   - auth bypass real (endpoint aceita credencial inválida);
   - falso positivo do fuzzer (comportamento correto, check inadequado →
     adicionar à lista de `--exclude-checks` com justificativa).
2. **500→4xx**: garantir que handlers validem entrada antes de tocar
   serviços (ex.: `mcp tool-call` rejeitar `server` vazio com 400/422,
   não propagar exceção → 502).
3. **Auth**: `POST /api/v1/authenticate` não pode retornar 2xx para
   credencial inválida — auditar o handler e o dev-login (o endpoint de
   dev pode estar dentro do escopo fuzzado; se for intencional em dev,
     gate por ambiente + documentar).
4. Atualizar #218 com o resultado; fechar quando nightly ficar verde ou
   com falhas justificadas.

## 3. Fora de escopo

- Novos endpoints/features; hardening de rate-limit (coberto em SPEC-97);
- Mudar a config do workflow schemathesis além de `--exclude-checks`
  justificados.

## 4. Testes

- Teste de integração por bug corrigido (endpoint retorna 4xx + ProblemDetails
  para o input que antes 500ava).
- Re-run do job de fuzz local como verificação antes do PR.
- Nightly seguinte verde (ou falhas remanescentes documentadas).

## 5. DoD

- Zero "Server error" reproduzível nos casos corrigidos.
- `POST /api/v1/authenticate` rejeita credencial inválida (ou justificativa
  dev-only documentada + excluída do fuzz).
- CI verde; nightly seguinte confirma regressão zero.
