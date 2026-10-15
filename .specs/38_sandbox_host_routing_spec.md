# SPEC — 38 · Sandbox Host Routing (subdomain | path mode)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `sandbox-host-routing` |
| Type | `Infra / Networking` |
| Stack | `.NET 10 · YARP/reverse proxy · DNS wildcard` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Done` (PR #79; preview-token deferido → SPEC-40 RF-004) |
| Ticket | #77 |
| Depends on | `03` (sandbox), `04` (API), `14` (ops), `35`/`37` quando remotos |
| Touches | `SandboxAgentClient` (URL resolution), proxy layer, `doctor`, config |

## 1. User Story

**As a** operador que expõe serviços do sandbox ao usuário final
(ex.: terminal web, preview de app, VS Code web)
**I want** roteamento por **subdomínio** (`{sandbox}.runtime.<domain>`)
além do modo path atual
**So that** apps web dentro do sandbox funcionam sem path rewriting
(cookies, absolute URLs, WebSocket) — paridade com o
`runtime.<domínio>` wildcard do upstream enterprise (conceitos apenas).

**Contexto:** hoje o sandbox-agent só é alcançado internamente pelo
Api (port mapping Docker) — path interno fixo basta. Se qualquer
superfície do sandbox virar user-facing (browser terminal via URL do
sandbox, preview de app servida pelo sandbox), path-based quebra apps
com URLs absolutas; o upstream resolve com wildcard DNS + host routing.

## 2. Scope (RF)

### RF-001 — `Sandbox:RoutingMode`

```yaml
Sandbox:
  RoutingMode: internal | path | host   # default: internal (atual)
  PublicBaseDomain: runtime.example.com # requerido p/ host
```

- `internal`: comportamento atual — Api → sandbox via rede interna.
- `path`: `/sandbox/{id}/…` proxied pelo Api (já cobre casos simples;
  YARP transform strip-prefix).
- `host`: `{sandbox-id}.runtime.{domain}` → sandbox; wildcard cert
  (`*.runtime.example.com`) + wildcard DNS; o Api emite URLs públicas
  dos serviços do sandbox no formato host.

### RF-002 — Router/proxy

Componente `SandboxHostRouter` no Api (middleware antes do routing,
por `Host` header) **ou** ingress equivalente no provider k8s
(SPEC-37: `Ingress` por sandbox, `ingressClassName` configurável):

- resolve `sandbox-id` do host → lookup `ISandboxService` → upstream
  interno do pod/container;
- headers `X-Forwarded-*` corretos; WebSocket upgrade pass-through
  (sandbox-agent `/exec/stream` já é WS-friendly);
- timeout/limits iguais ao proxy de API existente; sem cache.

### RF-003 — Emissão de URL pública

`SandboxServiceUrls` (novo value object no response do sandbox /
`service_ports`): Api devolve URL absoluta conforme o modo —
`internal` → omite (não user-facing); `path` → `/sandbox/{id}/port/{p}`;
`host` → `https://{id}-{p}.runtime.example.com` (porta codificada no
label do subdomínio).

### RF-004 — Segurança do routing

- Todo request `host`-routed exige auth (mesma do dono da conversa —
  cookie/JWT; o router valida antes de proxear). Sem exceção: nenhuma
  porta de sandbox pública anônima por default; opt-in explícito por
  porta (`SandboxSpec.PublicPorts`) para preview público com token
  de preview rotativo.
- SSRF guard: router só aceita ids/labels válidos (`{ulid}` ou
  `{ulid}-{port}`), nunca host arbitrário.
- CORS restrito ao domínio do app.

## 3. API Contract

Sem endpoint novo; campos novos opcionais:

| Field | Where | Notes |
|---|---|---|
| `service_urls: {port: url}` | `GET /sandboxes/{id}` | emitidas conforme `RoutingMode` |
| `public_ports: [int]` | `SandboxSpec` | opt-in de portas públicas |

## 4. Data model

Sem migration (spec já é JSON coluna — novo campo opcional).

## 5. NFR / Security

- Wildcard cert via Let's Encrypt DNS-01 ou cert fornecido
  (`Deployment:WildcardCert`) — documentado; nunca self-signed em prod.
- DNS preflight: `doctor --preflight` valida `*.{PublicBaseDomain}`
  resolve + cert cobre wildcard quando `RoutingMode=host`.
- Rate limit no router (mesmo pipeline de limit do Api).
- Audit log de acesso a porta pública (SPEC-08 audit events).

## 6. Task Plan

1. `Sandbox:RoutingMode` config + `service_urls` no DTO.
2. `path` mode: YARP route + strip-prefix + WS.
3. `host` mode: host-header router + wildcard cert/docs.
4. Auth no router + `public_ports` opt-in + preview token.
5. `doctor` preflight DNS/cert; testes de router (host inválido →
   404; auth; WS upgrade; path rewriting).

## 7. Deliverables

`Api/Routing/SandboxHostRouter.cs`, config, YARP pipeline, doctor
checks, docs DNS/TLS, testes.

## 8. Referências

- Enterprise docs upstream: wildcard DNS/SANs
  `admin|app|auth|analytics|llm-proxy|runtime-api|runtime.<dom>`,
  sandbox routing `subdomain|path-based` (SPEC-32 §2 — conceitos).
- YARP docs (Microsoft).

## 9. Guardrails

- `internal` permanece default — nenhuma superfície pública nova sem
  opt-in de config.
- Router nunca proxia para host fora do registry de sandboxes
  (allowlist estrita, anti-SSRF).
- `public_ports` é lista explícita — default vazio.

## 10. DoD

- `RoutingMode=host` + wildcard DNS local (dnsmasq/hosts): app
  user-facing no sandbox acessível em `https://{id}.runtime.local`.
- Request sem auth → 401 antes do upstream.
- Host label inválido/fora do registry → 404, nunca proxy.
- `dotnet test` verde.
