# SPEC — 138 · Rename OpenHandsNET → Sandcastle / Sandcastle Cloud

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `sandcastle-rename` |
| Type | `Refactor (cross-cutting) / Infra / Release` |
| Stack | `.NET 10` |
| Repository | `afonsoft/OpenHandsNET` → `afonsoft/sandcastle` |
| Status | `Completed` |
| Ticket | #395 |
| Scope | `src/` + `cli/` + `tests/` + `.specs/` + `docs/` + `.github/` + `docker/` + `runtime/` + `tools/` + raiz |

## 1. User Story

**As a** mantenedor preparando o anúncio público do beta, **I want**
renomear o produto de OpenHandsNET para **Sandcastle** (core/OSS) e a
edição cloud/enterprise para **Sandcastle Cloud**, **so that** a marca
reflita a identidade própria do fork/fusão, evite confusão com o
ecossistema upstream OpenHands e a release `v0.1.0-beta.2` já nasça com
o nome definitivo.

## 2. Contexto / escopo

SPEC-137 recomendou decidir o rename **antes do anúncio público**. Nome
aprovado: **Sandcastle** (produto/core) + **Sandcastle Cloud** (edição).
Decisões travadas:

- **Repo**: `afonsoft/OpenHandsNET` → `afonsoft/sandcastle` (somente
  rename; transferência para org `sandcastle-cloud` adiada para o
  anúncio — GitHub preserva redirects).
- **Root namespace**: `Sandcastle.*` — "Cloud" é marca/edição, não
  entra no código.
- **Env vars**: hard break `OPENHANDSNET_*` → `SANDCASTLE_*`, com nota
  de migração no CHANGELOG (sem dual-read — beta é a janela de quebrar).
- **Imagens**: app `openhandsnet` → `sandcastle`; runtime opt-in
  `openhandsnet/runtime` → `sandcastle-runtime`; GHCR segue o owner
  (`ghcr.io/afonsoft/sandcastle`).
- **Dados/estado local**: corte limpo — `openhandsnet.db` →
  `sandcastle.db`, diretórios/volumes/containers `openhandsnet*` →
  `sandcastle*`; nota "dados beta não migram" no CHANGELOG (sem script
  de migração — contrato beta).
- **SonarCloud**: manter project key `afonsoft_OpenHandsNET` — o rename
  do repo propaga o display name automaticamente e a key é imutável;
  criar key nova só quebraria o scanner sem admin da org. Reavaliar
  pós-anúncio se a key virar problema.
- **Histórico congelado**: `docs/specs/*` (arquivadas), seções antigas
  do `CHANGELOG.md` e `.claude/memory/` **não** são reescritos —
  permanecem com "OpenHandsNET" como registro histórico.
- **Timing**: `v0.1.0-beta.2` carrega o rename; `v0.1.0-beta.1` fica
  como última release OpenHandsNET.
- **Brand assets**: textos da UI/PWA/i18n mudam agora; ícone/logo
  "castelo de areia" fica para SPEC própria de design.
- **`.openhands/` convention**: diretórios de hooks/skills/microagents
  no workspace do usuário seguem a convenção upstream — **preservados**
  (compat de ecossistema, como `.claude/`/`.agents/`).
- **MCP tools**: prefixo `sc_*` → `sc_*` (breaking para clientes MCP;
  documentado no CHANGELOG).
- **Casing variants**: tanto `OpenHandsNET` (namespaces) quanto
  `OpenHandsNet` (classes como `OpenHandsNetOptions`) → `Sandcastle`.
- **Nunca renomear `OpenHands` puro**: referências ao upstream
  (All-Hands-AI/OpenHands, OpenHands-Cloud, comparações) permanecem —
  só casam `OpenHandsNET`/`OpenHandsNet`/`openhandsnet`/`OPENHANDSNET`.

### Blast radius (evidência, `main` @ 75be698)

- ~756 arquivos `.cs`, ~60 `.razor`, 15 `.csproj` + `OpenHandsNET.slnx`
- ~226 `.md`, ~219 `.json` (appsettings, i18n, golden OpenAPI, lock
  files), 26 workflows, 9 Dockerfile/compose
