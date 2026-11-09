# Install — Docker

Docker / Docker Compose install guide.
Português: [../../installation/docker.md](../../installation/docker.md).

## Requirements

- Docker Engine 24+ with `docker compose` v2
- 4 GB free RAM (app ~0.5 GB + sandboxes per profile)
- Free port: `3032` (`SANDCASTLE_PORT`)

## Quick start (OSS, SQLite)

```bash
git clone https://github.com/afonsoft/sandcastle.git
cd Sandcastle
cp .env.exemplo .env          # adjust if needed (never commit .env)
docker compose up -d --build  # app at http://localhost:3032
```

Verify:

```bash
curl -fsS http://localhost:3032/api/v1/ready   # 200 once migrated
curl -fsS http://localhost:3032/api/v1/health  # liveness
curl -fsS http://localhost:3032/metrics        # Prometheus
```

## Enable sandboxes (docker.sock)

Without the Docker socket the app starts but `/api/v1/readiness` reports
`docker: unreachable` and conversations stall at `WAITING_FOR_SANDBOX`.
Create `docker-compose.override.yaml`:

```yaml
services:
  app:
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
```

then run `docker compose up -d` again.

> The engine's CPU clamp adjusts `NanoCPUs` to the host CPU count — a
> `large` profile (4 CPU) works on a 2-CPU host without failing
> `docker create`.

## Compose profiles

| File | Use |
|---|---|
| `docker-compose.yaml` | OSS single-container + SQLite (default) |
| `docker-compose.postgres.yaml` | + PostgreSQL 16 |
| `docker-compose.runtime-split.yaml` | app + `sandcastle-runtime-api` (sandboxes in a separate process) |
| `docker-compose.observability.yaml` | + OTel Collector + Prometheus + Grafana |
| `docker-compose.enterprise.yaml` | full stack: Postgres + Keycloak + Valkey + LiteLLM |

Combine with `-f`:

```bash
# OSS + Postgres
docker compose -f docker-compose.yaml -f docker-compose.postgres.yaml up -d --build

# Runtime split (app without docker.sock; runtime-api with docker.sock)
docker compose -f docker-compose.yaml -f docker-compose.runtime-split.yaml up -d --build

# Observability
docker compose -f docker-compose.yaml -f docker-compose.observability.yaml up -d --build
```

## Enterprise (Postgres + Keycloak + Valkey + LiteLLM)

```bash
cp .env.exemplo .env
# set POSTGRES_PASSWORD, KEYCLOAK_ADMIN_PASSWORD, GRAFANA_ADMIN_PASSWORD,
# SANDCASTLE_Auth__Authority/ClientId/ClientSecret and OPENAI_API_KEY (LiteLLM)
docker compose -f docker-compose.enterprise.yaml up -d --build
```

Minimum `.env` for the enterprise tier:

```bash
SANDCASTLE_DeploymentMode=self_hosted
SANDCASTLE_Database__Provider=PostgreSql
SANDCASTLE_Database__ConnectionString=Host=postgres;Database=sandcastle;Username=sandcastle;Password=${POSTGRES_PASSWORD}
SANDCASTLE_Auth__Provider=oidc
SANDCASTLE_Cache__Provider=redis
SANDCASTLE_Cache__Redis__ConnectionString=valkey:6379
```

## Volumes

| Volume | Contents |
|---|---|
| `sandcastle-data` | SQLite/DataProtection keys/workspaces |
| `sandcastle-workspace` | sandbox workspace binds |

## Upgrade

```bash
git pull
docker compose up -d --build   # rebuild + restart; migrations run at boot
docker image prune -f
```

## Troubleshooting

| Symptom | Cause / action |
|---|---|
| `docker: unreachable` in readiness | `docker.sock` not mounted — see above |
| `range of CPUs is from 0.01 to N` | fixed by the engine CPU clamp; rebuild the image |
| Sandbox fails on egress | `EgressMode=allowlist` requires the proxy — see `docs/deployment/egress-allowlist.md` |
| invalid `X-Runtime-Key` | `SANDCASTLE_RUNTIME_KEY` differs from `SANDCASTLE_Sandbox__RemoteApiKey` |

More: [../../deployment/troubleshooting.md](../../deployment/troubleshooting.md).
