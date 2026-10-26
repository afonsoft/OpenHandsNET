# Sandbox networking — modelo de acesso (SPEC-115)

O OpenHandsNET expõe **apenas front + API** para o mundo externo. Tudo que
roda dentro do sandbox Docker (agente, VNC/noVNC, terminal, serviços de
usuário) é alcançado exclusivamente **pela API**, que atua como reverse
proxy. O browser do usuário nunca abre conexão direta com o container.

```
browser ──(cookie sessão)──▶ API ──▶ docker network (containerId:port)
   ▲          │                        │
   └── same-origin /api/v1/sandboxes/…/surfaces/{port}/ ──┘
```

## Caminhos suportados

| Recurso | Caminho | Transporte |
|---------|---------|-----------|
| VNC/noVNC | `/api/v1/sandboxes/{id}/surfaces/{port}/` | HTTP + WebSocket proxy |
| Terminal | `ConversationHub` (`/hubs/conversation`) | SignalR |
| IDE/VS Code | `/api/v1/sandboxes/{id}/surfaces/{port}/` | HTTP + WebSocket proxy |
| Service URLs | idem | HTTP proxy |

`SandboxSurfaceProxy` (`src/OpenHandsNET.Api/Routing/SandboxSurfaceProxy.cs`)
faz o forwarding de HTTP e upgrades de WebSocket para
`http://{containerId[..12]}:{port}` na rede Docker interna.

## RoutingMode

`Sandbox:RoutingMode` (default **`internal`**) controla o que
`GET /api/v1/app-conversations/{id}/surfaces` devolve:

- **`internal`** (default): sempre o path relativo same-origin
  `/api/v1/sandboxes/{id}/surfaces/{port}/`. O cookie de sessão cobre
  iframe e WebSocket — sem token na URL, nada exposto fora do host.
- **`path`/`host`** (opt-in, cenários enterprise/multi-host): URL pública
  construída por `SandboxUrlBuilder` + `?token=` assinado por
  `PreviewTokenService` (HMAC-SHA256, TTL ≤ 15 min, binding sandbox+porta).

O contrato é testado em `Spec115SurfaceUrlTests`: em `internal` o client
recebe **somente** paths relativos — nunca host/porta do container.

## Bind de portas

`DockerSandboxEngine` publica a porta do agente (`8000`) somente quando
`Sandbox:PublishAgentPort=true` (cenário Docker Desktop/daemon remoto):

- `AgentHost` ausente → `HostIP=127.0.0.1` (loopback only).
- `AgentHost` definido → `HostIP=0.0.0.0` + **warning no boot** (SPEC-115
  RF-003) — o único caminho suportado para clientes é o proxy da API;
  restrinja a porta no firewall do host.

`docker-compose.yaml` publica apenas `${OPENHANDSNET_PORT:-3032}:8080`;
o Docker daemon pode viver em outro host, mas as portas dos containers
nunca precisam ser expostas ao browser.

## Guard anti-regressão

`tools/check-no-direct-sandbox-url.sh` falha o build se o client abrir
`ws(s)://` literal ou montar URL com `containerId`/host de sandbox.
Rodar antes de PRs que tocam `src/OpenHandsNET.Client`.

## Configuração mínima segura

```yaml
# docker-compose / appsettings
Sandbox:
  RoutingMode: internal        # default — não setar em produção exposta
  PublishAgentPort: false      # default — só true p/ Docker Desktop remoto
  PreviewTokenSecret: <secret> # obrigatório só se RoutingMode != internal
```

Com isso, o único ataque surface externo é a API (cookie de sessão /
`sk-*` hash), e containers ficam inacessíveis a clientes.
