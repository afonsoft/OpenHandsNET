# Browser Auth Profiles (SPEC-64)

Login persistente por domínio para as sessões de browser do agente:
o estado de autenticação (cookies + localStorage do Playwright) é
capturado uma vez, criptografado em repouso e reanexado a conversas
futuras — evita re-login manual a cada sessão.

## Modelo

- **`BrowserProfileRecord`** — (id, org_id, owner_id, domain, name,
  ciphertext, nonce, shared, created_at, last_used_at, expires_at).
  `domain` normalizado para eTLD+1 simplificado (últimos 2 labels;
  limitação documentada para ccTLDs tipo `*.com.br`).
  Unique index `(owner_id, domain)` — um profile por domínio por dono
  (re-capture atualiza).
- **Criptografia** — ASP.NET DataProtection, purpose
  `openhandsnet.browserprofiles.v1` + nonce por registro (mesmo regime
  do `SecretStore`). O state **nunca** sai pela API — só metadados.
- **Visibilidade** — dono sempre; `shared=true` expõe à mesma org;
  super-admin vê/deleta qualquer um. Expirado → 410
  `browser_profile_expired`.
- Feature flag `Browser:ProfilesEnabled` (default on) e
  `Browser:AutoAttachProfiles` (opt-in de auto-attach por org —
  sem ele, attach é explícito por conversa).

## API (`/api/v1`)

| Método | Rota | Descrição |
|---|---|---|
| GET | `/browser-profiles` | metas dos profiles do usuário + shared |
| GET | `/browser-profiles/{id}` | meta de um profile |
| DELETE | `/browser-profiles/{id}` | remove (dono ou admin) |
| POST | `/app-conversations/{id}/browser/capture` | captura o storage state da sessão, filtrado ao `domain` informado, e salva como profile (`{domain, name?, expires_at?, shared}`) |
| POST | `/app-conversations/{id}/browser/attach` | anexa `{profile_id}` à conversa — state decriptado no servidor, cookies `site_cookie` do domínio mesclados, contexto recriado |

Audit: `browser.profile_saved`, `browser.profile_attached`,
`browser.profile_deleted`, `browser.totp_used`.

## Secrets tipados (`SecretRecord.Kind`)

`value` (default) | `site_cookie` | `totp`.

- `site_cookie:{domain}:{name}` — payload JSON `{value, domain, path,
  expires, secure, http_only}` mesclado no state no attach.
- `totp:{name}` — segredo base32; a action `browser` aceita
  `action: "totp"` + `name`: `TotpGenerator` (RFC 6238, HMAC-SHA1,
  30s, 6 dígitos) devolve **só o código efêmero** ao LLM — o segredo
  nunca sai do vault.

## Divergências vs spec

- UI de admin de profiles fica para um PR de UI posterior (endpoint
  + service completos; a página consome `ListAsync`).
- Capture/attach por endpoint explícito (a confirmação humana do
  guardrail `ask_user` é responsabilidade do agente/superfície que
  chama o endpoint — não embutida no endpoint).
