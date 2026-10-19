# SPEC — 40 · Agent Server Surfaces (OpenAI Gateway + User-Facing Sandbox + ACP)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `agent-server-surfaces` |
| Type | `Feature` |
| Stack | `.NET 10 · Api (Minimal API) · SandboxAgent · runtime image · Client WASM` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed` (fatiada: #87 RF-001 gateway ✅#94, #88 RF-002 terminal ✅#95, #89 RF-003 vscode ✅#96, #90 RF-004 preview tokens ✅#93, #91 RF-005 ACP ✅#97, #92 RF-006 obs+docs ✅#98 — e2e live das surfaces = validação manual) |
| Ticket | `#81` |
| Depends on | `03` (sandbox), `17` (runtime image), `24` (activity protocol), `38` (host routing), `34` (deltas), `04` (API), `08` (auth/RBAC) |
| Touches | `src/OpenHandsNET.Api` (gateway + acp endpoints), `src/OpenHandsNET.SandboxAgent`, `runtime/` (imagem), `src/OpenHandsNET.Client`, `src/OpenHandsNET.Api/Routing/` |

## 1. User Story

**As a** usuário/integrador do OpenHandsNET
**I want** que o sandbox exponha as mesmas surfaces do `agent-server`
upstream (VSCode web, terminal web, gateway OpenAI-compatível, ACP)
**So that** o sandbox deixa de ser uma caixa-preta: posso inspecionar o
workspace no editor/terminal pelo browser e dirigir o agente a partir de
qualquer cliente OpenAI-compatível (incl. o open-webui do ecossistema)
ou editor ACP (Zed).

