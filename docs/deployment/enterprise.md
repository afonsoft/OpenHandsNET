# Deploy enterprise (self-hosted)

Stack completa: app + PostgreSQL + Keycloak (broker OIDC) + LiteLLM
(gateway de LLM). Autenticação empresarial chega na SPEC-08 — este compose
já sobe as dependências.

## Subir

```bash
cp .env.exemplo .env
# .env: POSTGRES_PASSWORD, KEYCLOAK_ADMIN_PASSWORD, LITELLM_MASTER_KEY,
#       OPENAI_API_KEY / ANTHROPIC_API_KEY (providers do LiteLLM)
docker compose -f docker-compose.yaml -f docker-compose.enterprise.yaml up -d --build
```

## Componentes

| Serviço | Porta | Função |
|---|---|---|
| app | 3032→8080 | OpenHandsNET (DeploymentMode=self_hosted) |
| postgres | interno | Dados (users, orgs, api keys, events) + db `litellm` |
| keycloak | 8081 | Broker OIDC — SAML/social atrás dele |
| litellm | 4000 | Gateway OpenAI-compat + **BYOR** (virtual keys por usuário, SPEC-50) |

## BYOR — LiteLLM (SPEC-50)

Cada usuário pode ter uma **virtual key** do proxy com budget próprio:

- `LlmProxy:Mode=hybrid` (default no compose): catálogo local OmniRoute
  segue ativo; quem tem key ativa roteia pelo proxy.
- `LlmProxy:Mode=byor`: strict — só modelos do `GET /models` do proxy;
  seleção fora da lista → diagnostic `byor_model_rejected` + run `Error`.
- Env: `LITELLM_MODE`, `LITELLM_DEFAULT_BUDGET_USD` (default 50).
- Config do proxy em `deploy/litellm.yaml` (`model_list` de exemplo =
  lista permitted); secrets `LITELLM_MASTER_KEY`/`OPENAI_API_KEY`/
  `ANTHROPIC_API_KEY` só por env.
- O db `litellm` é criado por `deploy/postgres-init/00-litellm-db.sh`
  no primeiro boot do volume postgres.
- Detalhes de API/segurança: `billing.md` (seção BYOR).

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
