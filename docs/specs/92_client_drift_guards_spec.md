# SPEC — 92 · Client drift guards — css-classes, form a11y, i18n parity

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `client-drift-guards` |
| Type | `Infra / CI` |
| Stack | `.NET 10 · Blazor WASM · Tailwind v4 · Python 3 scripts` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed` |
| Ticket | #235 (Epic #231) · análise `afonsoft/open-webui` (2026-10-22) |
| Depends on | — |
| Reference | `open-webui` `tools/check-css-classes.py`, `check-form-a11y.py`, `check-i18n-parity.py`, `check-i18n-placeholders.py` (CI job "Blazor WASM Client Validation") |
| Touches | `tools/` (novos scripts), `.github/workflows/ci.yml` (job novo ou step no existente — **ver guardrail workflows**), `docs/` |

## 1. User Story

**As a** mantenedor do client Blazor WASM
**I want** gates de drift executáveis localmente e no CI — classes CSS
usadas vs definidas, controles de formulário sem nome acessível, e
paridade de chaves/placeholders entre os 8 locales
**So that** `.razor` não acumule classe Tailwind órfã ou inexistente,
form sem acessibilidade, ou chave i18n presente em `en-US` e ausente em
`pt-BR` — tudo hoje **sem nenhuma verificação automática**.

**Contexto:** o client já commita 8 locales
(`src/OpenHandsNET.Client/wwwroot/i18n/{de,en,es,fr,it,ja,pt,zh}.json`
+ `locales.json`) consumidos por `LocalizationService`, e os `.razor`
usam `L["key"]` + classes Tailwind livremente. `scripts/` tem
`check-spec-lifecycle.sh`, `check-env-drift.sh`, `check-pr-test-policy.sh`
e `check-quality-ratchet.py` — **nenhum** cobre client drift. O
open-webui roda 4 guards equivalentes no CI (job "Blazor WASM Client
Validation") exigindo que PRs que tocam `.razor`/i18n rodem os checks
antes.

## 2. Escopo

In-scope:

- `tools/check-css-classes.py`: extrai classes usadas em `.razor`/`.cs`
  do Client e compara com as definidas em `wwwroot/css/tailwind.css`
  (saída gerada) + utilitários estáticos do app — falha listando
  classe→arquivo:linha sem definição (whitelist para classes
  dinâmicas/geradas por JS interop, ex.: montadas por string concat).
- `tools/check-form-a11y.py`: varre `.razor` por `<input>/<select>/
  <textarea>/<button>` sem nome acessível (`aria-label`, `aria-labelledby`,
  `id`+`label for`, ou texto interno para button) — falha listando
  arquivo:linha.
- `tools/check-i18n-parity.py`: todas as chaves de `en-US.json` (locale
  de referência) presentes nos demais 7 + `locales.json` consistente;
  chaves órfãs (presentes em locale não-en e ausentes em en-US)
  reportadas como warning ou erro conforme flag `--strict`.
- `tools/check-i18n-placeholders.py`: placeholders `{name}`/`{{name}}`
  nos valores têm a mesma aridade/conjunto em todos os locales (falha
  comum: tradução que remove `{count}`).
- Wiring de CI: step/job no workflow existente rodando os 4 scripts
  (read-only, `python3`, sem deps além de stdlib) — seguir o padrão do
  `check-spec-lifecycle.sh`; **mudança em `.github/workflows/` exige
  revisão humana por hard rule → o workflow change vai no mesmo PR e
  fica explícito na descrição.**
- Documentação: `docs/` (ou `CLAUDE.md` "Comandos") com os 4 comandos;
  exit codes 0/1 + saída `arquivo:linha` igual aos guards atuais.

Out-of-scope: lint de Razor em si, verificação visual (e2e), auditoria
Lighthouse (ver `web-design-guidelines` skill — camada diferente),
reescrita dos locales existentes para cobrir chaves faltantes
(o guard *reporta*; preenchimento fica como follow-up se houver drift
real detectado na primeira rodada — registrar no PR).

## 3. Requisitos

1. **RF-001 — css-classes**: roda em <10s no repo; detecta classe em
   `.razor` ausente do CSS gerado; suporta whitelist file
   (`tools/css-class-allowlist.txt`) para dinâmicas; exit 1 com lista.
2. **RF-002 — form-a11y**: cobre input/select/textarea/button; ignora
   controles com nome acessível por qualquer um dos 4 mecanismos;
   exit 1 com `arquivo:linha`.
3. **RF-003 — i18n-parity**: en-US é referência; cada chave faltante em
   outro locale → `locale: chave` na saída; exit 1 se qualquer ausência;
   `--report-only` para baseline inicial.
4. **RF-004 — i18n-placeholders**: para cada chave, o conjunto de
   placeholders é idêntico nos 8 locales; divergência → exit 1.
5. **RF-005 — CI**: os 4 scripts rodam no CI em PRs que tocam
   `src/OpenHandsNET.Client/**` (path filter, reusando
   `classify-changes.sh` se aplicável).
6. **RF-006 — Baseline**: primeira execução documenta o estado atual;
   se houver drift real, o PR corrige os casos triviais (chaves
   faltantes óbvias, aria-labels) ou registra follow-up — o gate entra
   verde no merge.

## 4. API Contract

Sem API. Interface de scripts:

```bash
python3 tools/check-css-classes.py        # exit 0/1
python3 tools/check-form-a11y.py          # exit 0/1
python3 tools/check-i18n-parity.py        # [--strict|--report-only]
python3 tools/check-i18n-placeholders.py  # exit 0/1
```

## 5. Critérios de aceite

- Script introduzido detecta caso plantado (remover uma chave de
  `pt-BR.json` → parity falha; remover `aria-label` → a11y falha).
- CI executa os guards em PR tocando Client (evidência no check run).
- Baseline documentada no PR (drift encontrado e destino de cada caso).
- `dotnet build`/`test` intactos; scripts stdlib-only.

## 6. Task Plan

1. Portar/adaptar os 4 scripts para a estrutura do Client (paths,
   tailwind.css gerado em `wwwroot/css`, locales).
2. Rodar baseline; corrigir drift trivial ou registrar follow-up.
3. Wiring no CI (mudança de workflow sinalizada no PR para revisão).
4. Docs de comandos.

## 7. Deliverables

- 4 scripts + allowlist + step de CI + baseline + docs.

## 8. Referências

- `src/OpenHandsNET.Client/wwwroot/i18n/*.json` (8 locales),
  `Services/LocalizationService.cs`, `wwwroot/css/tailwind.css`
  (artefato gerado — ver convenção open-webui: nunca editar manual),
  `scripts/check-spec-lifecycle.sh` (padrão de guard local+CI),
  `scripts/classify-changes.sh` (path filter existente).
- `open-webui/tools/check-*.py` + CLAUDE.md seção "drift guards".

## 9. Guardrails

- **`.github/workflows/` só com revisão humana** — incluir o diff do
  workflow explicitamente no corpo do PR.
- Scripts stdlib-only (sem `pip install` no CI); não tocar
  `wwwroot/css/tailwind.css` manualmente.
- Sem suppressions: drift legítimo corrige o código ou entra em
  allowlist justificada, nunca `sys.exit(0)` condicional escondido.

## 10. DoD

- 4 guards verdes local+CI; baseline documentada; drift inicial
  resolvido ou triado; build/test/format verdes.
