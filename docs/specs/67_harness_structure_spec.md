# SPEC — 67 · Harness Structure: `.claude/` files + `docs/architecture/`

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `harness-structure` |
| Type | `Docs / harness` |
| Stack | `markdown · yaml` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
| Ticket | `GAP-documentation-harness-structure` (gap-analysis 2026-10-19) |
| Depends on | — |
| Touches | `.claude/` · `docs/architecture/` (novo) |

## 1. User Story

**As a** agente/maintainer do OpenHandsNET
**I want** a estrutura de harness prevista pelo orchestrator presente
**So that** contexto, regras e decisões arquiteturais fiquem
descobríveis e auditáveis entre sessões.

## 2. Evidência (AS-IS × TO-BE)

| Item | TO-BE (fonte) | AS-IS (evidência) |
|---|---|---|
| `.claude/settings.json` | orchestrator Phase 2 audit checklist ("permissions, hooks, env") | ausente |
| `.claude/rules/` + `.claude/agents/` | checklist ("global-rules.md, stack-scoped rules/", "engineer.md, plan.md, review.md, test.md, architecture.md") | ambos ausentes |
| `.claude/CONTEXT.md`, `.claude/MEMORY.md`, `.claude/README.md` | checklist | ausentes (`.claude/memory/` existe e é usado) |
| `docs/architecture/` | checklist + Phase 5 (`/architecture` owns ADRs/diagrams) | ausente — nenhum ADR ou diagrama de arquitetura existe no repo |

**Ressalva**: a sessão 2026-10-15 decidiu conscientemente criar um
`AGENTS.md` thin apontando para `CLAUDE.md` (gap P4 parcialmente
endereçado). O escopo desta SPEC deve ser validado no gate — opção
mínima aceitável: documentar a decisão "thin harness" em
`CLAUDE.md`/`AGENTS.md` e criar apenas `docs/architecture/`.

## 3. Escopo (proposta)

1. Criar `docs/architecture/` com:
   - ADR-0001: decisão "thin harness" (AGENTS.md → CLAUDE.md, sem
     `.claude/rules|agents|CONTEXT|MEMORY`), justificando a divergência
     do checklist do orchestrator.
   - Diagrama de arquitetura do sistema (Mermaid, headless-friendly):
     camadas Domain/Application/Infrastructure/Api/Client/Cli +
     RuntimeApi/Sandboxing/SandboxAgent (já documentado em CLAUDE.md —
     formalizar em `docs/architecture/overview.md`).
2. `.claude/settings.json` mínimo (permissions/hooks) **ou** ADR
   documentando ausência intencional.
3. `.claude/CONTEXT.md` com a linguagem de domínio (port do OpenHands:
   eventos V1, app-conversations, sandbox, agent loop) — hoje só existe
   implícita em CLAUDE.md/SPECs.

## 4. Fora de escopo

- Reescrever CLAUDE.md (permanece a fonte única de verdade).
- Agentes sub-`.claude/agents/` customizados se o thin harness for
  confirmado como decisão final.

## 5. Acceptance Criteria

- [ ] `docs/architecture/` existe com ≥1 ADR e 1 diagrama do sistema.
- [ ] A divergência "thin harness" vs checklist do orchestrator está
      documentada (ADR ou nota em AGENTS.md).
- [ ] `.claude/CONTEXT.md` ou decisão documentada de não criá-lo.

## 6. Deliverables

- `docs/architecture/0001-thin-harness-adr.md`
- `docs/architecture/overview.md` (+ diagrama Mermaid)
- `.claude/CONTEXT.md` e/ou `.claude/settings.json` (conforme gate)

## 7. Task Plan

| # | Task | Files |
|---|---|---|
| 1 | ADR thin harness | `docs/architecture/0001-*.md` |
| 2 | Diagrama/overview arquitetural | `docs/architecture/overview.md` |
| 3 | `.claude/CONTEXT.md` (+ settings.json se aprovado) | `.claude/` |

## 8. Referências

- orchestrator Phase 2 checklist · Phase 5 `/architecture`
- `.claude/memory/orchestrator_sessions.md` (decisão 2026-10-15)

## 9. Guardrails

- Docs em pt-BR; referenciar, não duplicar, CLAUDE.md.

## 10. DoD

- [ ] Checklist do orchestrator Phase 2 reavaliado: cada item ausente
      tem arquivo criado **ou** ADR de ausência intencional.
