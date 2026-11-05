# SPEC — 131 · Sandbox browser: Playwright + chrome-devtools MCP

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `sandbox-browser-playwright` |
| Type | `Infra` |
| Status | `Approved` |
| Ticket | #338 |
| Scope | `runtime/Dockerfile`, `src/OpenHandsNET.Infrastructure/Mcp/`, `docs/` |

## 1. User Story

**As a** usuário, **I want** Playwright e o MCP `chrome-devtools`
funcionando dentro do sandbox, **so that** o agente consiga navegar,
testar e depurar apps web no ambiente isolado — paridade com o runtime
do OpenHands Cloud e as VMs do Devin Cloud.

## 2. Contexto / escopo

Estado atual (evidência auditada):

- `PlaywrightBrowserService` + tool `browser` rodam **worker-side**
  (SPEC-18) — cobrem goto/click/type/screenshot no processo da API, não
  dentro do container. Não é regressão; permanece.
- `runtime/Dockerfile` tem Node 22 + python3 mas **zero browser**:
  nenhum Chromium, nenhum `ms-playwright`, nenhuma dep de X.
- `McpClientService.ResolveStdioAsync` já roda stdio via
  `docker exec -i <container> <cmd>` — porém **descarta `cfg.Env`**
  (env só alimenta `EnvironmentVariables` do processo docker host-side,
  nunca chega dentro do container).
- `McpSettingsService.EffectiveForAsync` retorna só servidores
  configurados (DB/overrides) — sem camada builtin.
- OWUIC: `browser_*` tools + `BrowserSessionService` — também
  worker-side (não tem sandbox). OpenHands Cloud: runtime image
  shipa playwright + chromium, browser roda dentro do runtime.
  Devin Cloud: VM shipa Chrome e `chrome-devtools-mcp` no catálogo.

## 3. Requisitos

| ID | Requisito |
|---|---|
| RF-001 | `runtime/Dockerfile`: deps de Chromium (apt via `playwright install-deps` ou lista explícita), `PLAYWRIGHT_BROWSERS_PATH=/opt/ohn/ms-playwright`, `npm i -g playwright@<pin> chrome-devtools-mcp@<pin>`, `playwright install chromium` no build, `CHROME_PATH`/`PUPPETEER_EXECUTABLE_PATH` env defaults. Tudo pinned; sem secrets. |
| RF-002 | `ResolveStdioAsync`: `cfg.Env` vira `docker exec -e K=V` antes do container name (host path mantém `EnvironmentVariables`). |
| RF-003 | Servidor builtin `chrome-devtools` no merge de `EffectiveForAsync` — `npx chrome-devtools-mcp@<pin> --headless=true --isolated` — default `Enabled`, usuário/override de mesmo nome vence; desligável. |
| RF-004 | Docs: `docs/mcp-server.md` (ou `docs/browser.md`) descreve browser no sandbox, preset, envs, comando `npx playwright`. |
| RF-005 | Testes: env pass-through em `docker exec -e`; merge builtin (override mesmo nome vence, disable honrado); validação estática do Dockerfile (binários presentes no build script via teste textual). |

## 4. Acceptance

- [ ] `docker exec <sandbox> npx playwright --version` funciona e
      `node -e "require('playwright')"` (ou `npx playwright screenshot`)
      lança Chromium sem download.
- [ ] Servidor MCP `chrome-devtools` aparece em `tools/list` ao conectar
      (registrado nas settings efetivas, enabled por default).
- [ ] `cfg.Env` chega dentro do container (`docker exec -e`).
- [ ] Unit tests verdes; integration suite inalterada.

## 5. API Contract

Sem mudança de API REST. Novo env `Mcp__*` nenhum; config existente.

## 6. Task Plan

1. Dockerfile: apt deps + `npm i -g` pins + `playwright install --with-deps chromium` + ENVs.
2. `ResolveStdioAsync`: `-e K=V` em docker exec; teste.
3. `McpSettingsService`: `BUILTIN_MCP_SERVERS` merge (override por nome vence).
4. Docs + testes unitários.

## 7. Riscos / notas

- Tamanho da imagem +~450MB (Chromium + deps) — inevitável, mesmo custo
  que upstream runtime.
- chrome-devtools-mcp com `--isolated` não persiste perfil entre calls;
  sem perfil persistente por conversa (follow-up possível).
- `browser` builtin permanece worker-side (SPEC-18) — divergência
  intencional documentada; mover para sandbox = SPEC futura.
