# SPEC — 48 · Enterprise Security Hardening (Org Policies + Resume Strict)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `enterprise-security-hardening` |
| Type | `Feature / Infrastructure + API + Client` |
| Stack | `.NET 10 · EF Core (2 ctx) · Blazor WASM` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
| Ticket | [#114](https://github.com/afonsoft/OpenHandsNET/issues/114) |
| Depends on | `08` (auth/RBAC/orgs), `09` (agent loop), `10` (persistence), `29` (tool permissions), `33` (security analyzer + secrets by-ref + resume_drift) |
| Reference | `OpenHands/OpenHands` (MIT): `SecurityAnalyzer` policy presets; upstream enterprise docs (conceitos apenas — Polyform) |
| Touches | `src/OpenHandsNET.Infrastructure` (records+service), `src/OpenHandsNET.Application` (contratos), `src/OpenHandsNET.Api/Endpoints`, `src/OpenHandsNET.Client` (org settings), migrations ×2 |

## 1. User Story

**As a** admin de uma organização no OpenHandsNET
**I want** políticas de segurança por org (`permissive|balanced|strict`)
e resume enforcement configurável
**So that** times com posturas distintas de risco convivem na mesma
instância — hoje o `ISecurityAnalyzer` é global
(`Agent:SecurityAnalyzer=pattern|llm`), o preset `strict` do upstream
(que também exige `ask` em tools `mcp__*`) não existe, e o resume
`resume_drift` é apenas fail-open/log sem opção de bloqueio.

**Contexto:** SPEC-33 entregou o analyzer plugável (High → confirmação
forçada + `agent_diagnostic` `security_risk`), secrets by-reference
(`SecretNamesJson` → resolvido na borda via `ISecretStore`) e o
fingerprint `agent_contract` com `resume_drift` fail-open. Esta SPEC
fecha os gaps residuais do SPEC-32 RF-03/RF-10: a **camada de política
por org** e o modo **fail-closed** opt-in. Não reabre secrets
(já implementado).

## 2. Scope (RF)

### RF-001 — `security_policies` por org (record + migration ×2)

- `SecurityPolicyRecord`: `{Id, OrgId (UNIQUE), Preset
  (permissive|balanced|strict), AnalyzerOverride (pattern|llm|null),
  AskMcpTools (bool), MinConfirmRisk (low|medium|high), CreatedAt,
  UpdatedAt, UpdatedByUserId}`.
- Presets (não mutáveis como presets — customização via campos
  explícitos fora do preset):
  - `permissive`: analyzer off opcional, `MinConfirmRisk=high`,
    `AskMcpTools=false`.
  - `balanced` (default quando sem policy): analyzer pattern,
    `MinConfirmRisk=high`, `AskMcpTools=false`.
  - `strict`: analyzer = max(pattern, llm quando configurado),
    `MinConfirmRisk=medium`, `AskMcpTools=true`.
- Cache: `IDistributedCache` (SPEC-36) com TTL 60s + invalidação no
  update — policy lookup não pode custar query por tool call.

### RF-002 — Policy-aware security gate no `AgentRunner`

- `ISecurityPolicyService.GetAsync(orgId)` resolvido **uma vez por
  run** (não por tool call — policy vive em cache durante o run;
  restart do run pega policy nova).
- `AskMcpTools=true` → tools `mcp__*` entram no gate de confirmação
  independente de `Risky` (como `ask` na tabela de permissões, antes
  do analyzer).
- `MinConfirmRisk`: `High` risk → confirma sempre (comportamento
  atual); `medium` → confirma a partir de Medium; `low` → confirma
  qualquer tool `Risky`.
- `AnalyzerOverride` por org: `pattern` força local mesmo com global
  `llm`; `llm` exige provider configurado (fallback pattern + warning
  `agent_diagnostic` quando indisponível).
- `security_risk` diagnostic já existe — estender payload com
  `policy_preset` + `min_confirm_risk` aplicados (observabilidade da
  decisão).

### RF-003 — API + UI de admin/org-settings

- `GET/PUT /api/v1/organizations/{id}/security-policy` — leitura p/
  membros admin+; PUT restrito a `edit_org_settings` (SPEC-08).
  PUT valida preset válido + campos coerentes; body nulo reseta para
  default `balanced`.
- Audit: `security_policy_updated` com diff `{old, new}` + actor.
- UI: card em `/admin/orgs/{id}` (ou settings org-scoped existente)
  com select de preset + checkboxes dos campos avançados; chaves
  i18n nos 8 locales.

### RF-004 — Resume fail-closed opt-in

- `Agent:ResumeEnforcement = open|strict` (default `open` — status
  quo) + override por org via `security_policies.ResumeStrict` (bool?,
  strict preset → true).
- `strict` + fingerprint divergente com **tools removidas** → run não
  inicia: `agent_diagnostic` `code="resume_drift"` severity `error` +
  status `Error` (não apenas warning); tools só aditivas continuam
  permitidas (igual ao upstream).
- API surface: nenhuma — comportamento do start-task existente.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| GET | `/api/v1/organizations/{id}/security-policy` | 200 policy efetiva (defaults se ausente) |
| PUT | `/api/v1/organizations/{id}/security-policy` | `perm:edit_org_settings`; 200 policy gravada |

## 4. Data model

1 tabela nova `security_policies` (UNIQUE em `org_id`) — migration
nos dois contextos.

## 5. NFR / Security

- Policy lookup: cache distribuído, TTL 60s, invalidação no PUT;
  miss → default `balanced` (fail-open documentado, sem erro).
- Nenhum segredo ou policy internals em eventos — só preset +
  threshold no `security_risk`.
- `strict` nunca pode ser enfraquecido por policy de conversa
  (conversa não carrega override de segurança).

## 6. Task Plan

1. Record + config + migrations ×2 + `ISecurityPolicyService` com
   presets + cache (testes dos presets).
2. Runner: resolução de policy no run + gates (`mcp__*` ask,
   MinConfirmRisk, AnalyzerOverride) — testes unit do gate.
3. Endpoints GET/PUT + audit + validação — contract tests.
4. `Agent:ResumeEnforcement` + `ResumeStrict` por org — testes de
   start-task strict/open.
5. UI card org settings + i18n + bUnit.
6. Doc `docs/deployment/security.md` — seção policies + strict.

## 7. Deliverables

`security_policies` ×2 ctx, `ISecurityPolicyService`, runner policy
gate, endpoints + audit, UI card, `ResumeEnforcement`, testes, docs.

## 8. Referências

- `.specs/32` RF-03 (analyzer plugável — gap: policy layer), RF-10
  (resume validation — gap: fail-closed).
- `.specs/33` (analyzer + secrets by-ref + fingerprint).
- `.specs/29` (permissões `allow|deny|ask` — o `ask` de `mcp__*`
  reusa a mesma trilha).

## 9. Guardrails

- Upstream enterprise é Polyform — conceitos apenas.
- Policy default é `balanced` (status quo) — nenhuma org existente
  muda de comportamento sem ação explícita.
- `strict` fail-closed nunca aborta silencioso: sempre emite
  `agent_diagnostic` + status `Error` explicando.

## 10. DoD

- Orgs podem ter posturas distintas simultâneas (teste com 2 orgs).
- `strict` força `ask` em `mcp__*` e confirma em Medium — coberto
  por unit + integration.
- Resume strict bloqueia toolset encolhido (teste de endpoint).
- PUT auditado; GET não vaza internals; cache com invalidação.
- Suite verde; SPEC `Done`; docs atualizadas.
