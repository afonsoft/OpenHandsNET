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

### Changed

- **SPECs (SPEC-65, #147)**: 66 SPECs entregues arquivadas em
  `docs/specs/` (`Status: Completed` + evidência de entrega no índice);
  `.specs/` passa a conter apenas SPECs ativas; colisões de numeração
  resolvidas (`52_nextgen`→68, `53_enterprise_sandbox`→69 com refs de
  código `SPEC-53`→`SPEC-69`); `CLAUDE.md` ganha seções de harness,
  context engineering e memory protocol.
- Formatação: `dotnet format` aplicado em toda a solução (baseline).
