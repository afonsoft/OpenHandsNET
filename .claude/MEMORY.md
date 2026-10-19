# MEMORY.md — Memory Protocol (docs)

Protocolo de memória cross-session. **Este arquivo é só documentação —
não guarda estado.**

## Estrutura

| Tipo | Path | Conteúdo |
|---|---|---|
| State (short-term) | `.claude/memory/memory.md` | estado da sessão corrente, ≤100 linhas, sobrescrito |
| History (long-term) | `.claude/memory/{YYYYMMDD}-memory.md` | prompts, decisões, débito, lições — append-only |
| Orchestrator | `.claude/memory/orchestrator_stats.md` `orchestrator_sessions.md` | DAG/state da skill orchestrator |
| Audit runs | `.claude/memory/gap-analysis-*.md` | resume state de auditorias |
| Knowledge | `.claude/knowledge/{slug}.md` | fatos reutilizáveis promovidos da memória |
| Devin-side | `.devin/memory/` | sessões do agente Devin (thin platform dir) |

## Regras

- Salvar tudo, sempre: prompts do usuário (1 linha), checkpoints,
  decisões, erros, descobertas.
- Ler `memory.md` + 3 long-term mais recentes no início da sessão.
- Promover conhecimento reutilizável para `.claude/knowledge/`.
- Nada sobrevive só no contexto — persistir antes de compactar/encerrar.
