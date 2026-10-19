# CONTEXT.md — OpenHandsNET

Estratégia de carregamento de contexto. Lida no início de cada sessão
(ver Agent Loop em `CLAUDE.md`).

## Linguagem de domínio

- **Conversa** (`AppConversation`) — sessão do usuário com o agente; possui
  1 sandbox, 1 event stream, estados `WORKING→…→READY/ERROR`.
- **Evento V1** — unidade atômica (`kind`, `id` ULID, `source`, `parent_id`);
  ações (`ActionEvent`), observações, deltas efêmeros (`streaming_delta`).
- **Start-task** — POST `/app-conversations` retorna task assíncrono, nunca
  `conversation_id` direto.
- **Sandbox** — workspace isolado por conversa (Docker local /
  `KubernetesSandboxEngine` / `ISandboxEngine` remoto via RuntimeApi).
- **Agent loop** — ReAct: `IAgentLlmProvider` (Semantic Kernel) + tools
  (`ToolRegistry`, builtins em `Infrastructure/Agent/Tools/`).
- **SPEC SDD** — `.specs/NN_name_spec.md` ativa; entregue → `docs/specs/`.

## Prioridade de leitura (sessão fria)

1. `CLAUDE.md` + `.claude/rules/global-rules.md` (always-on nativo)
2. `.claude/memory/memory.md` + 3 arquivos `*-memory.md` mais recentes
   + `orchestrator_sessions.md` (contexto de sessões)
3. `.claude/CONTEXT.md` e `.claude/RULES.md` (este arquivo + guardrails)
4. SPEC(s) relevantes em `.specs/` (ativas) ou `docs/specs/` (entregues)
5. Código por demanda — nunca ler `bin/`, `obj/`, `*.dll`, `logs/`

## Token budget

- Reservar ~20% do contexto para saída.
- Arquivos >500 linhas: ler por seções (`offset`/`limit`), nunca inteiro
  de uma vez.
- `.specs/*.md` e SPECs arquivadas: ler `0. Metadata` + seção relevante.

## Compaction ladder

1. Resumo incremental em `.claude/memory/memory.md` (state, max 100 linhas)
2. Prompts/decisões/lições em `.claude/memory/{YYYYMMDD}-memory.md`
3. Conhecimento reutilizável → `.claude/knowledge/{slug}.md`
