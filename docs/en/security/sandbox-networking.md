# Sandbox networking — access model (SPEC-115)

OpenHandsNET exposes **only the front-end + API** externally. Everything
running inside the Docker sandbox (agent, VNC/noVNC, terminal, user
services) is reached exclusively **through the API**, which acts as a
reverse proxy. The user's browser never opens a direct connection to the
container.

```
browser ──(session cookie)──▶ API ──▶ docker network (containerId:port)
   ▲          │                        │
   └── same-origin /api/v1/sandboxes/…/surfaces/{port}/ ──┘
```

## Supported paths

| Resource | Path | Transport |
|----------|------|-----------|
| VNC/noVNC | `/api/v1/sandboxes/{id}/surfaces/{port}/` | HTTP + WebSocket proxy |
| Terminal | `ConversationHub` (`/hubs/conversation`) | SignalR |
| IDE/VS Code | `/api/v1/sandboxes/{id}/surfaces/{port}/` | HTTP + WebSocket proxy |
| Service URLs | same | HTTP proxy |

`SandboxSurfaceProxy` (`src/OpenHandsNET.Api/Routing/SandboxSurfaceProxy.cs`)
forwards HTTP requests and WebSocket upgrades to
`http://{containerId[..12]}:{port}` on the internal Docker network.

## RoutingMode

`Sandbox:RoutingMode` (default **`internal`**) controls what
`GET /api/v1/app-conversations/{id}/surfaces` returns:

- **`internal`** (default): always the same-origin relative path
  `/api/v1/sandboxes/{id}/surfaces/{port}/`. The session cookie covers
  iframes and WebSockets — no token in the URL, nothing exposed off-host.
- **`path`/`host`** (opt-in, enterprise/multi-host scenarios): a public
  URL built by `SandboxUrlBuilder` plus `?token=` signed by
  `PreviewTokenService` (HMAC-SHA256, TTL ≤ 15 min, sandbox+port bound).

The contract is tested in `Spec115SurfaceUrlTests`: in `internal` mode the
client receives **only** relative paths — never the container's host/port.

## Port binding

`DockerSandboxEngine` publishes the agent port (`8000`) only when
`Sandbox:PublishAgentPort=true` (Docker Desktop / remote daemon scenario):

- `AgentHost` unset → `HostIP=127.0.0.1` (loopback only).
- `AgentHost` set → `HostIP=0.0.0.0` + a **startup warning** (SPEC-115
  RF-003) — the only supported client path is the API proxy; restrict
  the port at the host firewall.

`docker-compose.yaml` publishes only `${OPENHANDSNET_PORT:-3032}:8080`;
the Docker daemon may live on another host, but container ports never
need to be exposed to the browser.

## Regression guard

`tools/check-no-direct-sandbox-url.sh` fails if the client opens a literal
`ws(s)://` or builds a URL containing `containerId`/sandbox host. Run it
before PRs touching `src/OpenHandsNET.Client`.

## Minimal secure configuration

```yaml
# docker-compose / appsettings
Sandbox:
  RoutingMode: internal        # default — do not set in exposed prod
  PublishAgentPort: false      # default — only true for remote Docker
  PreviewTokenSecret: <secret> # required only if RoutingMode != internal
```

With this, the only external attack surface is the API (session cookie /
`sk-*` hash), and containers are unreachable by clients.
