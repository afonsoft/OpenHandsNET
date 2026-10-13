# SPEC — 07 · MCP Integration (Model Context Protocol)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `mcp-integration` |
| Type | `Feature / API` |
| Stack | `.NET 10 · MCP C# SDK (stdio + HTTP/SSE)` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Done` |
| Ticket | [#8](https://github.com/afonsoft/OpenHandsNET/issues/8) (Epic [#1](https://github.com/afonsoft/OpenHandsNET/issues/1)) |
| Depends on | `04_rest_and_signalr_api_spec` |

## 1. User Story

**As a** usuário do OpenHandsNET
**I want** conectar servidores MCP (tools, prompts, resources) às conversas do agente
**So that** o agente ganhe capacidades externas padronizadas — fetch, filesystem,
DBs, APIs — como no upstream, que expõe MCP nativamente.

**Problem context:** upstream tem três superfícies MCP: `agent_server/mcp_router.py`
(dentro do sandbox), `app_server/mcp/` (`mcp_test_router`, `mcp_tool_call_router`,
`mcp_oauth_router` + callback) e SDK `openhands.sdk.mcp` (config `MCPServer` map,
não o formato `.mcp.json` embrulhado). OpenHandsNET modela o mesmo: config por
usuário/conversa, OAuth para servidores remotos, tool-call auditado no EventStream.

## 2. Scope

**In scope:**
- `McpServerConfig` (mapa `name → {transport: stdio|sse|http, command|url, args, env, headers}` — flat map, paridade SDK).
- `IMcpClientService`: connect/list tools/call tool por conversa; lifecycle preso ao sandbox/conversa.
- `MCPTool` → expostas ao agente como tools nativas; invocações registradas como
  `ACPToolCallEvent`/`ActionEvent`+`ObservationEvent` (SPEC 02).
- API: `GET /api/v1/mcp/servers`, `POST /api/v1/mcp/test` (probe de conexão),
  `POST /api/v1/mcp/tool-call` (invocação direta), `GET /api/v1/mcp/oauth/authorize`,
  `GET /api/v1/mcp/oauth/callback` (paridade `mcp_oauth_*`).
- OAuth per-user com state + PKCE; tokens cifrados no secrets store (SPEC 08).
- UI: `McpSettingsPage` (CRUD de servidores, status de conexão, lista de tools).

**Out of scope:** OpenHandsNET **como** servidor MCP exposto a outros agentes
(fase 3); resources/prompts MCP além de tools; sampling MCP.

## 3. Technical Context

**Upstream:** `app_server/mcp/mcp_oauth_router.py` (authorize/callback/state),
`mcp_test_router.py`, `mcp_tool_call_router.py`; `agent_server/mcp_router.py`;
SDK `mcp/config.py` (`MCPServer` flat map + migração de `.mcp.json` wrapper).
**Where:** `Application/Mcp/` (contratos), `Infrastructure/Mcp/` (client SDK C#,
stdio process host, OAuth), `Api/Endpoints/McpEndpoints.cs`, `Client/Pages/McpSettings.razor`.

## 4. Requirements

### RF-001: Config
```csharp
public sealed record McpServerConfig(string Name, McpTransport Transport,
    string? Command, IReadOnlyList<string>? Args, Uri? Url,
    IReadOnlyDictionary<string,string>? Env, IReadOnlyDictionary<string,string>? Headers,
    bool Enabled = true);
