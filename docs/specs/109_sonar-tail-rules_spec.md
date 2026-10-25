# SPEC — 109 · SonarCloud tail — regras sem slice (S927, scripts, infra)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `sonar-tail-rules` |
| Type | `Fix` |
| Status | `Completed` |
| Ticket | gap-analysis 2026-10-24 · GAP-sonar-tail-rules |
| Scope | regras abertas não cobertas por SPECs 103–107 nem #155/#156 |

## 1. User Story

**As a** mantenedor, **I want** as regras remanescentes fora das fatias
grandes tratadas, **so that** o backlog Sonar zera ou vira won't-fix
documentado.

## 2. Contexto / escopo

Live 2026-10-24, não cobertos por SPEC 103–107 nem SPEC-74 (#156,
MINOR — cobre S8970×144):

- `csharpsquid:S927` ×35 CRITICAL — `cli/OpenHandsNET.Cli/Commands/*`
  (Parity ×8, Eval ×6, Conversations/Scm/ServiceUser/Ticket ×3, Chat ×2…)
- python ×5: S5655×2, S3776×2, S1192×1 — `tools/` scripts
- shelldre ×14: S131, S7688×9, S7677×2, S7682, S7679 — `scripts/`
- docker ×4: S7031×2, S7026, S7018 — Dockerfile
- kubernetes ×2: S6596, S6897 — `charts/`
- cs leftovers MINOR/MAJOR: S3267×9, S6673, S1118, S1172, S1905×2,
  S3400, S1199, S1125, S2737, S1135, S1994, S3871, S4487×3
- js first-party (fora de docs/ e wwwroot/lib): monacoInterop S6582×9,
  boot.js ×3, index.html Web ×1 (verificar se exclusão ci.yml:354
  fecha — dep. GAP-sonar-exclusion)

## 3. Requisitos

- RF-001: corrigir/triar cada ocorrência — won't-fix documentado onde
  justificado (vendored, gerado, semântica sensível).
- RF-002: testes de regressão para mudanças em `src|cli|runtime`.
- RF-003: `dotnet format` + build 0 warnings + suite verde.

## 4. Acceptance

- [ ] Todas as regras do escopo zeradas ou won't-fix documentado.

## 4. Execução (2026-10-24)

**Fixes reais:**
- `csharpsquid:S927` ×35 — params `ct`→`cancellationToken`,
  `ctx`→`context` nos overrides `ExecuteAsync` do CLI.
- python: S5655 ×2 (`Counter()` no lugar de `{}`), S1192
  (`BASE_LOCALE` const), S3776 ×2 (`check_file` split em 4 checks;
  `hub_first_event` extrai `_invocation_frame`).
- shelldre ×13: `[[` condicionais, `>&2` no erro, `*)` default,
  var local p/ $1 em `has()`, returns explícitos.
- docker S7018: pacotes apt ordenados alfabeticamente.
- kubernetes S6897: `ephemeral-storage` request; S6596: tag default
  `latest`→`"1.0"` (chart appVersion) em ambos os images.
- cs leftovers: S3267 ×5 (Where/Select), S1125 (`!TimedOut`),
  S3400 (`SquidConf` const), S1172 (SetStatusAsync ct),
  S2737 (catch-rethrow removido), S1135 (comentário reworded),
  S1199 (já resolvido via SPEC-107 extração).

**Won't-fix justificado:**
- docker S7026 (curl→ADD): o setup NodeSource verifica SHA256 do
  script — ADD remoto não suporta checksum (regressão de segurança).
- docker S7031 ×2: RUNs são camadas lógicas distintas (apt base,
  gh-cli, nodesource, ttyd) — merge prejudica cache/legibilidade.
- S1905 McpClientService: cast `(string?)` exigido pelo target
  `IDictionary<string,string?>` — confirmado por CS8619 sem ele.
- S1118 MarkdownService: DI-registered + instanciado em testes.
- S3267 OpenAiGatewayEndpoints: loop tem side-effect (WriteChunkAsync).
- js leftovers (monacoInterop/boot/index.html): cobertura da
  exclusão #248 — pending verificação do scanner.

**Regressão:** `SquidConf_Is_Deny_By_Default` cobre a const S3400.
