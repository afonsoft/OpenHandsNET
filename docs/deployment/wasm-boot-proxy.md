# WASM boot atrás de proxies corporativos (SPEC-94)

Clientes enterprise frequentemente rodam atrás de proxies que bloqueiam
downloads por extensão (`.wasm`, `.dat`, `.dll`) ou fazem content-sniffing
de payloads binários — o boot do Blazor WASM travava numa tela em branco.

## Como funciona

`index.html` carrega `blazor.webassembly.js` com `autostart="false"` e
`js/boot.js` assume o boot manualmente (`Blazor.start`). Para cada
recurso de `_framework` que não seja `.js`, `loadBootResource` tenta:

1. `GET /framework-assets/{stem}/{ext}` — mesmos bytes, sem a extensão
   bloqueada na URL; integridade validada nativamente pelo runtime.
2. `GET /framework-assets/{stem}/{ext}?enc=b64` — payload base64
   `text/plain`, derrota content-sniffing; o cliente revalida o SHA-256
   (`crypto.subtle`) contra o hash de integridade do boot manifest.
3. `defaultUri` — URL original (`_framework/...`) como fallback final.

Falha total → `#app` mostra mensagem legível (pt-BR/en-US).

## Endpoint mirror

`GET /framework-assets/{stem}/{ext}` — read-only, anônimo, whitelist
fechada (`wasm`, `dat`, `dll`, `pdb`, `json`, `js`, `map`), `stem`
validado (`^[A-Za-z0-9._-]+$`, sem `..`), serve `.br`/`.gz` irmãos
conforme `Accept-Encoding`, `Cache-Control: immutable` idêntico ao asset
original. `?enc=b64` devolve base64 + loga `Information` (sinal de proxy
hostil — métrica de observabilidade).

## Ambientes sem proxy

Nada muda — a rota normal `_framework/*` continua servida por
`MapStaticAssets()`; o mirror só complementa. O custo é um request a
mais por asset (hittável em `Debug` logs).
