# SPEC — 06 · OpenHandsNET CLI

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `cli` |
| Type | `Feature` |
| Stack | `.NET 10 · Spectre.Console · System.CommandLine` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
| Depends on | `04_rest_and_signalr_api_spec` |
| Upstream | `OpenHands/OpenHands-CLI` (MIT) |

## 1. User Story

**As a** desenvolvedor/CI
**I want** um CLI `openhandsnet` com login device-flow, modo cloud e modo local
**So that** eu inicie/acompanhe conversas do terminal e incorpore o agente em
pipelines — como o `openhands` CLI faz para a plataforma original.

**Problem context:** o CLI upstream oferece `login` (OAuth2 Device Flow),
`logout`, `cloud -t|-f [--server-url]`, TUI local (Textual), `web`, `serve`,
`acp`. Em .NET entregamos o equivalente com Spectre.Console (TUI rico) e
System.CommandLine (parse). Auth armazena tokens em `~/.openhandsnet/`.

## 2. Scope

**In scope:**
- `openhandsnet login [--server-url]` — OAuth2 Device Flow (RFC 8628) + fallback
  `--api-key`; `logout`; credenciais em `~/.openhandsnet/credentials.json` (0600).
- `openhandsnet cloud -t "<task>" | -f <file> [--server-url] [--repo owner/repo]` —
  `POST /app-conversations` + poll/stream do start-task → imprime URL/ID.
- `openhandsnet run -t "<task>" [--repo .] [--exit-on-finish] [--json]` — modo
  headless CI contra servidor local/remoto.
- `openhandsnet` (TUI interativo): chat + eventos live via SignalR, confirmação
  inline, slash commands (`/new /status /exit`).
- `openhandsnet serve [--port] [--mount-cwd]` — sobe `OpenHandsNET.Api` local
  (equivalente a `openhands serve`; Docker opcional fase 2).
- `openhandsnet conversations list|status|delete` — utilidades.
- Env vars: `OPENHANDSNET_CLOUD_URL`, `OPENHANDSNET_API_KEY`, `OPENHANDSNET_SERVER_URL`.

**Out of scope:** `acp` mode (fase 3), `web` (textual-serve não existe em .NET —
UI web já é o Client WASM), publicação single-file/AOT dos binários (build normal
+ dotnet tool package), auto-update.

## 3. Technical Context

**Upstream:** `openhands_cli/{auth,cloud,tui,conversations,argparsers}` —
`device_flow.py`, `api_client.py`, `token_storage.py`, `textual_app.py`.
Docs `/usage/cli/cloud` verificadas (flags e env `OPENHANDS_CLOUD_URL`).
**Where:** `cli/OpenHandsNET.Cli/` → `Commands/` (por verbo), `Services/`
(`CloudApiClient`, `CredentialStore`, `SignalRListener`), `Tui/` (Spectre layout).

## 4. Requirements

### RF-001: Auth
- `login`: device flow → abre browser/`device_code` → poll token endpoint →
  salva `{server_url, access_token, refresh_token?, api_key?}` com permissão 0600.
- `--api-key sk-…` grava direto (uso em CI); `--server-url` default
  `https://app.openhandsnet.dev` `[A DEFINIR domínio]`, env `OPENHANDSNET_CLOUD_URL`.
- `logout [--server-url]` remove credencial.

### RF-002: Cloud conversations
- `cloud -t`: POST `/api/v1/app-conversations` (`initial_message`,
  `selected_repository`) → exibe task em spinner → ao `READY` imprime
  `<server>/conversations/{app_conversation_id}`.
- `--watch` anexa stream de eventos (SignalR) até `execution_status` terminal.

### RF-003: Headless/CI
- `run … --json` emite `{conversation_id, execution_status, events_url}`; exit
  code: `0` finished, `2` error/stuck, `3` timeout (`--timeout-min`).
- `--exit-on-finish` encerra ao terminal state; sem flag, anexa TUI.

### RF-004: TUI
- Painéis Spectre: log de eventos (colored por `source`), input, status line
  (`running`, elapsed, tokens); confirmação com prompt Approve/Reject;
  reconexão automática do hub.

### RF-005: Serve
- `serve` localiza `OpenHandsNET.Api` (path config/`--api-path` ou sibling
  publicado), sobe Kestrel em `--port` (default 8080), abre browser; `--mount-cwd`
  exporta `OPENHANDSNET_WORKSPACE=$PWD`.

**Invariants:** nunca logar tokens; erros HTTP → mensagem amigável + exit≠0;
`--json` desliga todo output rico (CI-safe).

## 5. API Contract

Consome SPEC 04 (`/app-conversations*`, `/events*`, `/hubs/conversations`) +
endpoints de auth de SPEC 08 (device flow: `POST /oauth/device/code`,
`POST /oauth/token`).

## 6. Critérios de Aceite

- [ ] `login` device flow completo persiste credencial; `cloud -t "x"` cria conversa e imprime URL.
- [ ] `run --json --exit-on-finish` retorna JSON e exit code correto por status.
- [ ] TUI mostra eventos live e aprova confirmação; Ctrl+Q sai sem stack trace.
- [ ] `OPENHANDSNET_API_KEY` funciona sem `login` (CI); `--server-url` aponta self-hosted.
- [ ] `serve` sobe app e serve UI em `localhost:8080`.

## 7. Task Plan

1. Skeleton System.CommandLine + `CredentialStore` + `CloudApiClient` (verify: `login --api-key` + `conversations list`).
2. Device flow + `login/logout` (verify: teste c/ servidor mock).
3. `cloud`/`run` + polling start-task + `--json`/`--watch` (verify: mock API).
4. TUI Spectre + hub listener + confirmação (verify: manual + snapshot de saída).
5. `serve` + empacotamento `dotnet tool`/single-file (verify: e2e local).
