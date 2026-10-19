# SPEC-71: Sonar Bugs (SonarCloud)

| Campo | Valor |
|---|---|
| Status | Approved |
| Issue | #153 |
| Type | Bugfix |
| Sonar type | BUG |
| Issues | 32 issues · 7 regras · esforço Sonar ~0min |
| Fonte | SonarCloud `afonsoft_OpenHandsNET` (branch main, 2026-10-19) |

## User Story

Como mantenedor do OpenHandsNET, quero eliminar as 32 issues SonarCloud do tipo `BUG` (7 regras distintas) para reduzir risco e débito técnico, com cada correção coberta por testes e sem alteração de comportamento funcional.

## Escopo

Agrupamento por regra SonarQube (mesmo tipo) — a lista canônica issue→arquivo→linha está em `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` e `sonarqube_issues.json`.

| Regra | Qtd | Severidade predominante |
|---|---|---|
| `csharpsquid:S8949` | 14 | MAJOR |
| `csharpsquid:S3903` | 11 | MAJOR |
| `csharpsquid:S3168` | 2 | MAJOR |
| `csharpsquid:S3655` | 2 | MAJOR |
| `csharpsquid:S2930` | 1 | BLOCKER |
| `csharpsquid:S2583` | 1 | MAJOR |
| `csharpsquid:S3923` | 1 | MAJOR |

## Contexto técnico

- Stack: .NET 10 / C# 14 (+ workflows YAML, shell, docker, k8s manifests).
- Leitura: arquivos listados no ToDo Board (coluna File/Line).
- Testes: `tests/` — xUnit+Shouldly; novas linhas exigem cobertura (Iron Law).
- Lint gate: `dotnet format --verify-no-changes` deve permanecer verde.

## Requisitos

1. Corrigir todas as 14 ocorrência(s) de `csharpsquid:S8949` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
2. Corrigir todas as 11 ocorrência(s) de `csharpsquid:S3903` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
3. Corrigir todas as 2 ocorrência(s) de `csharpsquid:S3168` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
4. Corrigir todas as 2 ocorrência(s) de `csharpsquid:S3655` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
5. Corrigir todas as 1 ocorrência(s) de `csharpsquid:S2930` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
6. Corrigir todas as 1 ocorrência(s) de `csharpsquid:S2583` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
7. Corrigir todas as 1 ocorrência(s) de `csharpsquid:S3923` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).

## Critérios de aceite

- [ ] `api/issues/search` não retorna mais nenhuma das 32 issues desta SPEC para `afonsoft_OpenHandsNET`.
- [ ] `dotnet build -c Release` 0 warnings/errors; `dotnet test` 100% verde.
- [ ] Cobertura das linhas modificadas (sem exclusions).
- [ ] Nenhuma regra de negócio alterada sem justificativa documentada.

## Task Plan

1. Filtrar issues desta SPEC no ToDo Board; corrigir por regra (uma regra por commit lógico).
2. Adicionar/ajustar testes para cada linha tocada.
3. `dotnet format` + build + test suite.
4. Atualizar checkboxes do ToDo Board; re-scan SonarCloud via CI após merge.
