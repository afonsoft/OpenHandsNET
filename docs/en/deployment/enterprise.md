# Enterprise deploy (self-hosted)

Full stack: app + PostgreSQL + Keycloak (OIDC broker) + LiteLLM (LLM
gateway). Enterprise authentication lands in SPEC-08 — this compose
already brings the dependencies up.

## Bring it up

```bash
cp .env.exemplo .env
# .env: POSTGRES_PASSWORD, KEYCLOAK_ADMIN_PASSWORD, LITELLM_MASTER_KEY,
#       OPENAI_API_KEY / ANTHROPIC_API_KEY (LiteLLM providers)
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

## BYOR — LiteLLM (SPEC-50)

Each user may hold a **proxy virtual key** with its own budget:

- `LlmProxy:Mode=hybrid` (compose default): the local OmniRoute catalog
  stays active; users with an active key route through the proxy.
- `LlmProxy:Mode=byor`: strict — only models from the proxy `GET
  /models`; a selection outside the list → `byor_model_rejected`
  diagnostic + run `Error`.
- Env: `LITELLM_MODE`, `LITELLM_DEFAULT_BUDGET_USD` (default 50).
- Proxy config at `deploy/litellm.yaml` (the example `model_list` is
  the permitted list); secrets `LITELLM_MASTER_KEY`/`OPENAI_API_KEY`/
  `ANTHROPIC_API_KEY` via env only.
- The `litellm` database is created by
  `deploy/postgres-init/00-litellm-db.sh` on the postgres volume's
  first boot.
- API/security details: `billing.md` (BYOR section).
