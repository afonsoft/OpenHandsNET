# Enterprise deploy (self-hosted)

Full stack: app + PostgreSQL + Keycloak (OIDC broker) + LiteLLM (LLM
gateway). Enterprise authentication lands in SPEC-08 — this compose
already brings the dependencies up.

## Bring it up

```bash
cp .env.exemplo .env
# .env: POSTGRES_PASSWORD, KEYCLOAK_ADMIN_PASSWORD, OPENAI_API_KEY (LiteLLM)
docker compose -f docker-compose.yaml -f docker-compose.enterprise.yaml up -d --build
```

## Components

| Service | Port | Role |
|---|---|---|
| app | 3032→8080 | OpenHandsNET (DeploymentMode=self_hosted) |
| postgres | internal | Data (users, orgs, api keys, events) |
| keycloak | 8081 | OIDC broker — SAML/social behind it |
| litellm | 4000 | OpenAI-compat gateway (models, rate limit, future BYOK) |

## Keycloak

1. Create the `openhandsnet` realm (`http://localhost:8081`, admin).
2. Client `openhandsnet-web` (public, redirect
   `http://localhost:3032/*`).
3. `Auth__Authority=http://keycloak:8080/realms/openhandsnet` is already
   configured.

## Observability

Combine with the observability file:

```bash
docker compose \
  -f docker-compose.yaml \
  -f docker-compose.enterprise.yaml \
  -f docker-compose.observability.yaml up -d --build
```

Grafana at `:3000`, Prometheus at `:9090`, OTLP gRPC at `:4317`.
