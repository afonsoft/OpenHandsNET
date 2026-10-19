# .claude/ — Harness do OpenHandsNET

Infraestrutura de agente (Claude Code / Devin / afonsoft harness).
Fonte única de verdade: [`CLAUDE.md`](../CLAUDE.md) — referência fina:
[`AGENTS.md`](../AGENTS.md).

## Mapa

| Path | Papel | Loading |
|---|---|---|
| `rules/global-rules.md` | hard/soft rules | always-on nativo |
| `rules/dotnet.md` | convenções C#/EF/testes | `paths: **/*.cs,*.razor,*.csproj` |
| `rules/workflows.md` | proteção `.github/` | `paths: .github/**` |
| `agents/{engineer,plan,review,test,architecture}.md` | sub-agentes | Task/description |
| `CONTEXT.md` | estratégia de contexto + domínio | ritual (Agent Loop) |
| `RULES.md` | índice de guardrails | ritual |
| `MEMORY.md` | protocolo de memória (docs) | on-demand |
| `TOOLS.md` | ferramentas/CLI/MCP | on-demand |
| `WORKFLOWS.md` | CI + fluxo SDD | on-demand |
| `memory/memory.md` | estado da sessão | ritual |
| `memory/{YYYYMMDD}-memory.md` | histórico long-term | on-demand (últimos 3) |
| `memory/orchestrator_*.md` | state da skill orchestrator | on-demand |
| `knowledge/` | conhecimento durável | on-demand |
| `settings.json` | permissions/env | nativo |

## Princípios

- **Computacional > prompt**: regras bloqueantes vivem em
  `settings.json`/`permissions.deny` + CI, não só em markdown.
- **CLAUDE.md roteia, não duplica**: detalhe em `.claude/*` e `docs/`.
- **Nada de fonte paralela**: `AGENTS.md` é thin reference; `.devin/`
  guarda apenas memória do agente Devin.

Gerado por `create-agent-harness` + SPEC-67 (issue #149, 2026-10-19).
