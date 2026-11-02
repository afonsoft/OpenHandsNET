# SPEC — 115 · Sandbox access via API-only (proxy hardening)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `sandbox-api-only-access` |
| Type | `Hardening` |
| Status | `Completed` |
| Ticket | pedido direto do usuário (2026-10-26): "front não conecta direto no sandbox/VNC; tudo passa pela API (WS/SignalR); só front+api expostos" |
| Scope | `src/OpenHandsNET.Api`, `src/OpenHandsNET.Sandboxing`, `src/OpenHandsNET.Client`, `docker-compose*.yaml`, `docs/`, `tests/` |

## 1. User Story

**As a** operador do OpenHandsNET, **I want** garantia de que todo o
tráfego do cliente ao sandbox (HTTP, WebSocket, terminal, IDE, desktop
VNC) atravessa a API como reverse proxy autenticado, com as portas do
sandbox inalcançáveis de fora, **so that** somente front+API ficam
expostos e o sandbox nunca é atacável diretamente.

## 2. Contexto / escopo

Estado atual (verificado no código — já ~90% do caminho):

- `SandboxSurfaceProxy` (SPEC-113 RF-003) proxya HTTP **e** WebSocket
  para `{container}:{port}` via `GET /api/v1/sandboxes/{id}/surfaces/{port}/{**rest}`,
  autenticado por sessão ou preview token (SPEC-40 RF-004).
- `GET /app-conversations/{id}/surfaces` resolve URLs same-origin em
  `RoutingMode=internal` (default).
- Terminal: `ConversationHub` + `TerminalBridgeService` (SignalR) —
  nunca conexão direta.
- `docker-compose.yaml` publica só `${OPENHANDSNET_PORT}:8080` da API.
- `PublishAgentPort` (SPEC-35) publica 8000 no host — `127.0.0.1` por
  default; `0.0.0.0` apenas quando `AgentHost` configurado (modo split).

Gaps a fechar (esta SPEC):

1. **Prova de isolamento**: sem teste hoje garante que o client nunca
   recebe URL não-proxied de surface. Adicionar teste de contrato:
   `GET /app-conversations/{id}/surfaces` em `internal` retorna só URLs
   relativas `/api/v1/sandboxes/…` — nunca `http(s)://` externo nem
   host:porta do sandbox.
2. **Guard de config**: `PublishAgentPort` só pode ativar quando
   explicitamente configurado; documentar que `AgentHost` ≠ null implica
   exposição host-wide (validar/aviso no boot).
3. **Client**: audit de que nenhum componente monta URL de sandbox por
   conta própria (hoje `DesktopTab`/`IdeTab` só usam `surface.Url` —
   manter invariante em teste bUnit/unit).
4. **Docs**: página `docs/security/sandbox-networking.md` (en+pt)
   descrevendo o modelo: cliente → API (REST+SignalR+proxy) → sandbox
   (rede interna); matriz de superfícies e auth de cada caminho.

Fora de escopo: mTLS API↔sandbox, WAF/rate-limit do proxy, migração do
TerminalBridge para streaming real de PTY (já coberto por SPEC-09).

## 3. Requisitos

- RF-001: teste de contrato (integration) — em `internal`, resposta de
  `/app-conversations/{id}/surfaces` contém apenas paths relativos
  começando `/api/v1/sandboxes/`; nenhum host/porta do container exposto.
- RF-002: teste proxy — `GET /api/v1/sandboxes/{id}/surfaces/{port}/`
  retorna 401 sem sessão e sem token válido; 404 sandbox inexistente
  (cobertura parcial existe — completar se faltar); upgrade WS honra a
  mesma auth.
- RF-003: `PublishAgentPort` default `false` e bind `127.0.0.1` salvo
  `AgentHost` explícito; warning no startup quando `AgentHost` público
  + `PublishAgentPort` (registrar em log, sem quebrar boot).
- RF-004: invariante client — teste (unit/bUnit) garantindo que
  `DesktopTab`/`IdeTab` consomem `surface.Url` sem montar URLs do
  sandbox; grep-guard em CI opcional (`tools/check-no-direct-sandbox-url.sh`
  falha em `wss?://`/`vnc.html` hard-coded fora de testes/docs).
- RF-005: docs `docs/security/sandbox-networking.md` en+pt com diagrama
  e matriz de superfícies; `docs/comparison.md`/`README` atualizados.

## 4. Acceptance

- [ ] Teste de contrato prova: client só recebe URLs same-origin
      `/api/v1/…` para surfaces em todos os RoutingModes suportados por
      config default.
- [ ] Suítes verdes; nenhuma porta de sandbox publicada no compose
      default (audit script ou doc check).
- [ ] Documento de segurança publicado em en+pt.

## 5. API Contract

| Método | Rota | Auth | Notas |
|---|---|---|---|
| GET | `/api/v1/sandboxes/{id}/surfaces/{port}/{**rest}` | sessão ou `?token=` | existente — SPEC-113 (sem mudança) |
| GET | `/api/v1/app-conversations/{id}/surfaces` | sessão | existente — SPEC-40/113 (resposta validada por RF-001) |
| WS | `/api/v1/sandboxes/{id}/surfaces/{port}/websockify` (e ttyd WS) | mesma | upgrade proxied pelo `SandboxSurfaceProxy` |

## 6. Riscos / notas

- `host`/`path` RoutingModes (SPEC-38) são opt-in enterprise — a SPEC
  não os remove; garante que `internal` (default OSS) nunca vaza URL
  direta e que o modo host exige config consciente.
- Preview token é necessário para iframes em contextos sem cookie —
  mantido; escopo por sandbox+porta, TTL curto (já implementado).

## Delivered (2026-10-26)

- `AppConversationEndpoints.ResolveSurfaceUrl` extraído — `internal`
  (default) retorna só path same-origin `/api/v1/sandboxes/{id}/surfaces/{port}/`.
- `Spec115SurfaceUrlTests` (UnitTests): contrato same-origin em todos os
  casos internal; modos path/host levam `?token=` assinado.
- `DockerSandboxEngine.BuildHostConfig`: warning de boot quando
  `PublishAgentPort` + `AgentHost` → bind `0.0.0.0`.
- `tools/check-no-direct-sandbox-url.sh`: guard — client sem `ws(s)://`
  literal e sem URL com `containerId`/host de sandbox.
- `docs/security/sandbox-networking.md` + `docs/en/security/` — modelo
  de acesso (só front+API expostos; tudo via proxy/SignalR).
