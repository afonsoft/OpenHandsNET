# SPEC — 25 · Tool Surface, Repo Discovery & Skill Federation

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `tools-skills-discovery` |
| Type | `Feature / Agent Core` |
| Stack | `.NET 10 · sandbox agent v2 · MCP C# SDK` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Done` |
| Ticket | [#54](https://github.com/afonsoft/OpenHandsNET/issues/54) → PR #60 |
| Depends on | `09_agent_engine_spec` (tools), `11_skills_hooks_spec` (skills), `07_mcp_integration_spec`, `24_agent_activity_protocol_spec` (agente v2) |
| Touches | `ToolRegistry`, `BuiltinTools`, `ISkillService`, `SkillService`, `IMcpClientService`, `McpToolCatalog`, `SystemPromptBuilder` |

## 1. User Story

**As a** usuário com um repo que já tem `AGENTS.md`, `.openhands/skills/`,
`.devin/skills/` e configurações próprias
**I want** que o agente descubra automaticamente as capacidades do repo,
tenha ferramentas de busca/edição precisas, e que MCP exponha schemas reais
**So that** o agente trabalha como um "nativo" do projeto em vez de adivinhar
— e o número de iterações desperdiçadas cai.

**Problem context:**

- `SkillService` só resolve skills do DB (global/org/user) — skills
  **versionadas no repositório** são invisíveis (o padrão da indústria:
  `AGENTS.md`, `.claude/`, `.openhands/`, `.devin/`).
- Toolset mínimo: não há `search`/`grep`/`glob` (o agente usa `bash grep`
  sem limites nem ergonomia), `file_read` lê arquivo inteiro (sem range), e
  `file_edit` falha quando o `old_string` não casa byte-a-byte.
- MCP bridge `mcp__{s}__{t}` anuncia schema genérico `{"type":"object"}` —
  o modelo chama MCP tools às cegas.
- Não há endpoint para inspecionar o toolset efetivo de uma conversa.

## 2. Scope (RF)

### RF-001 — Repo discovery no start-task

`ConversationStartWorker`, após clone/blueprint, roda descoberta
(`ISandboxExecutor` + agente v2 `/files`):

| Path (repo root) | Tipo |
|---|---|
| `AGENTS.md`, `CLAUDE.md`, `CONTRIBUTING.md` (≤64KB) | `repo_instructions` |
| `.openhands/skills/`, `.devin/skills/`, `.agents/skills/`, `.claude/skills/` (`*/SKILL.md`) | `repo_skill` |
| `openhandsnet.yaml` / `.openhands/config.yaml` | `repo_config` |
| `README*` (primeiros 8KB) | `repo_readme` |

Resultado persiste via `ConversationStateUpdateEvent(key="repo_context")`:
`{instructions_files:[…], skills:[{name,path,description}], config:bool}` —
e o conteúdo entra no `SystemPromptBuilder` (seção `## Repository context`
com as instruções + catálogo de repo-skills). Rescan manual:
`POST …/context/rescan` (re-executa e atualiza o state).

### RF-002 — Repo skills federadas

- `Skill` ganha `Source` (`db|repo`) + `RepoPath` (nullable; migration).
  Repo skills são **read-only**, escopo da conversa, nunca persistidas no DB
  global — carregadas por run pelo `RepoSkillProvider` (lê do workspace via
  executor, cache na memória da run).
- `ISkillService.ResolveForConversationAsync` passa a fundir:
  `db (user→org→global)` ∪ `repo` (path set convencional), dedupe por nome
  (db ganha), cap total `MaxSkills` (default 20, ordenado por trigger).
- `invoke_skill` aceita repo skills — conteúdo do `SKILL.md` + listagem dos
  arquivos do diretório da skill vão ao observation.
- `Skill.TriggerSpec` existente (always/keyword/task) vale igual para repo
  skills; keywords extraídas do frontmatter `description`.

### RF-003 — Novas tools built-in

`BuiltinTools` +`ToolRegistry`:

| Tool | Args | Comportamento |
|---|---|---|
| `search` | `{query, mode:"content"|"files", glob?, path?, max_results=50, context_lines?}` | `rg`/`rg --files` no sandbox (blueprint já instala; fallback `grep -R`/`find`); saída capped 100KB head+tail; `Risky=false` |
| `file_read` (v2) | `+ {offset?, limit?}` | linhas `offset..offset+limit` (default: inteiro até 2000 linhas); resposta inclui `{total_lines, truncated}` |
| `file_edit` modes | `+ {mode:"exact"(default)|"anchor"|"hashline"}` | `anchor`: casa por bloco `// anchor: <texto>`; `hashline`: `{line_hash, content}` estilo oh-my-pi (edição por linha endereçável — tolera drift); falha → erro com snippet do conteúdo próximo (dica de correção) |
| `web_fetch` | `{url, max_chars=16000}` | via backend (mesmo egress do worker, **não** sandbox); só `http(s)`, redige, markdown-lite strip; `Risky=false`. Domínios respeitam `Egress` config |

Schema de todos documentado com descrições de parâmetros completas
(JSON Schema `description` por prop — hoje várias são vagas).

