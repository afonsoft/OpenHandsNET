# SPEC — 124 · OpenHandsNET.Mcp + ACP — produto dirigível por agentes externos

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `mcp-acp-exposure` |
| Type | `API` |
| Status | `In implementation` |
| Ticket | #326 (Epic #317) — gap-analysis OpenWebUiCode 2026-11-04 |
| Scope | novo `src/OpenHandsNET.Mcp` + `src/OpenHandsNET.Acp` (ou hosting no Api), `OpenHandsNET.Api` (endpoints consumidos), `cli/` |

## 1. User Story

**As a** usuário rodando Claude Code/OpenCode/outro agente externo,
**I want** dirigir o OpenHandsNET (criar conversas, enviar mensagens,
ler eventos/runs, gerir skills/MCPs) via MCP stdio ou ACP, **so that**
o produto funcione como backend de sessões para qualquer harness —
o mesmo papel do `OpenWebUI.Mcp`/`OpenWebUI.Acp` no fork irmão.

## 2. Contexto / escopo

AS-IS: OpenHandsNET **consome** MCP de servidores externos
(`McpToolCatalog`, `mcp__*` — SPEC-07), mas não **expõe** MCP. Domain
já tem `AcpToolCallEvent` (evento) — verificar estado real do ACP.
Nenhum `gen-context` (bundle de contexto p/ agente externo).

OWUIC:

- `src/OpenWebUI.Mcp/` (`McpServer`, `OwuiApiClient`, `Program`):
  JSON-RPC 2.0 stdio newline-delimited — `initialize`, `ping`,
  `tools/list`, `tools/call`; tools expõem chats/runs/teams via API
  REST; protocolo `2024-11-05`.
- `src/OpenWebUI.Acp/` (`AcpHost`, `AcpDispatch`, `Program`): host
  Agent Client Protocol (clientes IDE tipo Zed) dirigindo o produto.
- `wwwroot/gen-context/presets.json`: presets de contexto
  consumíveis por agentes externos.

Direção: `OpenHandsNET.Mcp` = processo stdio fino sobre a **API pública
existente** (`/api/v1/app-conversations`, start-task assíncrono,
`search-events`, mensagens, skills, MCP configs) — hard rule: reusa
contratos V1, nunca inventa payload paralelo. Autenticação via
`sk-*` API key (header `Authorization: Bearer`, já suportado).

Fora de escopo: tools internos do agente (SPEC-121–123); UI
(SPEC-116–120); servidor MCP *dentro* do sandbox do agente.

## 3. Requisitos

- RF-001 — **`src/OpenHandsNET.Mcp`**: console .NET stdio; dispatch
  JSON-RPC (`initialize`→protocolVersion `2024-11-05`+capabilities,
  `ping`, `tools/list`, `tools/call`, `notifications` sem id → sem
  resposta); `McpApiClient` tipado sobre `HttpClient` (base URL +
  `sk-*` por env `OHN_API_URL`/`OHN_API_KEY` ou argv).
- RF-002 — **Tools expostos** (nomes `ohn_*`): `ohn_list_conversations`,
  `ohn_get_conversation`, `ohn_create_conversation`
  (`selected_repository`, `initial_prompt` → retorna task WORKING),
  `ohn_send_message`, `ohn_list_events`/`ohn_search_events`,
  `ohn_get_run_status`, `ohn_stop_run`, `ohn_list_skills`,
  `ohn_list_mcp_servers`, `ohn_get_usage` — apenas os que têm
  endpoint real hoje; verificar `api-inventory.md` na implementação
  e cortar os inexistentes (registrar quais faltam).
- RF-003 — **ACP**: avaliar primeiro o `AcpToolCallEvent` existente;
  `AcpHost`/`AcpDispatch` port do padrão OWUIC — session/new,
  prompt, tool-call routing p/ o agente OHN. Se o escopo ficar
  grande, dividir: MCP nesta SPEC, ACP vira SPEC-filha marcada aqui.
- RF-004 — **gen-context**: `GET /gen-context/presets.json` +
  presets de contexto estáticos (análogo ao OWUIC — descreve
  capabilities p/ agente externo bootstrapar).
- RF-005 — **CLI/docs**: `openhandsnet mcp serve` (Spectre) como
  entrypoint alternativo + `docs/` com bloco `.mcp.json` de exemplo
  (`{"type":"stdio","command":"openhandsnet","args":["mcp","serve"]}`)
  e `.devin`/claude-code usage.
- RF-006 — **Testes**: unit p/ `HandleLine` (parse error, notification,
  methods, tools/call mapping) + integração sobre `WebApplicationFactory`
  (list/create/message contra API real mockada de sandbox).

## 4. Acceptance

- [ ] `openhandsnet mcp serve` responde `initialize`/`tools/list`
      corretamente em stdio; `tools/call` de list_conversations
      retorna dados da API.
- [ ] Claude Code configurado com `.mcp.json` conecta e cria uma
      conversa real (evidência manual + teste integração).
- [ ] Nenhum endpoint novo inventado — todo tool mapeia para API V1
      existente; divergências documentadas na seção 5.
- [ ] `dotnet build`/`test` verde; projeto entra na `OpenHandsNET.slnx`.

## 5. API Contract

Consome apenas `/api/v1/**` público (start-task assíncrono:
`POST /app-conversations` retorna task — hard rule #2). Novo:
`GET /gen-context/presets.json` (estático, sem auth).
Schemas MCP publicados são derivados dos DTOs existentes — drift
guard: teste compara `tools/list` com snapshot (golden file).

## 6. Task Plan

1. Projeto `OpenHandsNET.Mcp` + dispatch JSON-RPC + `McpApiClient`.
2. Tools `ohn_*` sobre endpoints existentes + golden `tools/list`.
3. Entrypoint CLI + env config + docs `.mcp.json`.
4. `gen-context` presets.
5. ACP: investigação → port ou SPEC-filha.
6. Integração E2E manual (claude-code/.mcp.json) + testes.

## 7. Riscos / notas

- Start-task é **assíncrono** — `ohn_create_conversation` deve
  retornar a task e documentar polling (`ohn_get_run_status`);
  cliente MCP não pode fingir retorno síncrono de `conversation_id`.
- `sk-*` em env var local — nunca logar; `SecretRedactor` nos erros.
- ACP protocol evolui (Zed) — pinar versão no doc; se incompatível,
  entregar só MCP e registrar follow-up.
