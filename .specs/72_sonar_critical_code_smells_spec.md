# SPEC-72: Sonar Critical Code Smells (SonarCloud)

| Campo | Valor |
|---|---|
| Status | Approved |
| Issue | #154 |
| Type | Refactor |
| Sonar type | CODE_SMELL |
| Issues | 121 issues · 7 regras · esforço Sonar ~0min |
| Fonte | SonarCloud `afonsoft_OpenHandsNET` (branch main, 2026-10-19) |

## User Story

Como mantenedor do OpenHandsNET, quero eliminar as 121 issues SonarCloud do tipo `CODE_SMELL` (7 regras distintas) para reduzir risco e débito técnico, com cada correção coberta por testes e sem alteração de comportamento funcional.

## Escopo

Agrupamento por regra SonarQube (mesmo tipo) — a lista canônica issue→arquivo→linha está em `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` e `sonarqube_issues.json`.

| Regra | Qtd | Severidade predominante |
|---|---|---|
| `csharpsquid:S3776` | 74 | CRITICAL |
| `csharpsquid:S927` | 39 | CRITICAL |
| `csharpsquid:S4487` | 3 | CRITICAL |
| `csharpsquid:S3218` | 2 | CRITICAL |
| `csharpsquid:S2699` | 1 | BLOCKER |
| `csharpsquid:S1994` | 1 | CRITICAL |
| `csharpsquid:S3871` | 1 | CRITICAL |

## Contexto técnico

- Stack: .NET 10 / C# 14 (+ workflows YAML, shell, docker, k8s manifests).
- Leitura: arquivos listados no ToDo Board (coluna File/Line).
- Testes: `tests/` — xUnit+Shouldly; novas linhas exigem cobertura (Iron Law).
- Lint gate: `dotnet format --verify-no-changes` deve permanecer verde.

## Requisitos

1. Corrigir todas as 74 ocorrência(s) de `csharpsquid:S3776` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
2. Corrigir todas as 39 ocorrência(s) de `csharpsquid:S927` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
3. Corrigir todas as 3 ocorrência(s) de `csharpsquid:S4487` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
4. Corrigir todas as 2 ocorrência(s) de `csharpsquid:S3218` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
5. Corrigir todas as 1 ocorrência(s) de `csharpsquid:S2699` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
6. Corrigir todas as 1 ocorrência(s) de `csharpsquid:S1994` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
7. Corrigir todas as 1 ocorrência(s) de `csharpsquid:S3871` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).

## Critérios de aceite

- [ ] `api/issues/search` não retorna mais nenhuma das 121 issues desta SPEC para `afonsoft_OpenHandsNET`.
- [ ] `dotnet build -c Release` 0 warnings/errors; `dotnet test` 100% verde.
- [ ] Cobertura das linhas modificadas (sem exclusions).
- [ ] Nenhuma regra de negócio alterada sem justificativa documentada.

## Task Plan

1. Filtrar issues desta SPEC no ToDo Board; corrigir por regra (uma regra por commit lógico).
2. Adicionar/ajustar testes para cada linha tocada.
3. `dotnet format` + build + test suite.
4. Atualizar checkboxes do ToDo Board; re-scan SonarCloud via CI após merge.

## Live rescan — 2026-10-23 (SPEC-96, `resolved=false`)

| Regra | Board | Open live | Veredito |
|---|---|---|---|
| `csharpsquid:S3776` | 74 | 73 | deferred → SPECs 101–103 |
| `csharpsquid:S927` | 39 | 35 | **real** — param renames no `cli/` |
| `javascript:S3776` | — | 18 | `docs/architecture/runtime-architecture.html` gerado → exclusão (ci.yml) |
| `csharpsquid:S3218` | 2 | 4 | deferred → SPEC-105 |
| `csharpsquid:S4487` | 3 | 0 | resolvida |
| `csharpsquid:S2699` | 1 | 0 | resolvida via PR #246 |
| `csharpsquid:S1994`/`S3871` | 1/1 | 0 | resolvidas |
| js/py/shell rest | — | 10 | `runtime-architecture.html` + `tools/*.py` |

Critério de fechamento reescrito (RF-004): SPEC-72 fecha quando
`csharpsquid:S927` chegar a 0 no rescan live (S3776/S3218 seguem nos
SPECs filhos 101–103/105).
