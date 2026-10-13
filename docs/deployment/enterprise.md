# Deploy enterprise (self-hosted)

Stack completa: app + PostgreSQL + Keycloak (broker OIDC) + LiteLLM
(gateway de LLM). Autenticação empresarial chega na SPEC-08 — este compose
já sobe as dependências.

## Subir

```bash
cp .env.exemplo .env
# .env: POSTGRES_PASSWORD, KEYCLOAK_ADMIN_PASSWORD, OPENAI_API_KEY (LiteLLM)
docker compose -f docker-compose.yaml -f docker-compose.enterprise.yaml up -d --build
```

## Componentes

| Serviço | Porta | Função |
|---|---|---|
| app | 3032→8080 | OpenHandsNET (DeploymentMode=self_hosted) |
| postgres | interno | Dados (users, orgs, api keys, events) |
| keycloak | 8081 | Broker OIDC — SAML/social atrás dele |
| litellm | 4000 | Gateway OpenAI-compat (modelos, rate limit, BYOK futuro) |

## Keycloak

1. Crie o realm `openhandsnet` (`http://localhost:8081`, admin).
2. Client `openhandsnet-web` (public, redirect `http://localhost:3032/*`).
3. `Auth__Authority=http://keycloak:8080/realms/openhandsnet` já configurado.

## Observabilidade

Combine com o arquivo de observabilidade:

```bash
docker compose \
  -f docker-compose.yaml \
  -f docker-compose.enterprise.yaml \
  -f docker-compose.observability.yaml up -d --build
```

Grafana em `:3000`, Prometheus em `:9090`, OTLP gRPC em `:4317`.
