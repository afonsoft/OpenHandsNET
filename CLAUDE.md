# CLAUDE.md — OpenHandsNET

## Missão

Port open-source (MIT) do ecossistema **OpenHands** (conceitos Cloud +
Enterprise) para **.NET 10 / C# 14 / Blazor WebAssembly**, com UX inspirada no
Devin Web e componentes de chat/layout derivados de `afonsoft/open-webui`.
Este arquivo é o ponto de entrada de agentes: carregado antes de cada sessão.

## Tech Stack

| Camada | Tecnologia | Versão |
|--------|-----------|--------|
| Runtime | .NET | 10 (C# 14) |
| Frontend | Blazor WebAssembly | — |
| Backend | ASP.NET Core Minimal APIs + SignalR | — |
| ORM | EF Core (SQLite default / PostgreSQL enterprise) | 10 |
| Sandbox | Docker.DotNet (+ Microsoft.Playwright no worker) | — |
| LLM | Microsoft Semantic Kernel / gateway OpenAI-compat | — |
| MCP | MCP C# SDK (stdio + SSE/HTTP) | — |
| CLI | Spectre.Console + System.CommandLine | — |
| Testes | xUnit + Shouldly + Testcontainers + bUnit | — |
| CSS | Tailwind CSS v4 | — |

## Estrutura do Projeto (alvo — SPEC 01)

```
src/OpenHandsNET.Domain          # eventos (kind), entidades — zero deps
src/OpenHandsNET.Application     # contratos/DTOs/interfaces
src/OpenHandsNET.Infrastructure  # EF Core, Docker.DotNet, secrets, webhooks
src/OpenHandsNET.Api             # /api/v1 + hubs SignalR + hosting WASM
src/OpenHandsNET.Client          # Blazor WASM (base: open-webui-net Client)
cli/OpenHandsNET.Cli             # `openhandsnet` CLI
tests/                           # Unit · Integration (Testcontainers) · Client
.specs/                          # SPECs SDD ativas (fonte única de verdade)
docs/specs/                      # SPECs entregues (arquivo morto)
migration-plan/                  # research + roadmap do port
docs/reference/                  # baseline upstream + extratos de docs
```

Dependências: `Api → Infrastructure → Application → Domain`; `Client → Application`;
`Cli` consome só contratos HTTP/SignalR.

## Fontes de verdade (ordem)

1. `.specs/*.md` — SPECs aprovados governam implementação.
2. `migration-plan/research/api-inventory.md` — contrato V1 verificado no código upstream.
3. `docs/reference/` — baseline e extratos das docs oficiais.

## Regras duras (hard rules)

1. **Modelo de eventos V1**: discriminador `kind`, `id` ULID/UUID, `source`,
   `parent_id` — **nunca** o formato V0 (id int + `action`/`args`).
2. **Start-task assíncrono**: `POST /app-conversations` retorna task
   (`WORKING→…→READY`), nunca `conversation_id` direto; campo é `selected_repository`.
3. **Licença**: upstream `enterprise/` e `OpenHands-Cloud/` são Polyform —
   implementar conceitos próprios, **não portar código**.
4. Nunca commitar secrets; `sk-*` só com hash SHA-256; segredos serializam mascarados.
5. `main` protegida — merge via PR; testes verdes obrigatórios.
6. Não modificar `.github/workflows/` sem revisão humana.

## Regras suaves (soft rules)

1. Docs/comentários em pt-BR; código, commits e APIs em inglês (snake_case JSON).
2. `async/await` + `Async` suffix + `CancellationToken`; `record` imutável;
   `ValueTask`/`IAsyncEnumerable` onde couber.
3. Mudança de schema → EF migration + `DatabaseMigrator` no startup.
4. Divergência de contrato vs upstream → documentar na SPEC (seção API Contract).

## Comandos

```bash
dotnet restore OpenHandsNET.slnx && dotnet build OpenHandsNET.slnx -c Release
dotnet test
dotnet run --project src/OpenHandsNET.Api        # app em http://localhost:8080
docker compose up -d --build                      # app em http://localhost:3032
```

## Harness Structure

| Componente | Local | Loading |
|---|---|---|
| Instruções raiz | `CLAUDE.md` | always-on nativo |
| Regras globais | `.claude/rules/global-rules.md` | always-on nativo |
| Regras de domínio | `.claude/rules/{dotnet,workflows}.md` | path-scoped |
| Sub-agentes | `.claude/agents/{engineer,plan,review,test,architecture}.md` | Task |
| Contexto/domínio | `.claude/CONTEXT.md` | ritual (Agent Loop) |
| Guardrails | `.claude/RULES.md` | ritual |
| Memória state/history | `.claude/memory/{memory,YYYYMMDD}-memory.md` | ritual/on-demand |
| Protocolo memória | `.claude/MEMORY.md` | on-demand |
| Tools/MCP · workflows · harness | `.claude/{TOOLS,WORKFLOWS,README}.md` | on-demand |
| Arquitetura | `docs/architecture/` | on-demand |

## Context Engineering

Prioridade de leitura e orçamento de tokens: `.claude/CONTEXT.md`.
Reservar ~20% do contexto para saída; arquivos >500 linhas por seções.

## Memory Protocol

- **State**: `.claude/memory/memory.md` — sobrescrito a cada sessão (≤100 linhas).
- **History**: `.claude/memory/{YYYYMMDD}-memory.md` — append-only;
  prompts, decisões, débito, lições.
- **Orchestrator**: `orchestrator_stats.md` + `orchestrator_sessions.md`.
- **Knowledge**: `.claude/knowledge/` — fatos duráveis promovidos da memória.
- **Save always**: registrar cada prompt, checkpoint, decisão e erro;
  nada sobrevive só no contexto.
- **## Prompts**: toda instrução do usuário gera uma linha no arquivo
  long-term do dia sob `## Prompts`; checkpoints/decisões sob suas
  seções; `## Session summary` antes de compactar/encerrar.

## Agent Loop

Padrão **Plan-and-Execute**:

1. `CLAUDE.md` + `global-rules.md` carregados (always-on nativo).
2. Ler `memory.md` + 3 `*-memory.md` recentes + `orchestrator_sessions.md`.
3. Ler `.claude/CONTEXT.md` + `.claude/RULES.md` (ritual).
4. Ler `.specs/` ativas relevantes → plano → implementar em `feature/*`
   → `build → test` → PR. SPECs: `write-specs` → aprovação →
   `create-issues` → `execute-specs`/`tdd-spec` → review → merge →
   arquivar em `docs/specs/`.
5. Verificação: `dotnet format --verify-no-changes` → `dotnet build` →
   `dotnet test`.
6. Atualizar memória antes de encerrar/compactar.

## Always-on Connection

Claude Code carrega nativamente só `CLAUDE.md` + `.claude/rules/*.md`
(sem `paths:`). Os demais documentos always-on (`.claude/CONTEXT.md`,
`.claude/RULES.md`, `memory.md`) são lidos pelo ritual do Agent Loop.

## Autonomia do agente

- **Execute antes de anunciar**: nunca termine o turno dizendo "vou
  criar/alterar X" sem que X já exista — a chamada de ferramenta acontece
  no mesmo turno, não no próximo.
- **Erro de ferramenta é retry, não pausa**: falha de schema/args (ex.:
  `expected object, got string`) → corrija os argumentos e repita na hora;
  erro recuperável nunca espera `continue` do usuário.
- **Verifique o artefato**: depois de write/edit, confirme com leitura ou
  listagem antes de afirmar que o arquivo foi criado.
- **Turno completo**: tarefas multi-passo seguem até entregar ou bater num
  gate explícito (aprovação de SPEC, credencial, operação destrutiva) —
  nunca pare no meio "aguardando".

## Response Style

- Português (pt-BR) para o usuário; código/commits em inglês (Conventional Commits).
- Conciso e direto; referências `path:linha`.

## Referências

- [.claude/rules/](.claude/rules/) — regras always-on e path-scoped
- [.claude/agents/](.claude/agents/) — sub-agentes do harness
- [.claude/CONTEXT.md](.claude/CONTEXT.md) · [.claude/RULES.md](.claude/RULES.md)
- [.claude/MEMORY.md](.claude/MEMORY.md) · [.claude/TOOLS.md](.claude/TOOLS.md) · [.claude/WORKFLOWS.md](.claude/WORKFLOWS.md)
- [.claude/README.md](.claude/README.md) — infraestrutura do harness
- [.claude/memory/](.claude/memory/) — estado cross-session
- [docs/architecture/](docs/architecture/) — ADRs e diagramas
- [.specs/](.specs/) — SPECs ativas · [docs/specs/](docs/specs/) — entregues
