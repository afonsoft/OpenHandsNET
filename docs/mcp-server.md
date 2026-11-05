# OpenHandsNET.Mcp — servidor MCP stdio

Expõe o OpenHandsNET como ferramenta para agentes externos (SPEC-124):
Claude Code, OpenCode, Devin ou qualquer cliente MCP dirige conversas,
runs e skills sem UI — o mesmo papel do `OpenWebUI.Mcp` no fork irmão.

## Configuração

```json
// .mcp.json (Claude Code / OpenCode / harness compatível)
{
  "mcpServers": {
    "openhandsnet": {
      "type": "stdio",
      "command": "openhandsnet",
      "args": ["mcp", "serve"],
      "env": {
        "OHN_API_URL": "http://localhost:8080",
        "OHN_API_KEY": "sk-..."
      }
    }
  }
}
```

`OHN_API_KEY` é uma chave `sk-*` emitida pelo app (Settings → API keys).
Sem a chave os tools falham fechado com mensagem explícita.

## Tools expostos

| Tool | Endpoint V1 |
|---|---|
| `ohn_list_conversations` | `GET /api/v1/app-conversations/` |
| `ohn_get_conversation` | `GET /api/v1/app-conversations/{id}` |
| `ohn_create_conversation` | `POST /api/v1/app-conversations/` (start-task async) |
| `ohn_send_message` | `POST /api/v1/app-conversations/{id}/send-message` |
| `ohn_list_events` | `GET /api/v1/conversation/{id}/events` |
| `ohn_search_events` | `GET /api/v1/conversation/{id}/events/search?q=` |
| `ohn_get_task_status` | `GET /api/v1/app-conversations/start-tasks/{id}` |
| `ohn_stop_run` | `POST /api/acp/conversations/{id}/cancel` |
| `ohn_list_skills` | `GET /api/v1/skills` |
| `ohn_list_mcp_servers` | `GET /api/v1/mcp/servers` |
| `ohn_list_models` | `GET /api/v1/models` |
| `ohn_attention_list` | `GET /api/v1/app-conversations/attention` |

Start-task é **assíncrono**: `ohn_create_conversation` retorna uma task
`WORKING` — faça polling com `ohn_get_task_status` até `READY` e leia o
`conversation_id`. Nunca trate o retorno como id síncrono.

## Bootstrap de contexto

`GET /gen-context/presets.json` (estático, sem auth) descreve as
capabilities do produto para um agente externo se orientar — endpoints,
modelo de runtime (eventos duráveis, sandbox, `*.agent.md`) e o bloco
`.mcp.json` acima.

## Verificação com cliente MCP (SPEC-129 RF-004)

Smoke manual do stdio (sem cliente externo):

```bash
dotnet build src/OpenHandsNET.Mcp -c Release
echo '{"jsonrpc":"2.0","id":1,"method":"initialize"}' \
  | dotnet src/OpenHandsNET.Mcp/bin/Release/net10.0/openhandsnet-mcp.dll
# → {"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2024-11-05",...}}
```

Com cliente real (Claude Code): adicionar o bloco `.mcp.json` acima e
rodar `claude mcp list` — `openhandsnet` deve aparecer `connected` e os
12 `ohn_*` listáveis em `/mcp`. Um `ohn_list_conversations` real exige o
app rodando (`dotnet run --project src/OpenHandsNET.Api`) + `OHN_API_KEY`
válida; sem credencial o tool responde `isError` com a mensagem — nunca
payload fabricado.

## Servidores MCP dentro do sandbox (SPEC-131)

O caminho inverso — o **agente consumindo** servidores MCP — executa
`stdio` dentro do container da conversa via `docker exec -i` (SPEC-07).
Variáveis `env` da config são encaminhadas como `-e K=V` para o processo
dentro do container.

### Runtime com browser

A imagem `runtime/Dockerfile` instala `playwright@1.64.1` +
`chrome-devtools-mcp@1.12.0` (pinned) e `playwright install --with-deps
chromium` em `PLAYWRIGHT_BROWSERS_PATH=/opt/ohn/ms-playwright`, com o
binário linkado em `/usr/local/bin/chromium`. ENV expostos:

| Variável | Valor |
|---|---|
| `CHROME_PATH` | `/usr/local/bin/chromium` |
| `PUPPETEER_EXECUTABLE_PATH` | `/usr/local/bin/chromium` |

Scripts do agente que usam Playwright (node/python) e qualquer MCP que
lance um browser funcionam sem setup adicional.

### Servidor builtin `chrome-devtools`

Servidor MCP pré-configurado (`McpSettingsService.BuiltinServers`),
presente em toda conversa salvo override:

```json
{ "name": "chrome-devtools", "transport": "stdio",
  "command": "chrome-devtools-mcp",
  "args": ["--headless", "--isolated",
           "--chromeArg=--no-sandbox",
           "--chromeArg=--disable-dev-shm-usage"] }
```

- **Desligar**: configurar servidor de mesmo nome com `enabled: false`.
- **Customizar**: mesmo nome + campos próprios — a config vence o builtin
  por inteiro (não há merge de args).
- Browser lançado **headless** e **isolado** (perfil descartável) no
  container — nada persiste entre runs.

### `browser` tool × `chrome-devtools` MCP

São implementações distintas por design: o `browser` builtin (SPEC-18)
roda **worker-side** via `PlaywrightBrowserService` no processo da API;
`chrome-devtools` roda **sandbox-side** como MCP — DevTools protocol,
network/perf tracing, acessível também a MCPs externos do agente.

## Drift guard

`python3 tools/check-mcp-tools-golden.py` compara o catálogo
`tools/list` com `tools/drift-baseline/mcp-tools.json` — mudança
intencional no catálogo → regenerar com `--write-baseline` e incluir no
PR (SPEC-129 RF-001).