### RF-004 — MCP schema fidelity + lifecycle events

- `McpToolCatalog.ToSchema` passa a usar o `inputSchema` real anunciado pelo
  servidor MCP (já recebido via `ListToolsAsync`) em vez de
  `{"type":"object"}` — o modelo finalmente vê os parâmetros.
- `IMcpClientService` emite `ConversationStateUpdateEvent` em mudanças de
  conexão: `key="mcp_servers"` `{server, state:"connecting"|"connected"|
  "failed"|"disconnected", tools:int, error?}` — UI mostra badge por server.
- MCP notifications (JSON-RPC `notifications/*` do C# SDK) →
  `agent_diagnostic` (SPEC-24) com `code="mcp_notification"`.
- `CallToolAsync` preserva `content` blocks MCP (text/image/resource) no
  `Observation` em vez de aplainar tudo para string.

### RF-005 — Tool result policy (alimenta SPEC-22)

- Todo `ToolResult` passa por `OutputMeta` central: cap por tool
  (`bash` 60KB, `search` 100KB, `file_read` 120KB, MCP 80KB — config
  `AgentLlm:ToolOutputCaps`), truncamento head+tail com marcador
  `[…truncated N chars…]`, campo `truncated` no observation.
- Observations grandes ganham `artifact_ref`: o payload completo é gravado
  no workspace (`/tmp/ohn-artifacts/<action-id>.txt`) e o observation leva
  preview + path — o agente pode `file_read` o restante **e** a SPEC-22
  pode podar sem perda.

### RF-006 — Introspecção

`GET /api/v1/app-conversations/{id}/tools` (auth read): catálogo efetivo
`[{name, description, origin:"builtin"|"mcp:<server>"|"skill", risky,
schema}]` — debug + base pro "tools panel" da UI (SPEC-26).

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| GET | `/api/v1/app-conversations/{id}/tools` | toolset efetivo |
| POST | `/api/v1/app-conversations/{id}/context/rescan` | re-discovery do RF-001 |

Events: `state_update` keys `repo_context`, `mcp_servers`. Fixtures: ambos.

## 4. Data model

Migration: `Skills` + `Source` (default `db`), `RepoPath` (null) —
`Source` denormalizado p/ filtros; repo skills nunca inseridas (colunas
servem ao tipo de retorno). Sem mudança em eventos.

## 5. NFR / Security

- Repo skills são dados **não confiáveis do repositório**: `SKILL.md` entra
  no system prompt como conteúdo citado (`<skill name=…>` fences) — nunca
  como instrução privilegiada; `Risky` de `invoke_skill` mantido.
- `web_fetch` respeita allowlist de egress e recusa `file:`/`gopher:` e IPs
  privados (SSRF guard) — reusa `EgressProxyConfig` quando allowlist ativa.
- `search` nunca segue symlink fora do workspace (`rg` já confina ao root).
- Descoberta ignora diretórios >1MB e segue cap de 64KB/arquivo.

## 6. Task Plan

1. `RepoDiscoveryService` (executor-based) + `repo_context` event +
   `SystemPromptBuilder` seção nova + testes.
2. `Skill.Source/RepoPath` migration + `RepoSkillProvider` + fusão no
   resolve + `invoke_skill` repo-aware + testes.
3. `search`, `file_read` v2, `file_edit` modes, `web_fetch` + OutputMeta +
   artifact_ref + testes unitários (sem Docker, via executor fake).
4. MCP: schema real, `mcp_servers` states, notifications→diagnostic,
   content blocks + testes com MCP server fake.
5. `GET …/tools` + `POST …/context/rescan` + fixtures.
6. Blueprint/runtime: garantir `ripgrep` na imagem (SPEC-17 — já presente?
   verificar; senão adicionar) + doc `docs/agent/discovery.md`.

## 7. Deliverables

- `Infrastructure/Discovery/RepoDiscoveryService.cs`,
  `Infrastructure/Skills/RepoSkillProvider.cs`, tools novas/estendidas,
  MCP fidelity, endpoints, migration, docs.

## 8. Referências

- oh-my-pi: skills dirs `.omp/skills`, `discoverAndLoadCustomTools`,
  capability providers, hashline edit mode, `rg` nativo —
  https://github.com/can1357/oh-my-pi (conceitos apenas).
- `afonsoft/open-webui` repo-skills + slash commands (SPEC-20261009).
- Anthropic Agent Skills spec (`SKILL.md` frontmatter) — formato já usado
  por `.claude/skills`.

## 9. Guardrails

- Repo content nunca vira instrução privilegiada fora das fences.
- MCP `inputSchema` real passa adiante sem reescrita.
- Caps de output preservam head+tail (contexto útil nas duas pontas).

## 10. DoD

- Repo com `.openhands/skills/x/SKILL.md` → skill invocável via
  `invoke_skill` e listada em `/tools`.
- `search` acha por conteúdo e por nome; `file_edit` hashline tolera drift.
- MCP tool com schema real aparece no catálogo do modelo.
- `dotnet test` verde; fixtures novas passam.
