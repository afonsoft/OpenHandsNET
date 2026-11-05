# SPEC — 117 · PWA + assets + dead-weight cleanup

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `pwa-assets-cleanup` |
| Type | `Infra` |
| Status | `Completed` |
| Ticket | #319 (Epic #317) · PR #327 — gap-analysis OpenWebUiCode 2026-11-04 |
| Scope | `src/OpenHandsNET.Client/wwwroot/` + `index.html` + hosting WASM no `OpenHandsNET.Api` |

## 1. User Story

**As a** usuário do OpenHandsNET, **I want** o app instalável (PWA)
com ícones, splash e identidade visual completos, **so that** a
experiência seja indistinguível de um app desktop ao estilo Devin
Cloud/OpenWebUiCode — e o payload não carregue assets mortos.

## 2. Contexto / escopo

AS-IS vs OWUIC (evidência):

| Item | OWUIC | OHN |
|---|---|---|
| `manifest.webmanifest` | ✔ | — |
| `service-worker.js` | ✔ (registrado no index) | — |
| `favicon.svg` | ✔ | só `favicon.png` |
| `assets/icons/icon-192/512.png` | ✔ | `icon-192.png` na raiz |
| `assets/splash-dark.png` (boot) | ✔ | `loading-progress` svg |
| `assets/logo.png` | ✔ | — |
| `img/thinking-brain.svg` | ✔ | — |
| `lib/bootstrap/` | — | presente, **zero refs** em .razor/.html/.css (~dead weight) |
| meta `description`/`theme-color` | ✔ | — |
| `<html lang>` | `pt-BR` | `en` (i18n existe — SPEC-98) |

Fora de escopo: redesign visual (SPEC-116), conteúdo de páginas,
offline data sync (SW faz cache de shell/framework apenas).

## 3. Requisitos

- RF-001 — **Manifest**: `manifest.webmanifest` (name, short_name,
  icons 192/512, `display: standalone`, `theme_color`,
  `background_color`, `start_url: "/"`) + `<link rel="manifest">`.
- RF-002 — **Service worker**: portar o padrão do OWUIC
  (`service-worker.js`): cache-first p/ `_framework`+assets estáticos
  com versionamento, network-first p/ `api/`/`hubs`, skip do blazor
  boot; registrar no `index.html` atrás de `'serviceWorker' in
  navigator`. Blazor WASM assets com fingerprint seguem
  `MapStaticAssets`.
- RF-003 — **Ícones/imagens**: `favicon.svg` + reorganizar ícones sob
  `assets/icons/` (192/512) + `assets/logo.png` +
  `assets/splash-dark.png` (usado pelo `.boot-splash` da SPEC-116) +
  `img/` p/ ilustrações (thinking-brain). Gerar arte própria do
  OpenHandsNET (monograma "OH") — não reusar logo OWUIC.
- RF-004 — **Metas**: `<meta name="description">`,
  `<meta name="theme-color">` claro/escuro (media),
  `<html lang>` dinâmico via i18n (en-US default; `lang` reflete o
  locale ativo em `LocalizationService`).
- RF-005 — **Cleanup**: remover `wwwroot/lib/bootstrap/**`
  (confirmado zero refs) e `wwwroot/sample-data/weather.json`
  (vestígio do template) após grep final de segurança; documentar no
  PR. Regra da casa: deleção exige justificativa — registrar aqui.
- RF-006 — **Testes/verificação**: bUnit n/a (assets); validar
  manualmente `manifest` via DevTools + Lighthouse PWA check;
  CI `check-css-classes`/`check-form-a11y` inalterados.

## 4. Acceptance

- [ ] `manifest.webmanifest` servido com MIME correto; install prompt
      disponível no Chromium; Lighthouse "PWA" ≥ critério installable.
- [ ] SW registrado; segundo load serve shell de cache (offline
      abre boot-splash); nenhum asset `_framework` falha após reload.
- [ ] `favicon.svg` + icons 192/512 + splash-dark renderizam;
      sem refs quebradas (404) no console de rede.
- [ ] `lib/bootstrap` removido; nenhum `lib/bootstrap` referenciado
      (grep no repo = 0); tamanho do publish cai.
- [ ] `lang` do `<html>` segue o locale ativo após switch de idioma.
- [ ] `dotnet build`/`test` verde; docker compose sobe e serve assets.

## 5. API Contract

Sem mudanças de API. Requisito de hosting: `MapStaticAssets` expõe
`manifest.webmanifest`, `service-worker.js` no root scope (SW precisa
de scope `/`); `service-worker.js` **não** deve ser servido com
immutable-cache longo (header `Cache-Control: no-cache` ou equivalente
— verificar comportamento do `MapStaticAssets` e ajustar com
`MapFallback`/header middleware se necessário).

## 6. Task Plan

1. Manifest + icons + metas + lang dinâmico.
2. Service worker + registro + política de cache + header do SW.
3. Assets (logo/splash/favicon.svg próprios).
4. Remoção bootstrap/sample-data + greps de segurança.
5. Lighthouse installable + docs (`docs/` seção PWA).

## 7. Riscos / notas

- SW + `boot.js` (`autostart=false`) + espelho `/framework-assets`
  (SPEC-94): o SW não pode interceptar/quebrar o mirror — testar com
  proxy corporativo simulado ou documentar limitação.
- `icon-192.png` existe na raiz: mover para `assets/icons/` atualiza
  refs (verificar PWA/OG tags e `.csproj` globbing).
- Arte nova (logo/splash/favicon): gerar SVG simples e derivar PNGs —
  não commitar binários > 200 KB por asset.
