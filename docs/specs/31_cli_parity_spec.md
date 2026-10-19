# SPEC — 31 · CLI Parity (models / resume / stats / mcp / skills / export / attach / doctor preflight)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `cli-parity` |
| Type | `Feature / CLI` |
| Stack | `Spectre.Console · System.CommandLine` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed` (PR #72) |
| Ticket | #64 |
| Depends on | `06` (CLI), `20` (usage), `23` (`/models`), `24` (agent info), `25` (`/tools`) |
| Touches | `cli/OpenHandsNET.Cli/Commands/*`, `ApiClient` |

## 1. User Story

**As a** usuário de terminal
**I want** que `openhandsnet` cubra a superfície dos CLIs de referência
(opencode `models`/`attach`/`export`, claude-code `--resume`, codex `mcp`)
**So that** opero conversas/modelos/MCP/skills/usage sem abrir a UI.

## 2. Scope (RF)

### RF-001 — Novos comandos

| Comando | Backend | Saída |
|---|---|---|
| `models` | `GET /api/v1/models` (SPEC-23) | tabela provider/model/window/price |
| `conversations resume <id>` | `GET /events?from=…` + `POST /send-message` | replay tail + input loop |
| `stats` | `GET /api/v1/usage` | uso do usuário (SPEC-20) |
| `skills` | `GET /{id}/tools` (SPEC-25) filtrado | lista skills efetivas |
| `mcp list` / `mcp test <server>` | estado `mcp_servers` + `POST /mcp/{server}/test` | status + ping |
| `export <id>` | `GET /events` paginado | JSONL no stdout/`--out` |
| `attach --url <u>` | mesma sessão do `login` | TUI contra backend remoto |
| `doctor --preflight` | checks locais + `/readiness` | exit≠0 em falha |

### RF-002 — Doctor preflight (SPEC-32 RF-09)

Checks: DNS de `Deployment:PublicDomain` (quando set), reachability de
`registry-1.docker.io`/`ghcr.io`/providers configurados, docker socket,
cgroup v2, disco livre (≥50GB), portas livres. `--preflight` retorna
exit 1 na primeira falha (gate de CI/install).

### RF-003 — TUI keybindings

`tui` sai de `IsHidden`; `~/.openhandsnet/tui.json` mapeia teclas
(submit/cancel/scroll). Escopo mínimo: arquivo lido, keys aplicadas no
loop existente.

## 3. API Contract

Sem endpoints novos (reusa `/models`, `/usage`, `/tools`, `/readiness`,
`/events`, `/send-message`, `/mcp/*`).

## 4. Data model

Sem mudanças.

## 5. NFR / Security

- `attach`/`resume` usam token do `login` (nunca pede credencial nova).
- `export` redige secrets (mesmo `SecretRedactor` — CLI side).
- `doctor --preflight` falha rápido e determinístico.

## 6. Task Plan

1. Comandos `models`, `stats`, `skills`, `export` (fácil — GET).
2. `resume` (replay + loop), `attach` (TUI remoto), `mcp list/test`.
3. `doctor --preflight` + keybindings `tui.json`.
4. Testes de parsing/output; docs `docs/cli.md` atualizado.

## 7. Deliverables

Comandos novos, `ApiClient` methods, testes, doc.

## 8. Referências

- opencode CLI (`models`, `attach`, `session`, `stats`, `export`).
- claude-code (`--resume`, `mcp`), codex (`exec`, `resume`, `mcp`).

## 9. Guardrails

- Nenhum endpoint novo — o CLI consome só contratos HTTP/SignalR já
  publicados.
- `export` nunca vaza secrets.

## 10. DoD

- `openhandsnet models` lista o catálogo; `resume <id>` reabre conversa;
- `doctor --preflight` falha sem docker; `dotnet test` verde.
