# SPEC — 65 · SPECs Hygiene: archive `docs/specs/`, numbering, status metadata

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `specs-hygiene` |
| Type | `Docs / bookkeeping` |
| Stack | `markdown` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
| Ticket | `GAP-documentation-specs-archive` · `GAP-documentation-spec-metadata` (gap-analysis 2026-10-19) |
| Depends on | — |
| Touches | `.specs/` · `docs/specs/` (novo) · `.specs/README.md` |

## 1. User Story

**As a** maintainer do OpenHandsNET
**I want** o diretório `.specs/` refletindo a convenção documentada
**So that** SPECs entregues fiquem arquivadas, numeração seja única e
metadados `Status` não deixem specs de pesquisa presas na fila do
orchestrator.

## 2. Evidência (AS-IS × TO-BE)

| Item | TO-BE (fonte) | AS-IS (evidência) |
|---|---|---|
| Arquivo de SPECs entregues | `.specs/README.md`: "`Status`: Draft → Approved → Done (mover para `docs/specs/` ao entregar)"; orchestrator Phase 5.8 | `docs/specs/` **ausente**; 62 SPECs `Done`/`Delivered` ainda em `.specs/` |
| Numeração única | convenção `NN_name_spec.md` implícita | `52_github_app_scm_spec.md` **e** `52_nextgen_improvements_spec.md`; `53_enterprise_sandbox_hardening_and_timetravel.md` **e** `53_scm_providers_spec.md` |
| Campo `Status` presente | template SDD (seção 0. Metadata) | `53_enterprise_sandbox_hardening_and_timetravel.md` sem `Status` (mergeada via PR #134) |
| Research SPECs fechadas | specs de análise cujo deliverable é o próprio documento | `27_cli_opencode_comparative_spec.md`, `32_upstream_enterprise_gap_analysis.md`, `52_nextgen_improvements_spec.md` permanecem `Approved` — orchestrator Phase 4/6 as reenfileira |

## 3. Escopo

1. Criar `docs/specs/` e mover as 62 SPECs `Done`/`Delivered` para lá,
   ajustando `Status` para `Completed` + subseção `Delivered` (commit/PR).
2. Renumerar colisões: `52_nextgen_improvements_spec.md` → próximo número
   livre (metadados `SPEC-52-meta` interno); idem para a duplicata de 53.
3. Adicionar `Status: Delivered (PR #134)` em
   `53_enterprise_sandbox_hardening_and_timetravel.md`.
4. Marcar SPECs 27/32/52-nextgen como `Completed` (tipo `Analysis`) com
   nota de que o deliverable foi a própria análise/roadmap.
5. Atualizar `.specs/README.md` (índice + regra explícita de arquivamento).

## 4. Fora de escopo

- Alterar conteúdo técnico das SPECs.
- Deletar SPECs `Draft` legítimas.

## 5. Acceptance Criteria

- [ ] `docs/specs/` existe com todas as SPECs entregues movidas e
      referenciadas por commit/PR de entrega.
- [ ] Nenhuma colisão de numeração em `.specs/`.
- [ ] Toda SPEC em `.specs/` e `docs/specs/` possui campo `Status` válido.
- [ ] `grep -l 'Status.*Approved' .specs/` retorna apenas SPECs realmente
      aprovadas para implementação.

## 6. Deliverables

- `docs/specs/` populado; `.specs/` contendo apenas SPECs ativas/draft.
- README de índice atualizado.

## 7. Task Plan

| # | Task | Files |
|---|---|---|
| 1 | `mkdir docs/specs` + git mv SPECs Done/Delivered | `.specs/*` → `docs/specs/` |
| 2 | Ajustar `Status`/`Delivered` em cada SPEC movida | `docs/specs/*.md` |
| 3 | Renumerar duplicatas 52/53 + corrigir Status SPEC-53 | `.specs/` |
| 4 | Fechar SPECs de pesquisa 27/32/52-nextgen | `docs/specs/` ou `.specs/` |
| 5 | Atualizar índice `.specs/README.md` | `.specs/README.md` |

## 8. Referências

- `.specs/README.md` (convenção) · `.claude/skills/orchestrator` Phase 5.8
- gap-analysis run `.claude/memory/gap-analysis-20261019.md`

## 9. Guardrails

- `git mv` preserva histórico; nenhum conteúdo técnico alterado.
- Docs em pt-BR.

## 10. DoD

- [ ] Todos os acceptance criteria verificados.
- [ ] `dotnet build` não afetado (docs only).