- 119 referências ao prefixo `OPENHANDSNET_`
- 13 tools MCP `sc_*` + baseline `tools/drift-baseline/mcp-tools.json`
- `cli/OpenHandsNET.Cli` — `ToolCommandName` `openhandsnet`
- `data/openhandsnet.db` em `appsettings.json`; `manifest.webmanifest`
  + `index.html` com nome/título do produto

## 3. Requisitos

- RF-001 — **Código/solução**: dirs `src/OpenHandsNET.*` →
  `src/Sandcastle.*` (idem `tests/`, `cli/`), `.csproj` renomeados,
  `OpenHandsNET.slnx` → `Sandcastle.slnx`, namespaces/usings/classes
  `OpenHandsNET*` e `OpenHandsNet*` → `Sandcastle*`, `ToolCommandName`
  → `sandcastle`, referência `OpenHandsNET.Client.styles.css` segue o
  assembly.
- RF-002 — **Config/env**: `OPENHANDSNET_` → `SANDCASTLE_` em
  appsettings/options binding/`.env.exemplo`/compose/docs/testes;
  `openhandsnet.db` → `sandcastle.db`; hard break documentado.
- RF-003 — **Docker**: nomes de imagem (`sandcastle`, `sandcastle-
  runtime`), labels Dockerfiles, services/containers/volumes
  `openhandsnet*` → `sandcastle*` em `docker-compose*.yml` e docs.
- RF-004 — **UI/i18n**: `manifest.webmanifest` (`name`/`short_name`),
  `index.html` (`<title>`, meta description), strings i18n com nome de
  produto → "Sandcastle"; "Sandcastle Cloud" só onde fala da edição.
- RF-005 — **MCP**: tools `sc_*` → `sc_*` em `src/OpenHandsNET.Mcp` +
  baseline `tools/drift-baseline/mcp-tools.json` regen.
- RF-006 — **CI/CD**: `release.yml` `IMAGE` → `ghcr.io/afonsoft/
  sandcastle`; referências de nome de repo em workflows/badges;
  SonarCloud key mantida (ver decisão) + nota de revisão.
- RF-007 — **Docs live**: `README.md` (título, badges, quickstart),
  `docs/installation/*`, `docs/release-beta.md`, `docs/operations/*`,
  `CLAUDE.md`, `AGENTS.md`, `.specs/README.md`, `migration-plan/` —
  refs de produto atualizadas; refs upstream `OpenHands` intactas.
- RF-008 — **Goldens/baselines**: `openapi-v1.json` (info.title),
  `packages.lock.json` (project paths), baselines
  `tools/drift-baseline/*` com paths `OpenHandsNET.*` — regenerados.
- RF-009 — **CHANGELOG**: entrada `v0.1.0-beta.2` com seção breaking
  (env vars, imagens, DB, MCP tools, repo URL); histórico anterior
  intocado.
- RF-010 — **GitHub ops**: `gh repo rename sandcastle`, description/
  topics atualizados, issue #395 fechada, nota em `docs/specs/README.md`
  sobre o rename para leitores de SPECs arquivadas.

## 4. API Contract

Rotas `/api/v1/**` inalteradas (paths, shapes, discriminadores).
Mudanças de contrato **externo** (breaking, documentadas):

- Env vars `OPENHANDSNET_*` deixam de ser lidas (hard break).
- MCP `tools/list`: `sc_*` → `sc_*` (clientes MCP atualizam config).
- Nomes de imagem/volume/DB file (deployment contract).
- `info.title` do OpenAPI document passa a "Sandcastle" — golden regen.
- URLs do repo: `github.com/afonsoft/OpenHandsNET` redireciona para
  `afonsoft/sandcastle` (git remote atualizado na doc).

## 5. Critérios de aceite

- [ ] `git grep -nE 'OpenHandsNET|OpenHandsNet|openhandsnet|OPENHANDSNET'
  -- . ':(exclude)docs/specs/**' ':(exclude)CHANGELOG.md'
  ':(exclude).claude/memory/**'` → zero ocorrências (whitelist: notas
  de rename explícitas que mencionem o nome antigo propositalmente,
  ex.: seção breaking do CHANGELOG/README "formerly OpenHandsNET").
- [ ] `git grep -n 'OpenHands'` restante → apenas refs upstream ou
  notas de rename (revisão manual do diff).
- [ ] `dotnet format Sandcastle.slnx --verify-no-changes` limpo;
  `dotnet restore --locked-mode` OK; `dotnet build -c Release` 0
  warnings; `dotnet test` verde.
