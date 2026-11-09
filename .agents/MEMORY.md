# MEMORY.md — Memory Protocol (docs)

Protocolo de memória cross-session. **Este arquivo é só documentação —
não guarda estado.**

## Estrutura

| Tipo | Path | Conteúdo |
|---|---|---|
| State (short-term) | `.agents/memory/memory.md` | estado da sessão corrente, ≤100 linhas, sobrescrito |
| History (long-term) | `.agents/memory/{YYYYMMDD}-memory.md` | prompts, decisões, débito, lições — append-only |
| Orchestrator | `.agents/memory/orchestrator_stats.md` `orchestrator_sessions.md` | DAG/state da skill orchestrator |
| Audit runs | `.agents/memory/gap-analysis-*.md` | resume state de auditorias |
| Knowledge | `.agents/knowledge/{slug}.md` | fatos reutilizáveis promovidos da memória |
| Devin-side | `.devin/memory/` | sessões do agente Devin (thin platform dir) |

## Regras

- Salvar tudo, sempre: prompts do usuário (1 linha), checkpoints,
  decisões, erros, descobertas.
- Ler `memory.md` + 3 long-term mais recentes no início da sessão.
- Promover conhecimento reutilizável para `.agents/knowledge/`.
- Nada sobrevive só no contexto — persistir antes de compactar/encerrar.
