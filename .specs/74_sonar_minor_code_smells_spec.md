# SPEC-74: Sonar Minor Code Smells (SonarCloud)

| Campo | Valor |
|---|---|
| Status | Approved |
| Issue | #156 |
| Type | Refactor |
| Sonar type | CODE_SMELL |
| Issues | 331 issues · 24 regras · esforço Sonar ~0min |
| Fonte | SonarCloud `afonsoft_OpenHandsNET` (branch main, 2026-10-19) |

## User Story

Como mantenedor do OpenHandsNET, quero eliminar as 331 issues SonarCloud do tipo `CODE_SMELL` (24 regras distintas) para reduzir risco e débito técnico, com cada correção coberta por testes e sem alteração de comportamento funcional.

## Escopo

Agrupamento por regra SonarQube (mesmo tipo) — a lista canônica issue→arquivo→linha está em `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` e `sonarqube_issues.json`.

| Regra | Qtd | Severidade predominante |
|---|---|---|
| `csharpsquid:S8970` | 145 | MINOR |
| `csharpsquid:S1192` | 50 | MINOR |
| `csharpsquid:S2486` | 30 | MINOR |
| `csharpsquid:S3267` | 20 | MINOR |
| `javascript:S6353` | 13 | MINOR |
| `csharpsquid:S1905` | 12 | MINOR |
| `csharpsquid:S1075` | 10 | MINOR |
| `csharpsquid:S2325` | 9 | MINOR |
| `javascript:S6582` | 9 | MINOR |
| `csharpsquid:S1481` | 7 | MINOR |
| `csharpsquid:S6608` | 5 | MINOR |
| `csharpsquid:S6667` | 3 | MINOR |
| `css:S1874` | 3 | MINOR |
| `csharpsquid:S1135` | 3 | INFO |
| `csharpsquid:S2094` | 2 | MINOR |
| `docker:S7031` | 2 | MINOR |
| `csharpsquid:S1125` | 1 | MINOR |
| `csharpsquid:S1199` | 1 | MINOR |
| `csharpsquid:S1643` | 1 | MINOR |
| `csharpsquid:S2737` | 1 | MINOR |
| `csharpsquid:S3220` | 1 | MINOR |
| `csharpsquid:S3400` | 1 | MINOR |
| `docker:S7018` | 1 | MINOR |
| `javascript:S4138` | 1 | MINOR |

## Contexto técnico

- Stack: .NET 10 / C# 14 (+ workflows YAML, shell, docker, k8s manifests).
- Leitura: arquivos listados no ToDo Board (coluna File/Line).
- Testes: `tests/` — xUnit+Shouldly; novas linhas exigem cobertura (Iron Law).
- Lint gate: `dotnet format --verify-no-changes` deve permanecer verde.

## Requisitos

1. Corrigir todas as 145 ocorrência(s) de `csharpsquid:S8970` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
2. Corrigir todas as 50 ocorrência(s) de `csharpsquid:S1192` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
3. Corrigir todas as 30 ocorrência(s) de `csharpsquid:S2486` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
4. Corrigir todas as 20 ocorrência(s) de `csharpsquid:S3267` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
5. Corrigir todas as 13 ocorrência(s) de `javascript:S6353` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
6. Corrigir todas as 12 ocorrência(s) de `csharpsquid:S1905` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
7. Corrigir todas as 10 ocorrência(s) de `csharpsquid:S1075` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
8. Corrigir todas as 9 ocorrência(s) de `csharpsquid:S2325` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
9. Corrigir todas as 9 ocorrência(s) de `javascript:S6582` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
10. Corrigir todas as 7 ocorrência(s) de `csharpsquid:S1481` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
11. Corrigir todas as 5 ocorrência(s) de `csharpsquid:S6608` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
12. Corrigir todas as 3 ocorrência(s) de `csharpsquid:S6667` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
13. Corrigir todas as 3 ocorrência(s) de `css:S1874` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
14. Corrigir todas as 3 ocorrência(s) de `csharpsquid:S1135` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
15. Corrigir todas as 2 ocorrência(s) de `csharpsquid:S2094` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
16. Corrigir todas as 2 ocorrência(s) de `docker:S7031` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
17. Corrigir todas as 1 ocorrência(s) de `csharpsquid:S1125` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
18. Corrigir todas as 1 ocorrência(s) de `csharpsquid:S1199` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
19. Corrigir todas as 1 ocorrência(s) de `csharpsquid:S1643` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
20. Corrigir todas as 1 ocorrência(s) de `csharpsquid:S2737` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
21. Corrigir todas as 1 ocorrência(s) de `csharpsquid:S3220` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
22. Corrigir todas as 1 ocorrência(s) de `csharpsquid:S3400` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
23. Corrigir todas as 1 ocorrência(s) de `docker:S7018` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
24. Corrigir todas as 1 ocorrência(s) de `javascript:S4138` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).

## Critérios de aceite

- [ ] `api/issues/search` não retorna mais nenhuma das 331 issues desta SPEC para `afonsoft_OpenHandsNET`.
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
| `csharpsquid:S8970` | 145 | 143 | **real** — params naming, mecânico |
| `csharpsquid:S1192` | 50 | 1 | quase zerada |
| `csharpsquid:S2486` | 30 | 0 | resolvida |
| `csharpsquid:S3267` | 20 | 9 | real (loops → LINQ) |
| `csharpsquid:S1075` | 10 | 10 | deferred → SPEC-106 |
| `javascript:*` | ~80 | ~95 | gerado/monaco vendored → exclusão |
| `css:*` / `docker:*` | ~10 | ~10 | tail real pequeno |
| resto csharpsquid | ~40 | ~25 | tail real pequeno |

Critério de fechamento reescrito (RF-004): SPEC-74 fecha quando
`csharpsquid` MINOR não-deferidas chegarem a 0 (S1075 no SPEC-106;
js/css vendored/gerado via exclusão).
