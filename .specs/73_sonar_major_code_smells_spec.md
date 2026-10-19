# SPEC-73: Sonar Major Code Smells (SonarCloud)

| Campo | Valor |
|---|---|
| Status | Approved |
| Issue | #155 |
| Type | Refactor |
| Sonar type | CODE_SMELL |
| Issues | 243 issues · 29 regras · esforço Sonar ~0min |
| Fonte | SonarCloud `afonsoft_OpenHandsNET` (branch main, 2026-10-19) |

## User Story

Como mantenedor do OpenHandsNET, quero eliminar as 243 issues SonarCloud do tipo `CODE_SMELL` (29 regras distintas) para reduzir risco e débito técnico, com cada correção coberta por testes e sem alteração de comportamento funcional.

## Escopo

Agrupamento por regra SonarQube (mesmo tipo) — a lista canônica issue→arquivo→linha está em `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` e `sonarqube_issues.json`.

| Regra | Qtd | Severidade predominante |
|---|---|---|
| `csharpsquid:S3358` | 69 | MAJOR |
| `csharpsquid:S108` | 54 | MAJOR |
| `csharpsquid:S107` | 29 | MAJOR |
| `csharpsquid:S6966` | 13 | MAJOR |
| `shelldre:S7688` | 11 | MAJOR |
| `csharpsquid:S1172` | 10 | MAJOR |
| `javascript:S8786` | 7 | MAJOR |
| `javascript:S6535` | 6 | MAJOR |
| `csharpsquid:S6580` | 5 | MAJOR |
| `css:S9423` | 4 | MAJOR |
| `javascript:S1121` | 4 | MAJOR |
| `javascript:S2681` | 4 | MAJOR |
| `csharpsquid:S127` | 3 | MAJOR |
| `csharpsquid:S2589` | 3 | MAJOR |
| `css:S4666` | 3 | MAJOR |
| `csharpsquid:S1117` | 2 | MAJOR |
| `csharpsquid:S1118` | 2 | MAJOR |
| `csharpsquid:S1121` | 2 | MAJOR |
| `javascript:S7761` | 2 | MAJOR |
| `csharpsquid:S1854` | 1 | MAJOR |
| `csharpsquid:S2376` | 1 | MAJOR |
| `csharpsquid:S2743` | 1 | MAJOR |
| `csharpsquid:S2925` | 1 | MAJOR |
| `csharpsquid:S3928` | 1 | MAJOR |
| `csharpsquid:S4581` | 1 | MAJOR |
| `csharpsquid:S6673` | 1 | MAJOR |
| `javascript:S1871` | 1 | MAJOR |
| `kubernetes:S6596` | 1 | MAJOR |
| `kubernetes:S6897` | 1 | MAJOR |

## Contexto técnico

- Stack: .NET 10 / C# 14 (+ workflows YAML, shell, docker, k8s manifests).
- Leitura: arquivos listados no ToDo Board (coluna File/Line).
- Testes: `tests/` — xUnit+Shouldly; novas linhas exigem cobertura (Iron Law).
- Lint gate: `dotnet format --verify-no-changes` deve permanecer verde.

## Requisitos

1. Corrigir todas as 69 ocorrência(s) de `csharpsquid:S3358` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
2. Corrigir todas as 54 ocorrência(s) de `csharpsquid:S108` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
3. Corrigir todas as 29 ocorrência(s) de `csharpsquid:S107` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
4. Corrigir todas as 13 ocorrência(s) de `csharpsquid:S6966` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
5. Corrigir todas as 11 ocorrência(s) de `shelldre:S7688` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
6. Corrigir todas as 10 ocorrência(s) de `csharpsquid:S1172` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
7. Corrigir todas as 7 ocorrência(s) de `javascript:S8786` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
8. Corrigir todas as 6 ocorrência(s) de `javascript:S6535` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
9. Corrigir todas as 5 ocorrência(s) de `csharpsquid:S6580` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
10. Corrigir todas as 4 ocorrência(s) de `css:S9423` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
11. Corrigir todas as 4 ocorrência(s) de `javascript:S1121` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
12. Corrigir todas as 4 ocorrência(s) de `javascript:S2681` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
13. Corrigir todas as 3 ocorrência(s) de `csharpsquid:S127` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
14. Corrigir todas as 3 ocorrência(s) de `csharpsquid:S2589` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
15. Corrigir todas as 3 ocorrência(s) de `css:S4666` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
16. Corrigir todas as 2 ocorrência(s) de `csharpsquid:S1117` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
17. Corrigir todas as 2 ocorrência(s) de `csharpsquid:S1118` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
18. Corrigir todas as 2 ocorrência(s) de `csharpsquid:S1121` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
19. Corrigir todas as 2 ocorrência(s) de `javascript:S7761` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
20. Corrigir todas as 1 ocorrência(s) de `csharpsquid:S1854` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
21. Corrigir todas as 1 ocorrência(s) de `csharpsquid:S2376` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
22. Corrigir todas as 1 ocorrência(s) de `csharpsquid:S2743` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
23. Corrigir todas as 1 ocorrência(s) de `csharpsquid:S2925` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
24. Corrigir todas as 1 ocorrência(s) de `csharpsquid:S3928` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
25. Corrigir todas as 1 ocorrência(s) de `csharpsquid:S4581` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
26. Corrigir todas as 1 ocorrência(s) de `csharpsquid:S6673` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
27. Corrigir todas as 1 ocorrência(s) de `javascript:S1871` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
28. Corrigir todas as 1 ocorrência(s) de `kubernetes:S6596` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).
29. Corrigir todas as 1 ocorrência(s) de `kubernetes:S6897` conforme a mensagem da issue, sem suppressions (`NOSONAR`, `ExcludeFromCodeCoverage` etc.).

## Critérios de aceite

- [ ] `api/issues/search` não retorna mais nenhuma das 243 issues desta SPEC para `afonsoft_OpenHandsNET`.
- [ ] `dotnet build -c Release` 0 warnings/errors; `dotnet test` 100% verde.
- [ ] Cobertura das linhas modificadas (sem exclusions).
- [ ] Nenhuma regra de negócio alterada sem justificativa documentada.

## Task Plan

1. Filtrar issues desta SPEC no ToDo Board; corrigir por regra (uma regra por commit lógico).
2. Adicionar/ajustar testes para cada linha tocada.
3. `dotnet format` + build + test suite.
4. Atualizar checkboxes do ToDo Board; re-scan SonarCloud via CI após merge.
