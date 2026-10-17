# Local deploy (OSS)

The default tier runs everything in a single container with embedded
SQLite.

## Requirements

- Docker 24+ (the app needs the daemon to spawn sandboxes — see
  `security.md`)
- 4 GB free RAM

## Bring it up

```bash
cp .env.exemplo .env          # adjust if needed
docker compose up -d --build  # app at http://localhost:3032
```

Verification:

```bash
curl -fsS http://localhost:3032/api/v1/ready   # 200 once migrated
curl -fsS http://localhost:3032/api/v1/health  # simple liveness
curl -fsS http://localhost:3032/metrics        # Prometheus counters
```

## Sandboxes

Each conversation creates an isolated Docker container mounting
`data/workspaces/<conversation-id>` at `/workspace` inside the sandbox.
Without the `docker.sock` mount, the app reports `docker: unreachable`
on `/api/v1/readiness` and conversations fail at WAITING_FOR_SANDBOX.

To enable sandbox execution, mount the socket:

```yaml
# docker-compose.override.yaml
services:
  app:
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
```

## Volumes

| Volume | Contents |
|---|---|
| `openhandsnet-data` | SQLite, DataProtection keys, workspaces |
| `openhandsnet-workspace` | Internal workspace bind |
