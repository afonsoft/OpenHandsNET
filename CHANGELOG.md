# Changelog

Formato baseado em [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/),
versionamento [SemVer](https://semver.org/lang/pt-BR/).

## [Unreleased]

## [v0.1.0-beta.2] — 2026-11-09

**Rename do produto**: OpenHandsNET → **Sandcastle** (core/OSS) +
**Sandcastle Cloud** (edição enterprise/hosted). SPEC-138 · issue #395.
`v0.1.0-beta.1` permanece como a última release com a marca OpenHandsNET.

### Breaking changes

- **Repositório**: `github.com/afonsoft/OpenHandsNET` →
  `github.com/afonsoft/sandcastle` (redirects preservados; atualizar
  `git remote set-url origin`).
- **Env vars**: prefixo `OPENHANDSNET_*` → `SANDCASTLE_*` e `OHN_*` →
  `SC_*` (hard break, sem dual-read — variáveis antigas são ignoradas).
- **Imagens Docker**: app `openhandsnet` → `sandcastle`
  (`ghcr.io/afonsoft/sandcastle`); runtime opt-in `openhandsnet/runtime`
  → `sandcastle-runtime`.
- **Dados locais**: `openhandsnet.db` → `sandcastle.db`; volumes,
  containers, pods (`ohn-pool-*` → `sc-pool-*`) e prefixos de cache/
  localStorage (`ohn*` → `sc*`) novos — **dados da beta não migram**.
- **MCP**: tools `ohn_*` → `sc_*` e env `OHN_API_URL`/`OHN_API_KEY` →
  `SC_API_URL`/`SC_API_KEY`; binário `openhandsnet-mcp` →
  `sandcastle-mcp`.
- **CLI**: `openhandsnet` → `sandcastle`
  (`dotnet tool install Sandcastle.Cli`).
- **Namespaces/assemblies**: `OpenHandsNET.*`/`OpenHandsNet*` →
  `Sandcastle.*`; solução `Sandcastle.slnx`.

### Notes

- Convenção `.openhands/` (hooks/skills/microagents) **preservada** —
  é convenção de ecossistema upstream, não marca do produto.
- SonarCloud mantém project key `afonsoft_OpenHandsNET` (imutável);
  display name acompanha o repo.
- Histórico congelado: `docs/specs/` (SPECs arquivadas), entradas
  anteriores deste changelog e `.claude/memory/` seguem com o nome antigo.
- Ícone/logo da marca nova fica para SPEC própria de brand assets.

## [v0.1.0-beta.1] — 2026-11-08

Primeira tag beta pública (Epic #365 · SPECs 132–137). Highlights do
audit: ~130 SPECs entregues e arquivadas em `docs/specs/`, design-system
inspirado no Devin Web sobre a base `afonsoft/open-webui`, 45+ builtin
tools + MCP/ACP, e enterprise ops completo (orgs/SCIM/OIDC/quotas/audit).

### Added

- **Auth UX (SPEC-132, #366)**: página `/auth` (3 providers), avatar e
  user-menu na sidebar, sign-out via `POST /auth/logout`, guard de 401,
  `GET /api/v1/auth/session` (provider + user para a shell).
- **Web search tool (SPEC-133, #367)**: builtin `web_search`
  (duckduckgo/tavily/brave) com provider configurável.
- **BYOK (SPEC-134, #368)**: chaves LLM por usuário (openai/anthropic/
  gemini/openai_compat) com storage criptografado (DataProtection+nonce),
  CRUD `/api/v1/keys/llm*` mascarado, test ping rate-limited e
  precedência user → BYOR → global no AgentRunner (`key_source` no ledger).
- **Conversation UX (SPEC-135, #369)**: `POST /app-conversations/{id}/fork`
  (clona eventos até `at_event_id`, `parent_conversation_id`), turn undo
  `POST .../undo` (soft-delete `truncated_at` + revert ao checkpoint do
  início do turno) e `POST .../files/revert-hunks` (reverse-apply por
  hunk, `applied[]`/`conflicts[]`); UI: sidebar + topbar overflow,
  `/undo` no composer, badge de proveniência, DiffTab por hunk.
- **Nav visual polish (SPEC-136, #370)**: nav órfã, fontes, ícones.
- **Release housekeeping (SPEC-137, #371)**: este checklist +
  `docs/release-beta.md`, dead code, docs de instalação.
- **Quality gates (SPEC-66, #148)**: `.editorconfig` baseline, step
  `dotnet format --verify-no-changes` no CI, job `sonarcloud` (SonarCloud
  `afonsoft_OpenHandsNET`, gated por `vars.SONAR_ENABLED` + `SONAR_TOKEN`),
  badges CI/SonarCloud nos READMEs.
- **Harness (SPEC-67, #149)**: estrutura `.claude/` completa —
  `settings.json`, `rules/` (global + dotnet + workflows), `agents/`
  (engineer/plan/review/test/architecture), `CONTEXT.md`, `RULES.md`,
  `MEMORY.md`, `TOOLS.md`, `WORKFLOWS.md`, `README.md`, `memory/` —
  e `docs/architecture/` (ADR-0001 + overview Mermaid).
- **Desktop ao vivo do sandbox (SPEC-113, #284)**: surface `desktop`
  lazy-install (Xvfb + openbox + x11vnc + noVNC), tab Desktop na conversa
  com iframe noVNC interativo, e proxy same-origin
  `GET /api/v1/sandboxes/{id}/surfaces/{port}/{**rest}` (HTTP+WS, sessão
  ou `?token=` preview) — paridade com o live desktop do Devin Web.
- **Docs de instalação (#282)**: `docs/installation/` (index, `docker.md`,
  `host.md` com systemd+nginx, `environment-variables.md`) + mirror
  `docs/en/installation/` e seção de comparação Devin Web/Cursor Cloud
  em `docs/comparison.md`; screenshots reais em `docs/images/`.

### Changed

- **SPECs (SPEC-65, #147)**: 66 SPECs entregues arquivadas em
  `docs/specs/` (`Status: Completed` + evidência de entrega no índice);
  `.specs/` passa a conter apenas SPECs ativas; colisões de numeração
  resolvidas (`52_nextgen`→68, `53_enterprise_sandbox`→69 com refs de
  código `SPEC-53`→`SPEC-69`); `CLAUDE.md` ganha seções de harness,
  context engineering e memory protocol.
- Formatação: `dotnet format` aplicado em toda a solução (baseline).

### Fixed

- **Sandbox `NanoCpus` (#282)**: `DockerSandboxEngine` clampa o CPU
  configurado ao `NCPU` do daemon — container deixava de subir em hosts
  com menos cores que `Sandbox:Cpus`.
- **Boot WASM em Docker (#282)**: `index.html` usava marcador
  `blazor.webassembly#[.{fingerprint}].js` literal — substituído pelo
  alias resolvido pelo `MapStaticAssets`; app renderiza em compose.
- **OpenAPI schema inválido (#218, #288)**: `pattern` regex emitido em
  parâmetros de rota `{x:int}` (schema `type: integer`) quebrava o
  schemathesis nightly — removido no DocumentTransformer com teste de
  contrato.
- **WebSocket middleware (SPEC-113)**: `app.UseWebSockets()` faltava —
  proxy WS de surfaces (vscode/terminal/desktop) não funcionava.
- **Sonar smells (#287)**: lambda de 8 parâmetros (S107) e delimiter
  hardcoded (S1075) no endpoint de surfaces.


### Breaking changes

- **`GET /api/v1/pending-messages` removido** (SPEC-137): pending
  messages são in-memory por réplica e o endpoint foi dropado —
  consumidores devem usar o stream de eventos da conversa.

### Known limitations

- Pending messages são in-memory por réplica (fila durável = roadmap).
- `Auth:Provider=none` é single-user local — **não exponha à internet
  sem `oidc`** (callouts em `.env.exemplo` + docs de instalação;
  `docker-compose.beta.yaml` liga rate limit 120/min e IP rules).
- Multi-repo workspace não existe; resume exige sandbox `RUNNING`
  (sem wake-in-fresh-sandbox); billing = metering apenas (sem PSP);
  k8s multi-host parcial (SPEC-49).