public enum McpTransport { Stdio, Sse, Http }
```
Armazenada em `mcp_settings` por usuário (+ override por conversa).

### RF-002: Cliente
- `ConnectAsync(server)` por transporte; stdio spawna processo filho no sandbox
  quando há sandbox, senão no host (dev mode apenas — flag `allow_host_mcp`).
- `ListToolsAsync` → catálogo `{name, description, inputSchema}` cacheado por conversa.
- `CallToolAsync(conversationId, server, tool, args)` → resultado ou erro; timeout
  configurável (default 60s); todo call → `ACPToolCallEvent` (request) +
  `ObservationEvent` (result) no EventStream.
- Desconexão da conversa fecha conexões órfãs (owner = conversation).

### RF-003: OAuth
- `GET /mcp/oauth/authorize?server=X` → redirect ao authorization endpoint
  (state+PKCE persistidos); `GET /mcp/oauth/callback?code&state` → troca token →
  secrets store; `POST /mcp/oauth/disconnect`.
- Refresh automático antes de expirar; falha → `ServerErrorEvent` na conversa ativa.

### RF-004: Tool exposure ao agente
- Prefixo `mcp__{server}__{tool}` nos function calls do LLM (evita colisão).
- Descrições truncadas a 1024 chars (paridade regra upstream de skills).
- `confirmation_mode` pode exigir aprovação por servidor (`require_confirmation: true`).

### RF-005: API/UI
- `POST /mcp/test` → `{ok, tools_count, latency_ms, error?}` sem persistir.
- UI lista servidores com status `connected|error|auth_required` e botão
  "Authenticate" → fluxo OAuth.

**Invariants:** secrets/tokens só no secrets store; falha de um servidor não derruba
a conversa (tools dele saem do catálogo + evento de erro); stdio host-spawn só com
flag explícita.

## 5. API Contract

`GET /api/v1/mcp/servers` · `POST /api/v1/mcp/servers` · `DELETE /api/v1/mcp/servers/{name}` ·
`POST /api/v1/mcp/test` · `POST /api/v1/mcp/tool-call` ·
`GET /api/v1/mcp/oauth/authorize` · `GET /api/v1/mcp/oauth/callback` ·
`POST /api/v1/mcp/oauth/disconnect`.

## 6. Critérios de Aceite

- [ ] Servidor MCP stdio de teste (fetch fake) lista e executa tool numa conversa.
- [ ] Tool call vira `ACPToolCallEvent`+`ObservationEvent` consultável via `events/search`.
- [ ] Fluxo OAuth completo com provider de teste persiste token cifrado e reconecta.
- [ ] Servidor quebrado → status `error` + catálogo degradado, conversa segue.
- [ ] `POST /mcp/test` reporta `tools_count`/latência real.

## 7. Task Plan

1. `McpServerConfig` + storage + CRUD API (verify: unit tests).
2. Cliente stdio/SSE/HTTP + `ListTools`/`CallTool` (verify: servidor fixture).
3. EventStream wiring (call→eventos) + tool prefixing no agente (verify: integration).
4. OAuth authorize/callback/refresh (verify: IdP mock).
5. Settings UI + `mcp/test` probe (verify: E2E).

## 8. Deliverables — arquivos a criar/modificar

```text
src/OpenHandsNET.Domain/Mcp/{McpServerConfig,McpTransport}.cs
src/OpenHandsNET.Application/Mcp/IMcpClientService.cs    # connect/list/call per conversation
src/OpenHandsNET.Application/Mcp/IMcpOAuthService.cs     # authorize/callback/refresh
src/OpenHandsNET.Application/Mcp/McpToolCatalog.cs       # cache por conversa, prefixo mcp__s__t
src/OpenHandsNET.Infrastructure/Mcp/McpClientService.cs  # MCP C# SDK wrapper
src/OpenHandsNET.Infrastructure/Mcp/StdioMcpHost.cs      # spawn no sandbox (ou host c/ flag)
src/OpenHandsNET.Infrastructure/Mcp/McpOAuthService.cs   # state+PKCE + secrets store
src/OpenHandsNET.Api/Endpoints/McpEndpoints.cs           # servers CRUD + test + tool-call + oauth
src/OpenHandsNET.Client/Pages/McpSettingsPage.razor      # CRUD + status + tools list
tests/OpenHandsNET.IntegrationTests/Mcp/McpClientTests.cs        # fixture stdio server
tests/OpenHandsNET.UnitTests/Mcp/{McpToolCatalogTests,McpOAuthStateTests}.cs
```

## 9. Referências e links

### Internas (repo)
- `migration-plan/research/api-inventory.md` §1 (mcp routers) e §6.
- `.specs/02_event_stream_spec.md` — `ACPToolCallEvent`/`ObservationEvent`.
- `.specs/08_enterprise_security_rbac.md` — secrets store + OAuth tokens.
- `.specs/09_agent_engine_spec.md` — exposição das tools MCP ao loop do agente.

### Upstream — código-fonte (baseline `acca181`, MIT)
- SDK MCP config (flat map `name→server`): https://github.com/OpenHands/software-agent-sdk/blob/acca181/openhands-sdk/openhands/sdk/mcp/config.py
- Pacote MCP completo: https://github.com/OpenHands/software-agent-sdk/tree/acca181/openhands-sdk/openhands/sdk/mcp
- Testes MCP upstream (wire-compat, OAuth, security): https://github.com/OpenHands/software-agent-sdk/tree/acca181/tests/sdk/mcp
- Agent-server MCP router: …/openhands-agent-server/openhands/agent_server/mcp_router.py
- App-server MCP OAuth (Polyform — conceitos): …/693589e/openhands/app_server/mcp/

### Especificação MCP
- Spec oficial: https://modelcontextprotocol.io/specification (lifecycle, tools, transports)
- MCP C# SDK: https://github.com/modelcontextprotocol/csharp-sdk (stdio + HTTP/SSE client)
- OAuth 2.1 + PKCE RFC 7636: https://www.rfc-editor.org/rfc/rfc7636
- `.mcp.json` convenção (Claude Code): https://code.claude.com/docs/en/mcp (flat-map
  equivalente — upstream migra wrapper→flat, SPEC adota flat desde já)

### Bibliotecas / framework
- `System.Text.Json` schema→C#: https://learn.microsoft.com/dotnet/standard/serialization/
- Process spawning stdio: https://learn.microsoft.com/dotnet/api/system.diagnostics.process

## 10. Guardrails de execução (Devin)

- Config **flat map** `name → server` (paridade SDK); aceitar import de `.mcp.json`
  wrapper só via migração explícita (como upstream faz).
- stdio em sandbox por padrão; `allow_host_mcp=true` exigido para spawn no host
  (dev only) — jamais ligado por default.
- Tokens OAuth só no secrets store; log nunca inclui headers `Authorization`.
- Falha de um servidor = catálogo degradado + `ServerErrorEvent`, nunca crash da conversa.
- `CallToolAsync` timeout default 60s, configurável por servidor; todo call gera
  par `ACPToolCallEvent`→`ObservationEvent` (auditável).
- Servidor com `require_confirmation=true` → tools passam pelo confirmation mode
  da conversa (SPEC 09).

## 11. Definition of Done

- [ ] CRUD de servidores + conexão stdio/SSE/HTTP funcionais.
- [ ] `ListTools`/`CallTool` integrados ao catálogo do agente com prefixo `mcp__`.
- [ ] Fluxo OAuth completo (authorize→callback→refresh) com IdP de teste.
- [ ] `POST /mcp/test` probe real (tools_count + latência); UI de settings ok.
- [ ] `dotnet test` verde; SPEC `Done`; issue fechada pelo PR.
