# SPEC — 66 · Quality Gates: lint enforcement em CI + integração SonarQube

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `quality-gates-automation` |
| Type | `Automation / CI` |
| Stack | `GitHub Actions · dotnet format · SonarQube/SonarCloud` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
| Ticket | `GAP-automation-sonarqube-gate` · `GAP-automation-lint-ci` (gap-analysis 2026-10-19) |
| Depends on | credenciais SonarQube (`SONAR_HOST_URL`/`SONAR_TOKEN` + project key) — fornecidas pelo usuário |
| Touches | `.github/workflows/ci.yml` · `.editorconfig` (novo) · `sonar-project.properties` ou step scanner |

## 1. User Story

**As a** maintainer do OpenHandsNET
**I want** os quality gates documentados executando de fato em CI
**So that** code smells e débito técnico sejam detectados continuamente,
viabilizando o gate `/sonarqube-autofix` do orchestrator (Phase 2).

## 2. Evidência (AS-IS × TO-BE)

| Item | TO-BE (fonte) | AS-IS (evidência) |
|---|---|---|
| Lint em CI | `.claude/memory/orchestrator_stats.md`: `lint_command: dotnet format --verify-no-changes` | `.github/workflows/ci.yml` só executa `restore`/`build`/`test`/`test Golden` — **nenhum step de lint**; `.editorconfig`/`.globalconfig` ausentes |
| Static analysis/SonarQube | orchestrator Phase 2: "`/sonarqube-autofix` for P1/P2 static analysis, security vulnerabilities, code smells" | Zero integração: sem `sonar-project.*`, sem step em `ci.yml`/`release.yml`/`k8s-e2e.yaml`, sem issues de code smell; `sonar-scanner` existe no PATH da VM (`~/.local/share/sonar-scanner/`) mas sem `SONAR_HOST_URL`/`SONAR_TOKEN`/project key configurados |

## 3. Escopo

1. Criar `.editorconfig` baseline (convenções C# do repo: file-scoped
   namespaces, `Async` suffix, etc.) — gerado por `dotnet format` style
   defaults + ajustes mínimos.
2. Adicionar step `dotnet format OpenHandsNET.slnx --verify-no-changes`
   em `ci.yml` (após build).
3. Integrar SonarQube:
   - Opção A (SonarCloud): `sonarcloud-github-action` ou
     `dotnet-sonarscanner` em `ci.yml` com secrets do repo.
   - Opção B (self-hosted): `sonar-project.properties` + step scanner.
   - Escolha registrada na SPEC após decisão do usuário.
4. Rodar primeira análise e triar issues via `/sonarqube-autofix`
   (code smells → SPECs/issues conforme o contrato da skill).

## 4. Fora de escopo

- Corrigir os code smells encontrados (geram SPECs próprias via
  `sonarqube-autofix`).
- Cobertura mínima de coverage (target ainda `n/a` — pode ser decidido
  na triagem inicial).

## 5. Acceptance Criteria

- [ ] PR que falha `dotnet format --verify-no-changes` falha o CI.
- [ ] Análise SonarQube executa em todo PR/push para `main` e publica
      resultado no projeto configurado.
- [ ] Primeira triagem `/sonarqube-autofix` executada; issues criadas
      para code smells P1/P2 (ou report de "0 issues").
- [ ] Secrets apenas em GitHub Secrets — nenhum token commitado.

## 6. Deliverables

- `.editorconfig`, `ci.yml` atualizado, `sonar-project.properties` (se B).
- Report da primeira análise SonarQube.

## 7. Task Plan

| # | Task | Files |
|---|---|---|
| 1 | `.editorconfig` baseline + format fix inicial | `.editorconfig`, `src/**` |
| 2 | Step lint no CI | `.github/workflows/ci.yml` |
| 3 | Integração Sonar (secrets + step) | `ci.yml`, `sonar-project.properties` |
| 4 | Primeira análise + triagem `/sonarqube-autofix` | issues/SPECs derivadas |

## 8. Referências

- `.github/workflows/ci.yml` · `~/.claude/skills/orchestrator` Phase 2
- `~/.claude/skills/sonarqube-autofix` · `~/.claude/skills/sonarqube-review`

## 9. Guardrails

- **Nunca** commitar `SONAR_TOKEN`; secrets via GitHub Secrets.
- `.github/workflows/` exige revisão humana (hard rule 6) — PR dedicado.
- NuGet ≥7-day rule para `dotnet-sonarscanner`.

## 10. DoD

- [ ] CI verde com os novos gates.
- [ ] Evidência da primeira análise anexada à issue.
