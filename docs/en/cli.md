# `sandcastle` CLI

Spectre.Console CLI to operate Sandcastle against a local or remote
backend. Credentials live in `~/.sandcastle/` (login/API key); with no
arguments the CLI opens the TUI.

```bash
dotnet run --project cli/Sandcastle.Cli -- <command>
# or, after `dotnet tool install`:
sandcastle <command>
```

## Auth and server

| Command | Description |
|---|---|
| `login` | Authenticate to the backend (browser or API key). |
| `logout` | Remove saved credentials. |
| `cloud` | Managed-cloud mode commands. |
| `attach --url <u>` | Open the TUI connected to a remote backend. |
| `serve` | Bring up the local stack (API + sandbox). |

All commands accept `--url <base>` to target another server
(default `http://localhost:8080`).

## Conversations

| Command | Description |
|---|---|
| `run` | New conversation/start-task (`--repo`, `--branch`, `--model`). |
| `list` | List conversations. |
| `resume` | Resume a paused/finished conversation. |
| `delete` | Delete a conversation. |
| `export` | Export the event stream (JSON). |
| `stats` | Usage statistics. |

## Operations

| Command | Description |
|---|---|
| `models` | List OmniRoute catalog models/roles. |
| `mcp` | Manage MCP servers. |
| `skills` | List/create skills. |
| `doctor` | Health checks; `--preflight` runs install checks (DNS, egress, disk, ports, k8s with `--k8s`). |
| `eval` | Headless eval runs with fixtures (see `eval.md`). |
| `support-bundle` | Diagnostic bundle for support. |

See `sandcastle <command> --help` for flags. Português:
[../cli.md](../cli.md).
