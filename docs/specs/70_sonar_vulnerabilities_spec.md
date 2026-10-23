# SPEC-70: Sonar Vulnerabilities (SonarCloud)

| Campo | Valor |
|---|---|
| Status | Completed |
| Issue | #157 |
| Type | Security |
| Sonar type | VULNERABILITY |
| Issues | 52 issues · 16 regras · esforço Sonar ~0min |
| Fonte | SonarCloud `afonsoft_OpenHandsNET` (branch main, 2026-10-19) |

## User Story

Como mantenedor do OpenHandsNET, quero eliminar as 52 issues SonarCloud do tipo `VULNERABILITY` (16 regras distintas) para reduzir risco e débito técnico, com cada correção coberta por testes e sem alteração de comportamento funcional.

## Escopo

Agrupamento por regra SonarQube (mesmo tipo) — a lista canônica issue→arquivo→linha está em `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` e `sonarqube_issues.json`.

| Regra | Qtd | Severidade predominante |
|---|---|---|
| `csharpsquid:S6444` | 13 | MINOR |
| `csharpsquid:S5332` | 8 | MINOR |
| `csharpsquid:S4036` | 6 | MINOR |
| `roslyn.sonaranalyzer.security.cs:S7044` | 4 | MAJOR |
| `shell:S6506` | 4 | MAJOR |
| `csharpsquid:S5443` | 3 | CRITICAL |
| `docker:S6506` | 3 | MAJOR |
| `githubactions:S7637` | 2 | MAJOR |
| `githubactions:S8233` | 2 | MAJOR |
| `docker:S8482` | 1 | BLOCKER |
| `csharpsquid:S4790` | 1 | CRITICAL |
| `csharpsquid:S5542` | 1 | CRITICAL |
| `csharpsquid:S5693` | 1 | MAJOR |
| `kubernetes:S6865` | 1 | MAJOR |
| `yaml:S2068` | 1 | MAJOR |
| `kubernetes:S6868` | 1 | MINOR |

## Contexto técnico

- Stack: .NET 10 / C# 14 (+ workflows YAML, shell, docker, k8s manifests).
- Leitura: arquivos listados no ToDo Board (coluna File/Line).
- Testes: `tests/` — xUnit+Shouldly; novas linhas exigem cobertura (Iron Law).
- Lint gate: `dotnet format --verify-no-changes` deve permanecer verde.

## Requisitos

1. Corrigir todas as 13 ocorrência(s) de `csharpsquid:S6444` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
2. Corrigir todas as 8 ocorrência(s) de `csharpsquid:S5332` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
3. Corrigir todas as 6 ocorrência(s) de `csharpsquid:S4036` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
4. Corrigir todas as 4 ocorrência(s) de `roslyn.sonaranalyzer.security.cs:S7044` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
5. Corrigir todas as 4 ocorrência(s) de `shell:S6506` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
6. Corrigir todas as 3 ocorrência(s) de `csharpsquid:S5443` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
7. Corrigir todas as 3 ocorrência(s) de `docker:S6506` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
8. Corrigir todas as 2 ocorrência(s) de `githubactions:S7637` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
9. Corrigir todas as 2 ocorrência(s) de `githubactions:S8233` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
10. Corrigir todas as 1 ocorrência(s) de `docker:S8482` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
11. Corrigir todas as 1 ocorrência(s) de `csharpsquid:S4790` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
12. Corrigir todas as 1 ocorrência(s) de `csharpsquid:S5542` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
13. Corrigir todas as 1 ocorrência(s) de `csharpsquid:S5693` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
14. Corrigir todas as 1 ocorrência(s) de `kubernetes:S6865` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
15. Corrigir todas as 1 ocorrência(s) de `yaml:S2068` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
16. Corrigir todas as 1 ocorrência(s) de `kubernetes:S6868` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).

## Critérios de aceite

- [ ] `api/issues/search` não retorna mais nenhuma das 52 issues desta SPEC para `afonsoft_OpenHandsNET`.
- [ ] `dotnet build -c Release` 0 warnings/errors; `dotnet test` 100% verde.
- [ ] Cobertura das linhas modificadas (sem exclusions).
- [ ] Nenhuma regra de negócio alterada sem justificativa documentada.

## Task Plan

1. Filtrar issues desta SPEC no ToDo Board; corrigir por regra (uma regra por commit lógico).
2. Adicionar/ajustar testes para cada linha tocada.
3. `dotnet format` + build + test suite.
4. Atualizar checkboxes do ToDo Board; re-scan SonarCloud via CI após merge.
