# SPEC — 94 · WASM boot resilience — boot.js + framework-assets mirror

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `wasm-boot-resilience` |
| Type | `Infra / Frontend` |
| Stack | `.NET 10 · Blazor WASM · ASP.NET Core static assets` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
| Ticket | #237 (Epic #231) · análise `afonsoft/open-webui` (2026-10-22) |
| Depends on | — |
| Reference | `open-webui` `wwwroot/js/boot.js` + endpoint `GET /framework-assets/{stem}/{ext}` (`?enc=b64` fallback) — hard rule do open-webui CLAUDE.md |
| Touches | `Api/Program.cs` (static assets + endpoint mirror), `Client/wwwroot/js/boot.js` (novo), `Api` `index.html`/host page, `tests` |

## 1. User Story

**As a** usuário enterprise atrás de proxy corporativo
**I want** o boot do Blazor WASM sobreviver a proxies que bloqueiam
extensões `.wasm`/`.dat`/`.dll` ou fazem content-sniffing
**So that** o app carrega onde hoje ele trava numa tela em branco —
cenário comum em redes corporativas, exatamente o segmento que o
OpenHandsNET mira (org/SCIM/edge security).

**Contexto:** `Api/Program.cs` serve o client via `MapStaticAssets()`
(:323) + `MapFallbackToFile("index.html")` (:412) com boot padrão
(`autostart` implícito): o runtime do Blazor busca `_framework/*.wasm`,
`*.dat`, `*.dll` diretamente. Proxies que rejeitam essas extensões ou
strippam `Content-Type` quebram o boot sem remediação. O open-webui
resolve com `js/boot.js` (`autostart="false"` + boot manual) que busca
assets de `_framework` não-`.js` num espelho **sem extensão**:
`GET /framework-assets/{stem}/{ext}` (ex.: `dotnet.wasm` →
`/framework-assets/dotnet/wasm`), com `?enc=b64` como fallback para
assets que sofrem content-sniffing (corrompem MIME/binário).

## 2. Escopo

In-scope:

- `src/OpenHandsNET.Client/wwwroot/js/boot.js`: desliga autostart
  (`Blazor.start({...})` manual — API `blazor.webassembly.js` v10),
  intercepta `loadBootResource` reescrevendo URLs de `_framework` que
  não sejam `.js` para `/framework-assets/{stem}/{ext}`; fallback
  `?enc=b64` quando o fetch binário falha/vem com tipo errado.
- Endpoint mirror na API: `GET /framework-assets/{stem}/{ext}` →
  resolve o arquivo fingerprintado de `_framework` correspondente
  (`stem.ext` → `_framework/{stem}.{fingerprint}.{ext}` via
  `IWebHostEnvironment`/`manifest` de static assets), serve com
  `Content-Type` correto + cache headers idênticos aos assets
  originais; `?enc=b64` devolve o conteúdo em base64
  (`application/octet-stream` texto) para o cliente decodificar
  localmente.
- `index.html`/host: incluir `boot.js` antes do
  `blazor.webassembly.js` com `autostart="false"`.
- Segurança do mirror: whitelist de extensões (`wasm`, `dat`, `dll`,
  `pdb`? — apenas o que o boot realmente baixa), `stem` restrito a
  `[a-zA-Z0-9._-]+` sem path traversal, somente GET/HEAD, sem listagem.
- Testes: endpoint serve asset conhecido, rejeita `..`, rejeita ext
  fora da whitelist, `?enc=b64` devolve base64 válido decodificável
  para o bytes originais.
- Docs: `docs/deployment/` — como o mirror ajuda atrás de proxy; nada
  muda para ambientes sem proxy (rota normal continua).

Out-of-scope: service worker/PWA offline; compressão Brotli
customizada; CDN; alterar pipeline de publish dos static assets.

## 3. Requisitos

1. **RF-001 — Boot manual**: `autostart="false"` + `boot.js` chamando
   `Blazor.start()`; app sobe identico em ambiente sem proxy
   (regressão: smoke e2e/manual).
2. **RF-002 — Rewrite de recurso**: `loadBootResource` (ou hook
   equivalente .NET 10) redireciona apenas tipos/extensões
   configurados (`.wasm`, `.dat`, `.dll` — não `.js`/`css`) para o
   mirror; falha no mirror → fallback `?enc=b64` → só então erro.
3. **RF-003 — Mirror correto**: `{stem}/{ext}` resolve o arquivo
   fingerprintado certo; `Content-Type` igual ao asset original;
   `Cache-Control`/`ETag` preservados.
4. **RF-004 — Hardening**: `stem` validado (`^[a-zA-Z0-9._-]+$`, sem
   `..`, sem `/`), extensões na whitelist, 404 padrão fora dela;
   sem directory listing; somente leitura de `_framework`.
5. **RF-005 — Observabilidade**: miss/hit do mirror loga em `Debug`;
   fallback b64 conta métrica/log `Information` (sinal de proxy
   hostil).

## 4. API Contract

```
GET /framework-assets/{stem}/{ext}[?enc=b64]
  200 → body = bytes do asset _framework/{stem}.{fingerprint}.{ext}
        (?enc=b64 → body = base64 dos bytes, Content-Type text/plain)
  400 → stem/ext inválido ou fora da whitelist
  404 → asset inexistente
```

## 5. Critérios de aceite

- App sobe com boot manual idêntico ao autostart (smoke manual +
  teste de integração do endpoint).
- Request `/framework-assets/dotnet/wasm` serve o wasm fingerprintado;
  `/framework-assets/../../etc/passwd`-style → 400.
- Simular bloqueio (ex.: host local servindo mirror + regra de teste)
  demonstra o fallback — evidência no PR.
- `dotnet test` verde; build intacto.

## 6. Task Plan

1. Endpoint mirror + whitelist + testes.
2. `boot.js` + `autostart=false` + rewrite + fallback b64.
3. Smoke manual + docs de deployment.

## 7. Deliverables

- Mirror endpoint, `boot.js`, host page atualizado, testes, docs.

## 8. Referências

- `src/OpenHandsNET.Api/Program.cs:320-323` (`MapStaticAssets`),
  `:412` (`MapFallbackToFile`), `Client/wwwroot/` (host page),
  `Client/wwwroot/js/` (padrão de interops existentes).
- `open-webui` CLAUDE.md hard rule do boot WASM + `js/boot.js` +
  `/framework-assets` (spec/implementation de referência).

## 9. Guardrails

- `MapStaticAssets()` permanece — o mirror **complementa**, não
  substitui (open-webui mantém a mesma regra: `UseStaticFiles`/
  `MapFallbackToFile` não resolvem os placeholders `#[.{fingerprint}]`).
- Whitelist fechada de extensões; nada de mirror genérico de arquivos.
- `boot.js` é infra de boot — lógica mínima, sem framework JS novo.

## 10. DoD

- Boot manual + mirror + fallback funcionando; testes de endpoint;
  smoke ok; docs; build/test verdes.
