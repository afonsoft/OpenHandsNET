# SPEC — 147 · Product polish (quick wins da análise 2026-11)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `product-polish` |
| Type | `Feature / Security-hardening + UX (bundle)` |
| Stack | `.NET 10 · ChatOps Slack · Git · Client · Usage` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | `GAP-POLISH-QW` (docs/research/2026-11-competitive-gap-analysis.md §G8) |
| Depends on | `54` (Slack), `12`/`52` (git/SCM), `20` (usage/quotas), `60` (indexing) |
| Reference | Devin bot-comment settings; OH 0.70 "Your Budget" — conceitos apenas |
| Touches | `src/Sandcastle.{Infrastructure,Application,Api,Client}`, `cli/`, `tests/` |

## 1. User Story

**As a** org admin / usuário diário
**I want** cinco correções de produto pequenas e independentes
**So that** fechamos wins baratos identificados no gap analysis —
nenhum requer SPECs separadas. Cada RF é um PR fatiável (ou um PR
com commits separados); são independentes entre si.

## 2. Scope (RF)

### RF-001 — Sanitizar pings Slack em todo texto outbound

- Problema: `ChatOpsReporter` posta `summary` gerado pelo LLM com
  `SecretRedactor` mas sem neutralizar `@channel`, `@here`,
  `@everyone`, `<!channel>`, `<!here>`, `<!everyone>`, `<!subteam^…>`
  — um resumo pode pingar o canal inteiro
  (`src/Sandcastle.Infrastructure/ChatOps/ChatOpsReporter.cs:41-49`).
- `SlackTextSanitizer.NeutralizePings(string)` central no
  `SlackChatSink.PostMessageAsync`/`UpdateMessageAsync` (ponto único
  de saída): escapa/limpa os padrões (`@channel`→`@​channel` com
  ZWSP ou backtick-quote), mantém `<@U123>` de usuários válidos.
- `ChatOps:AllowGroupMentions=false` (org setting) — quando true,
  pings de grupo permitidos explicitamente.
- Teste: summary contendo `@channel` sai neutralizado.

### RF-002 — Commit authorship configurável

- `GitOptions.AuthorName/AuthorEmail` hoje é global
  (`sandcastle-agent`). Adicionar resolução por escopo:
  user setting → org setting → global (mesmo padrão de precedência
  de secrets/settings existentes).
- Trailer `Co-authored-by: {user display name} <{user email}>` nos
  commits do agente quando `Git:CoAuthorUser=true` (default true) —
  atribuição real como Devin/OH fazem.
- UI: campo em Settings (perfil) + Settings org (admin).

### RF-003 — Preview renderizado `.md`/`.patch`

- File viewer/artifacts: arquivos `.md`/`.markdown` renderizam
  (viewer existente ganha toggle Rendered/Source); `.patch`/`.diff`
  ganham syntax highlight de diff (Monaco já suporta `diff`).
- i18n toggle label em 8 locales; drift guards
  (`check-css-classes.py`) verdes.

### RF-004 — Budget restante (card "Your Budget")

- `GET /api/v1/usage/budget` → `{monthly_limit, consumed, remaining,
  period_start, period_end, by_model[]}` sobre `IUsageService`
  (já mede tokens/custo por usuário; org admin vê da org).
- Settings > Usage: card com barra de consumo + restante + gráfico
  diário por modelo (mês corrente) — paridade OH 0.70.
- i18n 8 locales.

### RF-005 — Contador "X/Y repos indexados"

- `GET /api/v1/indexing/status` retorna `{total_connected, indexed,
  indexing, failed}` (parcial já existe em SPEC-145 RF-003 — se
  145 não estiver merged, implementar aqui o mínimo: status por repo).
- UI Ask/indexing: badge "X/Y repos indexed" + link p/ gerenciar.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| GET | `/api/v1/usage/budget` | budget do caller (`?org=` p/ admin) |
| GET | `/api/v1/indexing/status` | resumo X/Y |

## 4. Data model

- `user_settings`/`org_settings` + `git_author_name`,
  `git_author_email`, `coauthor_enabled` — migration leve (ou
  key-value existente se houver store de settings genérico —
  decidir na implementação seguindo o padrão de settings atual).

## 5. NFR / Security

- Sanitizer roda server-side sempre (não depende do cliente Slack).
- Author email do usuário é PII — só vai ao commit se
  `coauthor_enabled` e o usuário tem e-mail verificado; org pode
  forçar author neutro.
- Budget endpoint scoped ao caller (ou `perm:org.admin` p/ org view).

## 6. Task Plan

1. RF-001 sanitizer + testes (slice isolado, merge primeiro).
2. RF-002 authorship chain + trailer + settings UI.
3. RF-003 previews.
4. RF-004 budget endpoint + card.
5. RF-005 counter.
6. i18n + drift guards + golden.

## 7. Deliverables

Cinco wins entregues: Slack seguro contra pings, authorship real de
commits, previews úteis, budget visível, contador de indexação.

## 8. Referências

- `docs/research/2026-11-competitive-gap-analysis.md` §4 (quick wins).
- `ChatOpsReporter.cs:41-49`, `GitOptions.cs:16-19`,
  `GitService.cs:199-204` (evidências).

## 9. Guardrails

- Cada RF independente — PRs podem ser separados; nenhum quebra API.
- Sanitizer não remove menções a usuários válidos `<@U…>`.

## 10. DoD

- Teste: texto com `@channel`/`@here`/`<!everyone>` → neutralizado;
  `<@U123>` intacto.
- Commit com `Co-authored-by` quando habilitado; author respeita
  precedência user→org→global.
- `.md` renderiza; `.patch` highlight; budget endpoint + card;
  contador X/Y visível.
- Suite verde; i18n parity; drift guards verdes.
