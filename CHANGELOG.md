# Changelog

Formato baseado em [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/),
versionamento [SemVer](https://semver.org/lang/pt-BR/).

## [Unreleased]

### Added

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
