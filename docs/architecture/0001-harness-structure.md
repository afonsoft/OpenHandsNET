# ADR-0001 — Estrutura do harness agente

| Campo | Valor |
|---|---|
| Status | Accepted |
| Data | 2026-10-19 |
| Contexto | SPEC-67 / gap-analysis-20261019 / issue #149 |

## Contexto

O checklist do orchestrator (Phase 2) e da skill `create-agent-harness`
espera a estrutura `.claude/` completa (`settings.json`, `rules/`,
`agents/`, `CONTEXT.md`, `RULES.md`, `MEMORY.md`, `TOOLS.md`,
`WORKFLOWS.md`, `README.md`, `memory/`). Até 2026-10-19 o repo tinha
apenas `CLAUDE.md`, `AGENTS.md` (thin) e `.claude/memory/` — decisão
consciente de "thin harness" registrada na sessão 2026-10-15, que
mostrou-se insuficiente: guardrails dependiam só de prompts e a
linguagem de domínio não era descobrível fora de `CLAUDE.md`.

## Decisão

Adotar a estrutura `.claude/` completa do harness afonsoft:

- `.claude/settings.json` — permissions allow/deny + env (controle
  computacional, não ignorable por prompt).
- `.claude/rules/` — `global-rules.md` (always-on) + `dotnet.md` e
  `workflows.md` (path-scoped).
- `.claude/agents/` — 5 sub-agentes mandatórios (engineer, plan,
  review, test, architecture) copiados do template da skill.
- `.claude/CONTEXT.md` — linguagem de domínio + estratégia de contexto.
- `.claude/RULES.md`, `MEMORY.md`, `TOOLS.md`, `WORKFLOWS.md`,
  `README.md` — referências on-demand/ritual.
- `.claude/memory/memory.md` + `{YYYYMMDD}-memory.md` — protocolo
  state/history (coexiste com `orchestrator_*.md` e `gap-analysis-*.md`).
- `docs/architecture/` — este ADR + `overview.md` (diagrama Mermaid).

`CLAUDE.md` permanece a fonte única de verdade (merge de seções, nunca
overwrite); `AGENTS.md` permanece thin; `.devin/` permanece apenas como
memória do agente Devin.

## Consequências

- (+) Guardrails bloqueantes agora são computacionais
  (`permissions.deny`) além de prosa.
- (+) Domínio/convenções descobríveis por qualquer agente via ritual
  de leitura.
- (+) `docs/architecture/` passa a existir — Phase 5 do orchestrator
  tem onde publicar ADRs/diagramas.
- (−) Manter `.claude/*` sincronizado com `CLAUDE.md` é débito
  operacional contínuo (mitigado por referências, não duplicação).
