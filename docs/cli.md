# `openhandsnet` CLI

CLI Spectre.Console para operar o OpenHandsNET contra um backend local ou
remoto. Credenciais salvas em `~/.openhandsnet/` (login/API key); sem
argumentos o CLI abre a TUI.

```bash
dotnet run --project cli/OpenHandsNET.Cli -- <comando>
# ou, após `dotnet tool install`:
openhandsnet <comando>
```

## Autenticação e servidor

| Comando | Descrição |
|---|---|
| `login` | Autentica no backend (browser ou API key). |
| `logout` | Remove credenciais salvas. |
| `cloud` | Comandos do modo cloud gerenciado. |
| `attach --url <u>` | Abre a TUI conectada a um backend remoto. |
| `serve` | Sobe a stack local (API + sandbox). |

Todos os comandos aceitam `--url <base>` para apontar a outro servidor
(default `http://localhost:8080`).

## Conversas

| Comando | Descrição |
|---|---|
| `run` | Nova conversa/start-task (`--repo`, `--branch`, `--model`). |
| `conversations list` | Lista conversas (`--page`, `--page-size`). |
| `conversations status <id>` | Estado da conversa/sandbox. |
| `conversations resume <id>` | Retoma uma conversa pausada/encerrada. |
| `conversations delete <id>` | Remove conversa (e artefatos, SPEC-34). |
| `tui` | UI interativa multi-pane (keymap: `~/.openhandsnet/tui.json`). |

## Descoberta e ferramentas

| Comando | Descrição |
|---|---|
| `models` | Catálogo OmniRoute (`GET /api/v1/models`). |
| `skills` | Skills federadas do repositório/agente. |
| `tools <id> [--all]` | Tools efetivas da conversa (pós-filtro de permissões). |
| `mcp list` | Servidores MCP configurados. |
| `mcp test <server>` | Probe de conexão (`POST /api/v1/mcp/test`). |

## Observabilidade

| Comando | Descrição |
|---|---|
| `stats` | Contagem de conversas + uso da org (quota/custo). |
| `export <id> [--out file]` | Dump JSONL dos **eventos duráveis** — deltas
efêmeros de streaming não são exportados; secrets são redigidos. |
| `doctor` | Diagnóstico do ambiente. |
| `doctor --preflight` | Gate de instalação enterprise: DNS de
`OPENHANDSNET_PUBLIC_DOMAIN`, egresso TCP/443 para `registry-1.docker.io` e
`ghcr.io`, daemon Docker, cgroup v2, ≥50 GiB livres, portas 8080/30000
livres. Exit code ≠ 0 se qualquer probe falhar. |

## Keymap da TUI

`~/.openhandsnet/tui.json` define aliases de slash-commands:

```json
{
  "aliases": {
    "/q": "/quit",
    "/m": "/model"
  }
}
```

## Exit codes

`0` sucesso · `1` erro genérico · `2` falha de preflight/probe (`doctor`).