**Contexto:** o upstream V1 (`software-agent-sdk` ≥ 2026-10) promoveu o
`openhands.agent_server` a cidadão de primeira classe — REST/WebSocket
dentro do sandbox com endpoints bash/files/events/**VSCode/desktop** e
gateway `/v1/chat/completions` + `/v1/responses`; apps externos falam
com conversas via `/api/acp/conversations`. O nosso `SandboxAgent` é um
shim de exec/files/proc/activity — funcionalmente suficiente para o loop,
mas sem surfaces user-facing. A SPEC-38 (host routing `{id}-{port}.{domain}`
| path) já entrega o transporte; faltam as surfaces e o gate de acesso.
Esta SPEC usa **conceitos** apenas (licença Polyform do enterprise).

## 2. Scope (RF)

### RF-001 — Gateway OpenAI-compatible no Api

- `POST /v1/chat/completions` no `OpenHandsNET.Api`: cria uma conversa
  efêmera (ou anexa a `conversation_id` via header `X-Conversation-Id`),
  roda o agente e responde no formato OpenAI.
- `stream: true` → SSE `chat.completion.chunk` alimentado pelos
  `streaming_delta`/`durable` events da SPEC-34 (reuso do DeltaBatcher);
  `stream: false` → completion agregada ao fim da run.
- `model` no request mapeia para o role/agent profile default
  (`AgentLlm:DefaultProfile`); auth = `sk-*` existente (SPEC-08), com
  enforcement de plano/usage (SPEC-20) e permissões (SPEC-29).
- Tool calls do agente **não** vazam como tools OpenAI — o gateway é
  user-facing (resposta final + reasoning field quando o modelo expõe);
  eventos internos seguem no canal SignalR normal.
- Erros mapeados p/ formato OpenAI (`error.message/type/code`); run
  `failed` → chunk terminal + `data: [DONE]`.
- `/v1/responses` fica **fora do MVP** (formato OpenAI Responses é
  superset; follow-up se houver demanda).

### RF-002 — Web terminal surface (sandbox)

- Runtime image (SPEC-17 blueprint) ganha pacote opcional `ttyd` (ou
  equivalente) — flag `SANDBOX_TERMINAL=1` no blueprint habilita.
- `SandboxAgent` registra/repassa: `GET /surfaces` retorna
  `[{name:"terminal",port:7681,protocol:"ws"},…]` do que estiver ativo.
- Api publica a surface via `SandboxUrlBuilder` (SPEC-38):
  host `{id}-7681.{PublicBaseDomain}` ou path `/sandbox/{id}/port/7681`.
- Client WASM: botão "Terminal" no header da conversa abre iframe/nova
  aba apontando p/ a URL resolvida (somente com preview token — RF-004).

### RF-003 — VSCode web surface (sandbox)

- `code-server` opcional na runtime image (`SANDBOX_VSCODE=1`; fora do
  default — +~200MB); workspace montado como root folder.
- Mesma mecânica de `/surfaces` + URL routing; ícone "Open in editor"
  no client (SPEC-05/26) visível quando a surface está ativa.
- Desktop/VNC (upstream serve via agent-server) fica **fora do MVP** —
  footprint e UX não justificam; reavaliar se houver demanda.

### RF-004 — Preview tokens em surfaces públicas

- O middleware da SPEC-38 hoje faz 401 pre-proxy por auth de sessão —
  insuficiente p/ iframe/WS de terceiros (ttyd/code-server têm auth
  própria fraca ou nenhuma).
- `POST /api/v1/sandboxes/{id}/preview-token` (dono da conversa) → token
  opaco HMAC `{sandbox_id, port, exp≤15min, nonce}`; surfaces exigem
  `?token=` na primeira request; middleware valida antes do proxy e
  injeta no upstream; re-uso dentro da janela ok, reuso pós-exp → 401.
- Token nunca no event stream nem persistido em claro (hash p/ revogação).

### RF-005 — ACP minimal (Agent Client Protocol)

- Subset `POST /api/acp/conversations` (create+prompt inicial),
  `GET /api/acp/conversations/{id}`, `POST /api/acp/conversations/{id}/prompt`
  e `…/cancel`; eventos via SSE (session/update) — suficiente p/ editors
  (Zed) abrirem e dirigirem uma conversa.
- Auth `sk-*`; conversas ACP aparecem na UI normal como qualquer run.
- Full protocol (negotiation de capabilities, MCP-over-ACP) fica como
  follow-up.

### RF-006 — Observability + docs

- `sandbox_surface` diagnostic event quando surface liga/desliga;
  métricas `ohn_gateway_requests_total{model}`, latência do gateway.
- `docs/deployment/surfaces.md` (flags da imagem, DNS wildcard, preview
  token) + `docs/api/openai-gateway.md` (compat matrix de campos).

## 3. API Contract

```text
POST /v1/chat/completions          → OpenAI chat completion (SSE | JSON)
POST /api/v1/sandboxes/{id}/preview-token  → {token, expires_at}
GET  /sandbox/{id}/port/{p}/…      → já existe (38) + ?token= gate
GET  /surfaces  (sandbox-agent)    → surfaces ativas [{name,port,protocol}]
POST /api/acp/conversations        → {conversation_id, state}
GET  /api/acp/conversations/{id}   → {state, events?}
POST /api/acp/conversations/{id}/prompt|cancel
```

`chat/completions` aceita: `model`, `messages[]`, `stream`,
`X-Conversation-Id` (opt). Ignora (400 documentado): `tools`,
`response_format`, `n>1` — tool surface do agente não é exposta via
OpenAI tools (decisão: gateway é fim-de-linha user-facing, não proxy de
tool-calling arbitrário).

## 4. Data model

Sem migration. Preview tokens são stateless-HMAC (ou tabela
`preview_tokens` com hash+exp se revogação manual for exigida — decisão
na implementação; default stateless). Surface flags vivem no blueprint
`openhandsnet.yaml` (SPEC-17), não no banco.

## 5. NFR / Security

- Surface off por default (`SANDBOX_TERMINAL`/`SANDBOX_VSCODE` opt-in);
  gateway e ACP sempre on (auth `sk-*` + plan enforcement).
- Preview token: exp ≤15min, binding `sandbox+port`, nunca no event
  stream; SSRF guard da 38 segue (Guid-only labels).
- Egress allowlist (SPEC-19) inalterado — surfaces não abrem saída nova.
- Gateway: `model` validado contra catálogo/verified registry (SPEC-23);
  prompt injection via messages é problema do agente (SPEC-33 analyzer
  segue no caminho interno das tools).
- Audit: `agent_diagnostic code="sandbox_surface"` + request log do
  gateway (SPEC-13).

## 6. Task Plan

1. `POST /v1/chat/completions` (non-stream) → conversa efêmera + run +
   completion agregada; testes de contrato.
2. SSE streaming mapeando delta events → `chat.completion.chunk`.
3. `/surfaces` no SandboxAgent + ttyd opcional na imagem + URL builder +
   botão Terminal no client.
4. Preview token endpoint + gate no `SandboxHostRouter`.
5. code-server opcional na imagem + botão editor.
6. ACP subset + testes de fluxo create→prompt→cancel.
7. Docs + métricas + `dotnet test` verde.

## 7. Deliverables

`Api/Endpoints/OpenAiGatewayEndpoints.cs`, `Api/Endpoints/AcpEndpoints.cs`,
`Api/Endpoints/PreviewTokenEndpoints.cs`, `SandboxAgent` `/surfaces`,
`runtime/` blueprint flags + ttyd/code-server packages, `Client` botões,
`SandboxHostRouter` token gate, `docs/deployment/surfaces.md`,
`docs/api/openai-gateway.md`, testes unitários+integração.

## 8. Referências

- `docs.openhands.dev/sdk/guides/agent-server/*` — agent-server REST/WS,
  OpenAI gateway, surfaces (conceitos apenas).
- `docs.openhands.dev/enterprise/architecture` — runtime-api, image
  loader, integrations (conceitos apenas).
- `SPEC-32 §3` — gap table origem; esta SPEC cobre a linha "sandbox
  agent" + novos gaps do refresh (gateway/ACP/surfaces).
- SPEC-38 (routing), SPEC-17 (runtime image), SPEC-34 (deltas).

## 9. Guardrails

- **Licença**: conceitos apenas — nunca portar código
  `enterprise/`/`OpenHands-Cloud/` (Polyform).
- Surfaces opt-in por blueprint; nenhuma porta extra publicada sem flag.
- Preview token nunca persistido em claro nem emitido p/ não-dono.
- Gateway não vaza internals: tool calls/approvals seguem no SignalR,
  nunca como `tool_calls` OpenAI.
- `.github/workflows/` intocado; `main` só via PR.

## 10. DoD

- `curl -H "Authorization: Bearer sk-*" /v1/chat/completions` retorna
  completion válida (stream e non-stream) contra build local.
- Sandbox com `SANDBOX_TERMINAL=1`: `/surfaces` lista terminal, URL
  roteia com `?token=` válido e 401 sem ele.
- ACP create→prompt→cancel funciona e a conversa aparece na UI.
- Client mostra botões Terminal/Editor só quando a surface está ativa.
- `dotnet test` verde; docs publicadas.
