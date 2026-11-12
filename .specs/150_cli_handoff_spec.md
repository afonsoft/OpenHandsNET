# SPEC — 150 · CLI Handoff (`sandcastle handoff` local ↔ cloud)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `cli-handoff` |
| Type | `Feature / CLI + API` |
| Stack | `.NET 10 · sandcastle CLI (Spectre) · RuntimeApi · conversation resume` |
| Repository | `afonsoft/sandcastle` |
| Status | `Draft` |
| Ticket | `GAP-CLI-HANDOFF` (docs/research/2026-11-competitive-gap-analysis.md §2.3) |
| Depends on | `04` (start-task), `12` (git), `124` (MCP/CLI), `145` (clone provider) |
| Reference | Devin `handoff`/`pickup`/`ssh` (CLI↔cloud task transfer) — conceitos apenas |
| Touches | `cli/`, `src/Sandcastle.{Api,Application,Infrastructure}`, `tests/` |

## 1. User Story

**As a** dev trabalhando com o `sandcastle` CLI na minha máquina
**I want** `sandcastle handoff` empacotar minha sessão local (diff,
contexto, prompts) e continuar a mesma tarefa numa conversa cloud com
sandbox — e `sandcastle pickup <conv>` trazer uma conversa cloud de
volta para o meu working tree local
**So that** paridade com o `handoff`/`pickup` do Devin. Hoje o CLI
roda contra a API mas não existe transferência de contexto vivo.

## 2. Scope (RF)

### RF-001 — `sandcastle handoff` (local → cloud)

- No working tree local (deve estar num repo git com remote
  associado a uma SCM connection da org):
  1. Coleta: `git status` + `git diff` (unstaged+staged, incl. untracked
     respeitando `.gitignore` e `Handoff:MaxDiffKb=512`) + metadata
     (branch, base sha, repo url) + resumo opcional do usuário.
  2. `POST /api/v1/conversations/handoff` `{repository, base_sha,
     base_branch, diff_patch, message, metadata}` → cria conversa
     cloud: provisiona sandbox, clone base_sha, `git apply` do patch
     (3-way `git apply --3way`; conflito → `--reject` + reporta hunks
     falhados no evento `setup`), injeta a mensagem como prompt
     inicial + evento `handoff_applied` com stats.
  3. Output: URL/ID da conversa; `--open` abre browser; `--json` p/
     scripting.
- `--message <text>` para o prompt; `--include-untracked` opt-in
  (default: tracked only); `--dry-run` mostra o que seria enviado
  (diff stats + arquivos) sem chamar a API.

### RF-002 — `sandcastle pickup <conversation>` (cloud → local)

- `GET /api/v1/conversations/{id}/handoff-package` → `{repo,
  head_branch, diff_patch, summary}` — o servidor produz o diff do
  sandbox (working tree vs base).
- CLI: confirma cwd limpo ou `--force` (stash automático); checkout
  da branch (`handoff/{conv-short-id}` ou branch do PR se existe);
  `git apply` com `--3way`; conflito → `--reject` + lista hunks +
  exit code 2; imprime resumo do agente.
- `pickup --pr <n>`: atalho que resolve a conversa dona do PR.

### RF-003 — Segurança + UX

- Patch é conteúdo untrusted tanto na ida quanto na volta: servidor
  sanitiza (SecretRedactor) o diff antes de persistir; CLI mostra
  `diff --stat` + pede confirmação antes de apply (exceto `--yes`).
- `handoff-package` exige `perm:conversations` + owner ou org role;
  diff expira (`Handoff:PackageTtlMin=60`, regenerável).
- `--message` e summary passam pelo envelope SPEC-91 no prompt.

### RF-004 — Fora de escopo

- `sandcastle ssh` direto no sandbox (Devin tem; expõe SSH — exige
  design de chaves, fora de v1) · handoff de conversa cloud→cloud ·
  sync contínuo bidirecional · handoff de sessão TUI do agente local
  (não existe agente local CLI ainda — o handoff aqui é de working
  tree, não de processo agente rodando).

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| POST | `/api/v1/conversations/handoff` | cria conv+apply → task id |
| GET | `/api/v1/conversations/{id}/handoff-package` | `{repo, branch, diff_patch, summary, generated_at}` |

Erros: `400` patch inválido/oversize · `409` conversa sem sandbox vivo
para gerar package (usar `?regenerate=1` re-provisiona) · `403` sem
acesso · `422` apply falhou parcialmente (retorna hunks).

## 4. Data model

- `conversations` + `HandoffMetaJson` (origin: local|cloud, base_sha,
  applied_stats, created_cli_version) — migration dupla.
- `handoff_packages` ou geração on-demand: preferir **on-demand**
  (diff do sandbox vivo) com cache TTL — sem tabela nova se o sandbox
  estiver vivo; persistir só quando sandbox morto (snapshot do
  último diff — `HandoffPackageJson` no record, invalidado a cada
  novo commit).

## 5. NFR / Security

- Auth: `sk-*` do usuário (CLI já autentica); package scope caller.
- Diff nunca excede `MaxDiffKb`; arquivos binários → `binary files
  differ` placeholder + aviso (não transmite blob — listado no
  metadata para o usuário decidir).
- `git apply` server-side roda dentro do sandbox — mesma boundary
  dos outros execs.
- CLI nunca envia arquivos fora do repo git; `--include-untracked`
  respeita `.gitignore` + blocklist de secrets (`.env*`, `*.pem`,
  `id_rsa*` — warning explícito, opt-in `--include-secrets` nunca).

## 6. Task Plan

1. Endpoint handoff create + apply no provisionamento + evento.
2. CLI `handoff` (collect/diff/dry-run/post/open).
3. Endpoint handoff-package + CLI `pickup` (apply/conflict/force).
4. Tests: unit (diff build, gitignore, binary placeholder,
   conflict path, ttl), integração (handoff→conv com patch aplicado;
   pickup→apply local fixture), CLI smoke (mock http).
5. Golden OpenAPI + i18n (CLI strings PT/EN — seguir o padrão do CLI).

## 7. Deliverables

`handoff`/`pickup` movem trabalho entre máquina e cloud com diff
3-way e confirmação — a ponte local↔nuvem que o Devin vende.

## 8. Referências

- `docs/research/2026-11-competitive-gap-analysis.md` §2.3 (handoff,
  pickup, ssh).
- `docs/specs/04_rest_and_signalr_api_spec.md`,
  `12_git_scm_integration_spec.md`, `145_repo_wiki_mcp_spec.md`
  (clone credentials), `124_mcp_acp_exposure_spec.md`.

## 9. Guardrails

- `pickup` nunca sobrescreve trabalho local sem `--force`/confirmação.
- Secrets patterns nunca vão no patch (blocklist dura, sem opt-out).
- Handoff cria conversa normal — todas as policies de start-task se
  aplicam (quotas, perms, budget).

## 10. DoD

- `handoff` de repo fixture com diff → conversa criada, patch
  aplicado (verificado via evento/artifacts), conflito reporta hunks.
- `pickup` aplica package em working tree limpo; dirty→recusa sem
  `--force`; conflito→reject+exit 2.
- Secrets blocklist testada; TTL do package respeitado.
- Suite verde; migration dupla; golden; i18n parity.
