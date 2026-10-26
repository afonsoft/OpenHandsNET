# Instalação — Docker

Guia de instalação via Docker / Docker Compose.
English: [../en/installation/docker.md](../en/installation/docker.md).

## Requisitos

- Docker Engine 24+ e plugin `docker compose` v2
- 4 GB RAM livres (app ~0,5 GB + sandboxes conforme o profile)
- Porta livre: `3032` (configurável via `OPENHANDSNET_PORT`)

## Início rápido (OSS, SQLite)

```bash
git clone https://github.com/afonsoft/OpenHandsNET.git
cd OpenHandsNET
cp .env.exemplo .env          # ajuste se necessário (nunca commite .env)
docker compose up -d --build  # app em http://localhost:3032
```

Verificação:

```bash
curl -fsS http://localhost:3032/api/v1/ready   # 200 quando migrado
curl -fsS http://localhost:3032/api/v1/health  # liveness
curl -fsS http://localhost:3032/metrics        # Prometheus
```

## Habilitar sandboxes (docker.sock)

Sem o socket Docker, o app sobe mas `/api/v1/readiness` reporta
`docker: unreachable` e conversas param em `WAITING_FOR_SANDBOX`.
Crie `docker-compose.override.yaml`:

```yaml
services:
  app:
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
```

e rode `docker compose up -d` de novo.

> O clamp de CPU do engine (`SPEC` de sandbox) ajusta `NanoCPUs` ao número
> de CPUs do host — um profile `large` (4 CPU) funciona em host de 2 CPU
> sem falhar no `docker create`.

## Profiles do compose

| Arquivo | Uso |
|---|---|
| `docker-compose.yaml` | OSS single-container + SQLite (default) |
| `docker-compose.postgres.yaml` | + PostgreSQL 16 |
| `docker-compose.runtime-split.yaml` | app + `openhandsnet-runtime-api` (sandboxes em processo separado) |
| `docker-compose.observability.yaml` | + OTel Collector + Prometheus + Grafana |
| `docker-compose.enterprise.yaml` | stack completo: Postgres + Keycloak + Valkey + LiteLLM |

Combine arquivos com `-f`:

```bash
# OSS + Postgres
docker compose -f docker-compose.yaml -f docker-compose.postgres.yaml up -d --build

# Runtime split (app sem docker.sock; runtime-api com docker.sock)
docker compose -f docker-compose.yaml -f docker-compose.runtime-split.yaml up -d --build

# Observabilidade
docker compose -f docker-compose.yaml -f docker-compose.observability.yaml up -d --build
```

## Enterprise (Postgres + Keycloak + Valkey + LiteLLM)

```bash
cp .env.exemplo .env
# ajuste POSTGRES_PASSWORD, KEYCLOAK_ADMIN_PASSWORD, GRAFANA_ADMIN_PASSWORD,
# OPENHANDSNET_Auth__Authority/ClientId/ClientSecret e OPENAI_API_KEY (LiteLLM)
docker compose -f docker-compose.enterprise.yaml up -d --build
```

Variáveis mínimas no `.env` para o tier enterprise:

```bash
OPENHANDSNET_DeploymentMode=self_hosted
OPENHANDSNET_Database__Provider=PostgreSql
OPENHANDSNET_Database__ConnectionString=Host=postgres;Database=openhandsnet;Username=openhandsnet;Password=${POSTGRES_PASSWORD}
OPENHANDSNET_Auth__Provider=oidc
OPENHANDSNET_Cache__Provider=redis
OPENHANDSNET_Cache__Redis__ConnectionString=valkey:6379
```

## Volumes

| Volume | Conteúdo |
|---|---|
| `openhandsnet-data` | SQLite/chaves DataProtection/workspaces |
| `openhandsnet-workspace` | bind dos workspaces de sandbox |

## Atualizar

```bash
git pull
docker compose up -d --build   # rebuild + restart; migrações rodam no boot
docker image prune -f
```

## Solução de problemas

| Sintoma | Causa / ação |
|---|---|
| `docker: unreachable` no readiness | `docker.sock` não montado — veja acima |
| `range of CPUs is from 0.01 to N` | corrigido pelo clamp de CPU do engine; atualize a imagem |
| Sandbox não inicia por egress | `EgressMode=allowlist` exige proxy — veja `docs/deployment/egress-allowlist.md` |
| `X-Runtime-Key` inválido | `OPENHANDSNET_RUNTIME_KEY` diverge de `OPENHANDSNET_Sandbox__RemoteApiKey` |

Mais: [../deployment/troubleshooting.md](../deployment/troubleshooting.md).
