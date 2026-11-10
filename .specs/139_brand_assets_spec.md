# SPEC-139 — Sandcastle brand assets

- **Status**: Draft
- **Tipo**: feature/brand
- **Prioridade**: média (pós-beta.2)

## Contexto

SPEC-138 renomeou o produto para **Sandcastle** (repo `afonsoft/sandcastle`,
release `v0.1.0-beta.2`). Os assets visuais ainda são genéricos/placeholder.
A marca precisa de um ícone-logo "castelo de areia" consistente em todos os
pontos de contato públicos.

## Requisitos

### RF-001 — Conceito
Ícone-logo "castelo de areia" minimalista: silhueta de castelo (torre +
bandeira ou conchas/areia), flat/monocromático-capaz, legível a 16×16.
Vetor-mestre em SVG (source of truth), variantes dark/light.

### RF-002 — Deliverables
- `logo.svg` + `logo-dark.svg`/`logo-light.svg` em `docs/brand/` (fonte).
- **Favicon**: `favicon.svg` + `favicon.ico` (fallback 32×32/16×16) em
  `src/Sandcastle.Client/wwwroot/`.
- **PWA icons**: `icon-192.png`, `icon-512.png`, `icon-maskable-512.png`
  (safe-zone ≥10%) + atualização de `manifest.webmanifest`.
- **README badges visuais**: header do `README.md`/`README.en.md` com logo +
  badges (release, license, CI, Sonar).
- **GitHub social preview**: `docs/brand/social-preview.png` 1280×640
  (logo + wordmark "Sandcastle" + tagline curta), referenciada nas
  repo settings (upload manual humano).

### RF-003 — Critérios de aceite
- [ ] SVG mestre legível a 16px; sem texto rasterizado (wordmark via fonte).
- [ ] Favicon + PWA icons presentes e referenciados no `index.html`/`manifest`.
- [ ] Maskable icon passa no safe-zone check (maskable.dev).
- [ ] README header renderiza bem em light/dark do GitHub.
- [ ] Social preview gerado e asset commitado em `docs/brand/`.
- [ ] Nenhuma referência residual "OpenHandsNET" nos assets.

### RF-004 — Fora de escopo
Anúncio público, org `sandcastle-cloud`, store listings, docs i18n de marca.

## Riscos
- Raster exports devem ser gerados do SVG (não desenhados à mão) —
  reproducibilidade via script em `tools/brand/`.
- PWA icon cache: bump de `?v=` no manifest se necessário.
