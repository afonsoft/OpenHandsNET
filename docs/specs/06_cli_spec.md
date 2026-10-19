# SPEC — 06 · OpenHandsNET CLI

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `cli` |
| Type | `Feature` |
| Stack | `.NET 10 · Spectre.Console · System.CommandLine` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed` |
| Ticket | [#7](https://github.com/afonsoft/OpenHandsNET/issues/7) (Epic [#1](https://github.com/afonsoft/OpenHandsNET/issues/1)) |
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

## 8. Deliverables — arquivos a criar/modificar

```text
cli/OpenHandsNET.Cli/OpenHandsNET.Cli.csproj        # PackAsTool, ToolCommandName=openhandsnet
cli/OpenHandsNET.Cli/Program.cs                     # System.CommandLine root
cli/OpenHandsNET.Cli/Commands/LoginCommand.cs       # device flow + --api-key
cli/OpenHandsNET.Cli/Commands/LogoutCommand.cs
cli/OpenHandsNET.Cli/Commands/CloudCommand.cs       # -t|-f|--watch|--server-url|--repo
cli/OpenHandsNET.Cli/Commands/RunCommand.cs         # headless CI (--json|--exit-on-finish|--timeout-min)
cli/OpenHandsNET.Cli/Commands/ConversationsCommand.cs # list|status|delete
cli/OpenHandsNET.Cli/Commands/ServeCommand.cs       # sobe Api local
cli/OpenHandsNET.Cli/Commands/TuiCommand.cs         # default (sem args)
cli/OpenHandsNET.Cli/Services/CredentialStore.cs    # ~/.openhandsnet/credentials.json (0600)
cli/OpenHandsNET.Cli/Services/DeviceFlowClient.cs   # RFC 8628
cli/OpenHandsNET.Cli/Services/CloudApiClient.cs     # REST typed client
cli/OpenHandsNET.Cli/Services/SignalRListener.cs    # eventos live
cli/OpenHandsNET.Cli/Tui/ConversationTui.cs         # Spectre layout + slash commands
cli/OpenHandsNET.Cli/Tui/EventRenderer.cs           # colored por source/kind
tests/OpenHandsNET.UnitTests/Cli/{CredentialStoreTests,CloudCommandTests,RunCommandTests}.cs
```

## 9. Referências e links

### Internas (repo)
- `docs/reference/openhands-upstream/cli-and-setup.md` — extratos verificados
  (flags `login|logout|cloud -t|-f`, env `OPENHANDS_CLOUD_URL`).
- `migration-plan/research/api-inventory.md` §8.
- `docs/specs/04_rest_and_signalr_api_spec.md` — endpoints consumidos.
- `docs/specs/08_enterprise_security_rbac.md` — device flow + `sk-*` server-side.

### Upstream — código-fonte (baseline `0403f1a`, MIT)
- Device flow: https://github.com/OpenHands/OpenHands-CLI/blob/0403f1a/openhands_cli/auth/device_flow.py
- Token storage: …/openhands_cli/auth/token_storage.py · API client: …/api_client.py
- Login/logout: …/openhands_cli/auth/{login_command,logout_command}.py
- Cloud mode: …/openhands_cli/cloud/{command,conversation}.py
- Argparsers (verbos/flags canônicos): https://github.com/OpenHands/OpenHands-CLI/tree/0403f1a/openhands_cli/argparsers
- TUI (Textual → inspiração p/ Spectre): https://github.com/OpenHands/OpenHands-CLI/tree/0403f1a/openhands_cli/tui

### Documentação oficial
- CLI cloud (`login|cloud`, `OPENHANDS_CLOUD_URL`): https://docs.openhands.dev/openhands/usage/cli/cloud
- Local setup (`openhands serve`): https://docs.openhands.dev/openhands/usage/run-openhands/local-setup

### Bibliotecas / padrões
- Spectre.Console: https://spectreconsole.net/ (prompts, live display, markup)
- System.CommandLine: https://learn.microsoft.com/dotnet/standard/commandline/
- Device Authorization Grant RFC 8628: https://www.rfc-editor.org/rfc/rfc8628
- SignalR .NET client: https://learn.microsoft.com/aspnet/core/signalr/dotnet-client
- `dotnet tool` packaging: https://learn.microsoft.com/dotnet/core/tools/global-tools-how-to-create
- Single-file publish: https://learn.microsoft.com/dotnet/core/deploying/single-file/overview

## 10. Guardrails de execução (Devin)

- `~/.openhandsnet/credentials.json` com `0600` (Unix) / ACL restrita (Windows).
- Nunca logar tokens/api keys — sanitizar em todo output (`***` últimos 4 chars).
- `--json` desliga rich output por completo (linha única por evento ou JSON final).
- Exit codes: `0` ok/finished · `1` erro genérico · `2` agent error/stuck · `3` timeout.
- Sem referência ao projeto Api (contratos HTTP apenas — ver SPEC 01 invariants).
- `--server-url` > `OPENHANDSNET_SERVER_URL` > `OPENHANDSNET_CLOUD_URL` > default.
- Device flow só existe se servidor `deployment_mode != oss` ou auth OIDC ativa;
  em OSS local, `--api-key` é o caminho.

## 11. Definition of Done

- [ ] `login`/`logout`/`cloud -t|-f`/`run`/`conversations`/`serve`/TUI funcionam.
- [ ] `run --json --exit-on-finish` com exit codes corretos por terminal state.
- [ ] TUI renderiza eventos live + confirmação; Ctrl+C sai limpo.
- [ ] `dotnet tool install` do pacote funciona (`openhandsnet --version`).
- [ ] `dotnet test` verde; SPEC `Done`; issue fechada pelo PR.
