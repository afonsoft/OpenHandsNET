# SPEC — 27 · CLI & Agent-Harness Comparative (opencode + peers)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `cli-opencode-comparative` |
| Type | `Analysis / Backlog (research spec)` |
| Stack | `reference survey → candidate RFs for follow-up SPECs` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed` — research/analysis spec (deliverable = análise + roadmap) |
| Ticket | `TBD` |
| Depends on | `06_cli_spec`, `09_agent_engine_spec`, `11_skills_hooks_spec`, `22..25` (contexto, routing, activity, tools) |
| Touches | `cli/OpenHandsNET.Cli`, `AgentRunner`, `ToolRegistry`, `ISkillService`, `IMcpClientService` |

## 1. User Story

**As a** maintainer do OpenHandsNET
**I want** um comparativo estruturado entre o nosso CLI/agent harness, os CLIs
de referência (sst/opencode e o fork `alltomatos/opencode`, claude-code,
gemini-cli, codex, aider) e as melhorias que o fork fez sobre o upstream
**So that** priorizamos o backlog de agente/CLI com evidência — sem copiar
código (licenças), só conceitos.

**Problem context:** temos hoje loop ReAct + context engine + routing +
activity protocol + toolset enriquecido (SPEC-22..25), mas o harness ainda é
single-agent, sem subagentes, sem memória cross-session e com permissões
binárias (confirmation mode on/off). O fork `alltomatos/opencode` é um
sst/opencode modificado exatamente nesses eixos — referência direta do que
adotar.

## 2. Fontes analisadas

| Fonte | O que é | Evidência |
|---|---|---|
| `sst/opencode` (upstream) | Agente TUI/CLI TS, client-server, multi-provider | docs públicas |
| `alltomatos/opencode` | **Fork** com roadmap pt-BR próprio (DEVELOPER-ROADMAP.md) | deepwiki `alltomatos/opencode` |
| `claude-code` (Anthropic) | CLI agentic, subagents `.claude/agents`, hooks, `CLAUDE.md` | docs públicas |
| `gemini-cli` (Google) | TUI, checkpoints, `GEMINI.md`, extensions | docs públicas |
| `codex` (OpenAI) | TUI + `exec` headless, approvals, sandbox modes | docs públicas |
| `aider` | REPL pair-programmer, repo-map, auto-lint/test/commit | docs públicas |
| `devin` CLI (Cognition) | `devin`, `devin -r` resume, remote sessions | uso interno |

> **Regra de licença**: análise conceitual apenas — nenhum código TS é portado;
> `sst/opencode` é MIT mas o fork pode divergir; implementamos em C# do zero.

## 3. Comparativo de CLIs

| Superfície | `openhandsnet` (hoje) | opencode | claude-code | gemini-cli | codex | aider |
|---|---|---|---|---|---|---|
| TUI | `tui` (hidden stub) | **default** | **default** | **default** | **default** | REPL |
| Headless | `run` | `run` | `-p` | `-p` | `exec` | `--message` |
| Server remoto | `serve` | `serve` + `attach` | — | — | — | — |
| Resume | — | `--continue`/`--session` | `--resume`/`--continue` | `/chat resume` | `resume` | `--restore` |
| Modelos | — | `models` | `--model` | `--model` | `-m` | `--model` |
| Agentes/perfis | — | `agent create/list` | `.claude/agents/*` | — | profiles `-c` | — |
| Skills | — | — | `/agents`, `/memory`, skills | `GEMINI.md` | — | — |
| MCP | — | `mcp add/list/auth` | `mcp` | extensions | `mcp` | — |
| Permissões | confirmation on/off | `allow/deny/ask` por tool+agent | `--permission-mode`, `--allowedTools` | `--yolo`/`--sandbox` | `--ask-for-approval`, `--sandbox` | auto-commit on/off |
| Sessões | `conversations list/status/delete` | `session` | logs | `/chat save` | `resume` | hist |
| Telemetria | `doctor` | `stats`, `export` | — | `/stats` | — | — |
| Upgrade | — | `upgrade` | — | — | — | pip |

**Gaps diretos do nosso CLI** (vs. melhor-de-classe): `resume`, `models`,
`stats`, `mcp`, `skills`, `export`, `attach`, TUI de verdade com keybindings
configuráveis.

## 4. O que o fork `alltomatos/opencode` melhorou sobre o upstream

Levantamento via deepwiki + `DEVELOPER-ROADMAP.md` do fork:

| # | Melhoria do fork | Como funciona (conceito) |
|---|---|---|
| F1 | **Orquestração de subagentes ("Batuta", Epic E04)** | Tool `task {description, prompt, subagent_type}` spawna sessão filha; tipos built-in: `build` (primário, full tools), `plan` (análise, sem edit), `general`, `explore` (read-only código), `scout` (read-only docs externas). `subagent_depth` (default 1) limita aninhamento; permissões herdadas + denies extras; sync/async; V2 externo (CLI/PTY), V3 DAG + terminal vivo |
| F2 | **Memória centralizada (Epic E11, "Breniac")** | `Memory.Service` com dois escopos: global (`data/memory/global`) e por-projeto (`data/memory/projects/<key>`); agente pode `remember`/`summarize`; promoção projeto→global com confirmação; digest re-gerado num `SKILL.md` global que entra no contexto |
| F3 | **Skills por paths/URLs** | `skills.paths` + `skills.urls` em `opencode.json`; permissão `skill` por agente |
| F4 | **Permissões `allow`/`deny`/`ask`** | global + por agente; gating sobre `read/edit/bash/glob/grep/webfetch/skill`; `plan` mode = denies de edição |
| F5 | **MCP auto-reconnect** | reconexão automática de servidores MCP ao restart/falha |
| F6 | **Config em camadas** | merge com precedência: managed → remote → global → project → env; resolução de plugins, chaves legadas |
| F7 | **Remote/mobile** | API desacoplada, pairing por token/QR, canal Telegram |
| F8 | **Polish TUI + CI multi-plataforma** | menus consistentes, ações destrutivas destacadas, `tui.json` keybindings; release matrix win/mac/linux |

## 5. Melhorias propostas p/ OpenHandsNET (candidate RFs)

Ordenadas por impacto no agente/contexto — cada uma vira SPEC própria.

### RF-A — Subagentes via tool `task` (prioridade 1)

Novo builtin `task {description, prompt, subagent_type, background?}`:

- **Presets**: `explore` (read-only: `search`, `file_read`, `bash` allowlist
  read-only), `scout` (`web_fetch` + `search`), `general` (tudo exceto
  `task`/`finish`), `plan` (sem tools de mutação).
- Implementação: `SubagentRunner` reusando `AgentRunner`/`ConversationHistoryBuilder`
  com `IToolRegistry` filtrado, **mesmo sandbox** (executor compartilhado),
  `parent_tool_call_id` ligando eventos; resultado comprimido num
  `ToolResult` de ~1-2KB (o grande ganho de contexto — exploração não
  polui a janela principal).
- `subagent_depth` (default 1) em `AgentLlmOptions`; subagente não pode
  spawnar outro.
- `background: true` → run assíncrono com `task_status`/`task_result` tools;
  eventos `run_progress`/`work_summary` do filho carregam `parent_tool_call_id`
  (a UI de SPEC-24/26 já renderiza agrupado).
- Modelo: usa `RoleModels` da SPEC-23 — `explore`/`scout` roteiam pro modelo
  "fast" quando configurado (OmniRoute), cortando custo.

### RF-B — Permissões `allow|deny|ask` por tool (prioridade 1)

- `AgentLlmOptions.Permissions` + override por conversa no start-task:
  `{tool|glob-pattern: "allow"|"deny"|"ask"}`; wildcard `mcp__*`.
- `deny` → tool some do catálogo do modelo; `ask` → passa pelo fluxo de
  confirmação existente (confirmation mode vira caso particular: default
  `ask` para `Risky=true`).
- Preset `plan` (read-only total) ativável por `POST /{id}/mode` —
  base para o subagente `plan` e para "review mode" na UI.

### RF-C — Memória centralizada (prioridade 2)

- `IMemoryService`: escopos `org` (DB `MemoryRecords`, admin-auditável) e
  `project` (arquivos `.openhands/memory/*.md` no workspace — versionáveis).
- Tool `memory {op:"remember"|"recall"|"summarize", text|query}`;
  `remember` no projeto escreve markdown datado; promoção projeto→org via
  `ask` (permissão do RF-B).
- Digest: `MemoryDigestSkill` gerada a cada N mudanças (cap 4KB) e injetada
  pelo pipeline de skills da SPEC-25 — **sobrevive à compaction**
  (SPEC-22) por entrar como seção estável do system prompt.

### RF-D — Skills via URLs + gating por nome (prioridade 3)

- `openhandsnet.yaml` / org settings aceitam `skills.urls`: fetch no
  start-task via `IWebFetchService` (SSRF guard da SPEC-25), cache por
  sandbox, cap 64KB; `skills.allow` restringe nomes invocáveis.

### RF-E — MCP auto-reconnect (prioridade 3)

- `IMcpClientService`: falha `connected→failed` agenda retry exponencial
  (1s→60s, máx 5); estado `reconnecting` no `mcp_servers` event (SPEC-25)
  + `agent_diagnostic` por tentativa.

### RF-F — Config efetiva em camadas (prioridade 3)

- `EffectiveAgentConfigResolver`: precedência documentada
  `admin-managed → org → repo (openhandsnet.yaml) → start-task → defaults`;
  exposta via `GET /{id}/config` para debug.

### RF-G — CLI parity (prioridade 2)

Novos comandos `openhandsnet`:

| Comando | Contrato |
|---|---|
| `models` | `GET /api/v1/models` (SPEC-23) tabela provider/window/price |
| `resume <id>` | reabre stream da conversa (event replay + input loop) |
| `attach --url` | aponta TUI p/ backend remoto (token via `login`) |
| `stats` | `/usage` do usuário (SPEC-20) |
| `skills` | catálogo efetivo (GET `/{id}/tools` quando dentro de conversa) |
| `mcp list/test` | estado `mcp_servers` + ping por server |
| `export <id>` | event stream → JSONL (SPEC-02 format) |
| `tui` | sai de hidden; `~/.openhandsnet/tui.json` p/ keybindings |

### RF-H — Remote pairing/mobile (deferido)

SignalR já é remoto; pairing por QR + canal Telegram ficam como item de
roadmap enterprise (sem RF agora — registrar como `Futuro`).

### RF-I — Release matrix do CLI (deferido)

Publish self-contained single-file `win-x64/osx-arm64/linux-x64` via CI —
**requer revisão humana** (toque em `.github/workflows/`).

## 6. NFR / Security

- Subagentes compartilham o sandbox → mesmo isolamento já existente; o
  filtro de tools é **server-side** (não é instrução pro modelo).
- `memory` persiste dados de usuário → auditável (SPEC-08/20); repo memory
  é untrusted content — entra citada, nunca privilegiada (regra SPEC-25).
- `skills.urls` passa pelo mesmo SSRF/egress do `web_fetch`.
- Permissões são enforcement do harness — independentes de system prompt.

## 7. Task Plan (desta SPEC — análise apenas)

1. ✅ Levantar superfície do nosso CLI + peers (seção 3).
2. ✅ Levantar deltas do fork via deepwiki (seção 4).
3. ✅ Mapear melhorias em RF-candidates com dependências (seção 5).
4. → Abrir SPECs filhas: **SPEC-28 subagents** (RF-A), **SPEC-29
   permissions** (RF-B), **SPEC-30 memory** (RF-C), **SPEC-31 CLI parity**
   (RF-G + partes de RF-E/F/D conforme priorizado).

## 8. Referências

- Fork analisado: https://github.com/alltomatos/opencode
  (`DEVELOPER-ROADMAP.md` pt-BR; conceitos apenas — implementação própria).
- Upstream: https://github.com/sst/opencode (MIT).
- Peers: claude-code docs, gemini-cli docs, codex CLI docs, aider docs.
- SPECs internas: 06 (CLI), 09 (agent engine), 22 (context), 23 (routing),
  24 (activity), 25 (tools/skills/discovery).

## 9. Guardrails

- Nenhum código TS do fork/upstream é portado — só conceitos documentados.
- Subagentes nunca elevam privilégio: permissões filho ⊆ permissões pai.
- Memória/skills remotas sempre citadas como untrusted content.

## 10. DoD

- Comparativo CLI + fork documentado com evidência (deepwiki).
- Backlog priorizado (RF-A..I) com mapeamento para SPECs filhas.
- Índice `.specs/README.md` atualizado; build order revisado.
