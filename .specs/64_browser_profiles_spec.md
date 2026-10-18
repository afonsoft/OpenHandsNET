# SPEC — 64 · Browser Auth Profiles (storage state + site cookies/TOTP)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `browser-profiles` |
| Type | `Feature / Agent + Security` |
| Stack | `.NET 10 · Microsoft.Playwright · EF Core` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
| Ticket | `TBD` |
| Depends on | `18` (browser tool), `33` (secrets by-ref), `14`/`16` (sandbox
  lifecycle) |
| Reference | competitive-analysis §6.2 (G12); docs.devin.ai
  `/work-with-devin/browser-auth`, `/product-guides/secrets`
  (site cookies, TOTP) |
| Touches | `src/OpenHandsNET.{Application,Infrastructure,Api,Client}`, `tests/` |

## 1. User Story

**As a** usuário
**I want** logar uma vez num site pelo browser interativo do agente,
salvar o perfil de autenticação por domínio, e ter TOTP/cookies de
site como secrets gerenciados
**So that** futuras sessões já começam autenticadas — paridade com o
browser-auth do Devin. Hoje o browser tool (SPEC-18) é stateless por
sessão: cada run refaz login (e muitas vezes não consegue sem o
usuário presente).

## 2. Scope (RF)

### RF-001 — Browser profile entity

- `browser_profiles`: `Id, OrgId, OwnerId, Domain (eTLD+1 normalizado),
  Name, StorageStateRef (ref p/ `IArtifactStore` — JSON de storage
  state Playwright: cookies + localStorage), CreatedAt, LastUsedAt,
  ExpiresAt?`.
- Storage state criptografado em repouso via o mesmo mecanismo de
  secrets (`SecretProtector`/DPAPI-equivalente do repo — verificar
  implementação e reusar; se não houver, envelope AES-GCM com chave
  `Secrets:MasterKeyRef` já existente).
- `GET/POST/DELETE /api/v1/browser-profiles` + `GET .../{id}` (meta
  apenas — nunca devolve o state).

### RF-002 — Capture via browser interativo

- Quando o usuário abre a surface browser (SPEC-18/40) e loga num
  domínio, oferta "Save login for future sessions" → captura
  `context.StorageStateAsync()` filtrado ao eTLD+1 → grava profile.
- `POST /api/v1/app-conversations/{id}/browser/capture` forçável via
  tool `save_browser_profile` (o agente pede confirmação `ask_user`
  sempre — nunca salva silenciosamente).

### RF-003 — Inject em novas sessões

- `ConversationSettings.BrowserProfileIds[]` (por conversa) +
  auto-attach: se a org permite (`Browser:AutoAttachProfiles`) e o
  usuário tem profile do domínio alvo → contexto Playwright criado
  com `StorageState` correspondente.
- Profile expirado/inválido → o agente detecta falha de login e
  pede re-capture (evento `browser_profile_expired`).

### RF-004 — Site secrets: cookies + TOTP

- `secrets` + tipo `site_cookie` (name/value/domain/path/expires) e
  `totp_secret` (base32).
- Browser inject aplica cookies do domínio ao criar contexto; tool
  `browser` expõe `totp` action que gera código do segredo referenciado
  (`totp://name`) — o código é computado no sandbox-agent
  (segredo nunca sai do vault).

### RF-005 — Admin/UX/audit

- Settings `Browser profiles`: lista por domínio, last-used,
  delete; org pode desabilitar (`Browser:ProfilesEnabled`).
- Audit `browser.profile_saved`, `browser.profile_attached`,
  `browser.totp_used` (sem valores).

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| GET/POST | `/api/v1/browser-profiles` | |
| DELETE | `/api/v1/browser-profiles/{id}` | |
| POST | `/api/v1/app-conversations/{id}/browser/capture` | salva state do domínio atual |

Erros: `404` · `409` profile já existe p/ domínio (use PUT implícito
via capture) · `403` feature off.

## 4. Data model

`browser_profiles` (domain unique por owner+org, state ref),
`secrets` + `Kind` (value|site_cookie|totp). Migration.

## 5. NFR / Security

- Storage state = credencial — mesmo regime de segredos
  (`SecretNamesJson`, mascarado, cripto em repouso, redação em logs).
- Attach só dentro da mesma org + dono ou profile `shared=true` por
  admin (escopo org — opt-in explícito).
- TOTP: segredo nunca no prompt/evento — só o código gerado (e esse
  é efêmero; redactor também cobre padrão de 6 dígitos adjacente a
  login? documentar decisão — default: código vai só para o campo de
  input, não para o stream).
- Cookies com `Secure`/`HttpOnly` preservados no state.

## 6. Task Plan

1. Entidade + `IArtifactStore` ref + endpoints + cripto.
2. Capture (surface action + `save_browser_profile` c/ confirmação).
3. Attach no start (conversation settings + auto-attach + expirado).
4. `site_cookie`/`totp` kinds + inject + `totp` action.
5. Admin UI + docs.

## 7. Deliverables

Login persistente por domínio via profile; cookies/TOTP como secrets
tipados; UX de captura e attach; audit completo.

## 8. Referências

- `migration-plan/research/competitive-analysis-devin-openhands-cloud.md`.
- `.specs/18_browser_tool_and_switching_spec.md`,
  `.specs/33_agent_hardening_spec.md` (secrets),
  `.specs/34_streaming_deltas_artifacts_spec.md` (artifact store).
- Playwright `StorageStateAsync` docs.

## 9. Guardrails

- Profile nunca serializado para o client — meta apenas.
- Auto-attach exige opt-in org; sem ele, attach por conversa
  explícito.
- Capture sempre com confirmação humana (guardrail `ask_user`).

## 10. DoD

- Login manual + capture → nova sessão já autenticada (teste com
  site local fake).
- `totp` action retorna código válido (TOTP determinístico em teste).
- Profile listado sem state; delete revoga attach futuro.
- Suite verde; golden OpenAPI atualizado.