- [ ] Drift guards verdes: `check-css-classes`, `check-form-a11y`,
  `check-i18n-parity`, `check-i18n-placeholders`, `check-mcp-tools-
  golden` (baseline regen com `sc_*`), `check-spec-status`/lifecycle.
- [ ] Golden `openapi-v1.json` regenerado (title "Sandcastle").
- [ ] `docker compose up -d --build` em env limpa sobe com
  `SANDCASTLE_*` vars e imagem `sandcastle*`; runtime opt-in usa
  `sandcastle-runtime`.
- [ ] CLI `dotnet tool` → comando `sandcastle` funciona.
- [ ] CHANGELOG `v0.1.0-beta.2` com breaking notes; histórico
  `docs/specs/` e `.claude/memory/` intocados.
- [ ] Repo renomeado no GitHub; CI verde pós-merge no repo novo.

## 6. Task Plan

1. SPEC aprovada + issue #395 (esta SPEC).
2. Passo mecânico 1 — filesystem: `git mv` dirs/projetos/slnx →
   `Sandcastle.*`.
3. Passo mecânico 2 — conteúdo: sed ordenado
   `OpenHandsNET`→`Sandcastle`, `OpenHandsNet`→`Sandcastle`,
   `openhandsnet`→`sandcastle`, `OPENHANDSNET`→`SANDCASTLE`, `sc_`→
   `sc_` (escopo MCP/baseline) em arquivos tracked, excluindo
   `docs/specs/`, `CHANGELOG.md`, `.claude/memory/`.
4. Fixes manuais: DB name, imagens/compose, manifest/title, env docs,
   `.env.exemplo`, workflows (IMAGE, refs repo), MCP prefix,
   CHANGELOG beta.2, README/CLAUDE.md.
5. Regen: lock files, golden OpenAPI, mcp-tools baseline, demais
   drift baselines que referenciem paths renomeados.
6. Verificação completa (critérios acima) + clean-env compose.
7. PR único → CI → merge; GitHub ops (repo rename, issue close);
   tag `v0.1.0-beta.2` via checklist de release existente.

## 7. Deliverables

- `Sandcastle.slnx` + projetos `Sandcastle.*` (src/tests/cli).
- Config/docs/compose/workflows renomeados.
- Goldens/baselines regenerados; CHANGELOG `v0.1.0-beta.2`.
- Repo `afonsoft/sandcastle` + nota de rename nos índices de specs.

## 8. Referências

- `cli/OpenHandsNET.Cli/OpenHandsNET.Cli.csproj` — `ToolCommandName`.
- `src/OpenHandsNET.Api/appsettings.json:14` — `data/openhandsnet.db`.
- `src/OpenHandsNET.Client/wwwroot/manifest.webmanifest` + `index.html`
  — nome/título do produto.
- `src/OpenHandsNET.Mcp/McpServer.cs` — 13 tools `sc_*`.
- `src/OpenHandsNET.Domain/Sandbox/SandboxSpec.cs:22` —
  `DefaultImage` upstream default (`sandcastle-runtime` é opt-in doc).
- `.github/workflows/release.yml` — `IMAGE: ghcr.io/afonsoft/openhandsnet`.
- `.github/workflows/ci.yml` — `afonsoft_OpenHandsNET` (mantida).
- `docs/release-beta.md` — checklist para tag `v0.1.0-beta.2`.

## 9. Guardrails

- **Nunca** renomear `OpenHands` puro (upstream) — revisar cada hunk
  de diff que toque `OpenHands` sem sufixo.
- `.openhands/` convention preservada (hooks/skills upstream-compat).
- Sem rewrite de git history; `git mv` preserva blame.
- `.claude/memory/`, `docs/specs/`, CHANGELOG histórico: intocados.
- Workflows alterados com permissão já concedida pelo usuário.
- PR único atômico: rename é all-or-nothing para o build passar.
- Secrets/vars novas nunca comitadas; `.env.exemplo` sem valores reais.

## 10. DoD

Repo e codebase respondem por **Sandcastle** end-to-end (build, testes,
compose, CLI, MCP, docs live, CI); único vestígio de "OpenHandsNET" é
histórico congelado ou nota explícita de rename; `v0.1.0-beta.2` pronto
para tag; issue #395 fechada.
