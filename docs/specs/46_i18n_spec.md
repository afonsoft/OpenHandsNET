# SPEC — 46 · i18n (JSON Locales + LocalizationService)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `i18n` |
| Type | `Feature / Client` |
| Stack | `Blazor WASM · JSON dictionaries` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed` |
| Ticket | [#104](https://github.com/afonsoft/OpenHandsNET/issues/104) |
| Depends on | `26` (prefs endpoint + settings) |
| Reference | `afonsoft/open-webui` (MIT — port permitido): `LocalizationService`, `wwwroot/i18n/*.json` (8 locales), `tools/check-i18n-parity.py` |
| Touches | `src/OpenHandsNET.Client` (todos `.razor` chrome + `wwwroot/i18n`), `tests` (parity), `SettingsPage` |

## 1. User Story

**As a** usuário do OpenHandsNET
**I want** a UI no meu idioma, trocável em Settings sem reload
**So that** o produto deixa de misturar pt-BR e inglês hardcoded —
hoje **zero** i18n (`grep`: nenhum `.resx`/`IStringLocalizer`, strings
soltas nos dois idiomas).

**Contexto:** o fork usa **JSON dictionaries + serviço próprio**
(`i18n/{lang}.json`, fallback ativo→en→pt-BR→chave, `{{name}}`,
`Changed` event sem reload, `localStorage`) — **não** resx nem
`IStringLocalizer` (escolha acertada p/ WASM: sem satellite assemblies,
chave legível no diff). 8 locales com pt-BR default e guard de paridade
em CI. Decisões travadas: **portar o JSON service**, en-US+pt-BR
completos, demais 6 locales best-effort nas chaves portadas, guard de
paridade como **teste xUnit** (zero mudança de workflow).

## 2. Scope (RF)

### RF-001 — `LocalizationService` (port)

- `Services/LocalizationService.cs`: carrega `i18n/{lang}.json` via
  `HttpClient` (cache), `L["key"]` indexer + `L.T("key", ("name", v))`
  interpolação `{{name}}`, cadeia de fallback `active → en-US → pt-BR →
  key`, evento `Changed` (re-render sem reload), `Culture` para datas
  (CultureInfo), persiste `localStorage ohn.locale`.
- `L` injetado via `_Imports.razor`/base class p/ ergonomia.

### RF-002 — Locales

- `wwwroot/i18n/locales.json` (lista {code,name}) +
  **`en-US.json` + `pt-BR.json` completos** (100% das chaves do app) +
  `es-ES, fr-FR, de-DE, it-IT, ja-JP, zh-CN` com cobertura das chaves
  portadas dos componentes do fork (best-effort — missing → fallback
  en-US, não erro).
- Default do produto: **pt-BR** (fallback final da cadeia é pt-BR? —
  decidir: pt-BR default consistente com fork + CLAUDE.md pt-BR;
  cadeia `active → pt-BR → en-US → key` se pt-BR for o default.
  Implementação trava a ordem: default=pt-BR, fallback=en-US).

### RF-003 — Sweep de strings

- Todo texto user-facing em `.razor` chrome (sidebar, header, tabs,
  banners, botões, empty states, admin pages, toasts fixos) vira
  `L["area.element"]` — convenção chave flat `area.element` (ex.:
  `chat.send`, `sidebar.conversations`, `attention.empty`).
- Exceções documentadas: nomes de tools/events técnicos, enums de API,
  timestamps relativos via `L.T` com count.
- Strings **server-side** (ProblemDetails, audit) permanecem inglês —
  contrato de API não localiza.

### RF-004 — Seletor de idioma

- `SettingsPage`: dropdown `locales.json` → aplica imediato + persiste
  `localStorage` + `PUT /api/v1/users/me/preferences {locale}` (merge
  em `PreferencesJson` — endpoint da SPEC-26, extensão aditiva).

### RF-005 — Parity guard

- `tests/OpenHandsNET.Client.Tests/I18nParityTests.cs`: carrega os
  `wwwroot/i18n/*.json`, asserta key-set ⊆ en-US em todos os locales,
  zero valores vazios em en-US/pt-BR, `locales.json` ⇄ arquivos.
- Sem mudança em `.github/workflows/` — o teste roda no `dotnet test`
  normal do CI.

### RF-006 — Integração SPECs paralelas

- Chaves novas das SPECs 41–45/47 entram com `en-US`+`pt-BR` obrigatórios
  no merge (nota em cada SPEC/PR da wave; sweep final nesta SPEC cobre o
  resto).

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| PUT | `/api/v1/users/me/preferences` | `{locale?}` merge em `PreferencesJson` (aditivo — já existe) |

Sem endpoint novo; locale é client-driven.

## 4. Data model

`UserRecord.PreferencesJson` — coluna existente (SPEC-26), sem migration.

## 5. NFR / Security

- Missing key nunca quebra render — retorna a própria chave (visível no
  dev, seguro em prod).
- Locale fora da whitelist `locales.json` → fallback default (anti
  path-injection em `i18n/{lang}.json` fetch — `{lang}` validado
  `^[a-z]{2}(-[A-Z]{2})?$`).
- JSONs são assets estáticos públicos — nunca contêm dados de usuário.

## 6. Task Plan

1. `LocalizationService` + loader + fallback + `Changed` + prefs merge;
   testes.
2. `wwwroot/i18n/` skeleton (locales.json + en/pt) + sweep
   `MainLayout`/sidebar/header/tabs/banners.
3. Sweep completo (admin, settings, conversation, empty states) + chaves.
4. Seletor Settings + `Culture` datas.
5. 6 locales best-effort + `I18nParityTests`.
6. Sweep final + `dotnet test` verde.

## 7. Deliverables

`LocalizationService`, `wwwroot/i18n/` (8+1 arquivos), sweep `.razor`,
language selector, `I18nParityTests`, doc `docs/ui/i18n.md` (convenção
de chaves + como adicionar locale).

## 8. Referências

- `afonsoft/open-webui` (MIT): `Services/LocalizationService.cs`,
  `wwwroot/i18n/*.json`, `tools/check-i18n-parity.py`,
  `docs/specs/SPEC-20261001-i18n.md` + `-i18n-locales.md` +
  `-i18n-parity.md`.
- `docs/specs/26` (`PUT /users/me/preferences`, SettingsPage).

## 9. Guardrails

- JSON service, **não** resx/`IStringLocalizer` (paridade fork + WASM).
- Server-side messages ficam em inglês (API contract).
- Novo locale só entra com key-set completo de en-US ou explicitamente
  marcado `partial` em `locales.json`.
- `.github/workflows/` intocado; `main` só via PR.

## 10. DoD

- Trocar pt-BR↔en-US em Settings aplica sem reload e persiste por usuário.
- `I18nParityTests` verde: key-set pt-BR==en-US, parciais ⊆ en-US.
- Zero string pt-BR/en hardcoded nos componentes chrome (sweep grep).
- `dotnet test` verde.
