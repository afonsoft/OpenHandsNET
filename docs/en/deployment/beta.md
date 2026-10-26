# Beta deploy (single host with Docker)

Minimal guide to run an OpenHandsNET beta on a VM/host with Docker for
testing. Portuguese version: [`../../deployment/beta.md`](../../deployment/beta.md).

## Prerequisites

- Docker + compose plugin on the host
- Access to an LLM/gateway (OmniRoute, OpenAI-compatible, remote Ollama)
- Image: published to `ghcr.io/afonsoft/openhandsnet` on `v*` tags
  (`.github/workflows/release.yml`) or built locally.

## Run

```bash
cp .env.exemplo .env
# edit .env: OPENHANDSNET_LLM__BaseUrl / ApiKey / Model (OmniRoute),
# OPENHANDSNET_Sandbox__PreviewTokenSecret (recommended)
docker compose -f docker-compose.beta.yaml up -d
# local image: docker compose -f docker-compose.beta.yaml up -d --build
```

App: `http://<host>:3032` — first registered user becomes admin.

## Sandbox access (required to create conversations)

`docker-compose.beta.yaml` mounts `/var/run/docker.sock` and sets
`Sandbox__DockerSocketMount=true` — required for the app to create
sandbox containers on the host. Trade-offs in
[`../../deployment/dind.md`](../../deployment/dind.md): weakest
isolation — acceptable for single-user beta; production should use
`tecnativa/docker-socket-proxy`, rootless Docker
(`DOCKER_HOST=unix:///run/user/<uid>/docker.sock`), or the remote
provider (`Sandbox__Provider=remote` + Runtime API).

## Network model (SPEC-115)

Only front+API are exposed. VNC/noVNC, IDE and sandbox services go
through the API's same-origin proxy
(`/api/v1/sandboxes/{id}/surfaces/{port}/`); terminal via SignalR.
**Do not publish sandbox container ports.** Details:
[`../../security/sandbox-networking.md`](../../security/sandbox-networking.md).

## Essential `.env` variables

| Var | Purpose |
|---|---|
| `OPENHANDSNET_LLM__BaseUrl` / `__ApiKey` / `__Model` | LLM gateway (e.g. OmniRoute) |
| `OPENHANDSNET_Sandbox__DefaultImage` | sandbox runtime image |
| `OPENHANDSNET_Sandbox__PreviewTokenSecret` | signed preview tokens |
| `OPENHANDSNET_Sandbox__EgressMode` | `unrestricted` (beta) / `allowlist` / `deny` |
| `OPENHANDSNET_PORT` | external port (default 3032) |

## Smoke test

1. `GET /api/v1/health` → 200
2. Register user → login
3. Create a conversation with a test repo → task `READY`
4. Open VNC/IDE — everything via `/api/v1/sandboxes/…/surfaces/` (no
   direct container access)
