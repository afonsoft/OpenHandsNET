# SPEC — 13 · DevOps, Deployment & Observability

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `devops-observability` |
| Type | `Infra` |
| Stack | `Docker · GitHub Actions · Serilog · OpenTelemetry` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Done` |
| Ticket | [#14](https://github.com/afonsoft/OpenHandsNET/issues/14) (Epic [#1](https://github.com/afonsoft/OpenHandsNET/issues/1)) |
| Depends on | `01_architecture_overview` |
| Touches | todas as SPECs (saúde, métricas, deploy) |

## 1. User Story

**As a** operador/mantenedor do OpenHandsNET
**I want** build CI verde, imagem Docker publicável, compose profiles por tier e
observabilidade estruturada (logs, traces, métricas, health)
**So that** o produto rode localmente com um comando e escale para
self-hosted/enterprise com diagnóstico real.

**Problem context:** upstream cloud usa K8s+Helm+Keycloak+LiteLLM+Runtime API
(Polyform). OpenHandsNET entrega o equivalente self-hosted pragmático:
Dockerfile multi-stage, compose profiles (oss → postgres → observability →
enterprise-deps), CI GitHub Actions, e OTel nativo do .NET — Helm fica como
documento de direção, não entrega.

## 2. Scope

**In scope:**
- **Dockerfile** raiz multi-stage: `sdk:10.0` build → `publish` Api+Client WASM →
  `aspnet:10.0` runtime; `.dockerignore`; non-root user; `HEALTHCHECK` em `/api/v1/ready`.
- **Compose profiles** (arquivos separados):
  - `docker-compose.yaml` — app + volume `data/` (SQLite+workspaces) — default OSS.
  - `docker-compose.postgres.yaml` — + PostgreSQL 17 (`Database__Provider=PostgreSql`).
  - `docker-compose.observability.yaml` — + otel-collector + Prometheus + Grafana
    (opcional, profile `observability`).
  - `docker-compose.enterprise.yaml` — postgres + Keycloak (OIDC) + LiteLLM
    (opcional gateway) — profile `enterprise`.
- **CI** `.github/workflows/ci.yml`: restore→build Release→test (unit+integration
  sem Docker)→format/lint→docker build (cache GHA); job `integration-docker`
  com service docker opt-in; job `client-tests` bUnit.
- **Release** `.github/workflows/release.yml` (tag `v*`): image `ghcr.io/afonsoft/
  openhandsnet:{tag,latest}` + `openhandsnet` dotnet tool package artifact.
- **Observability**: Serilog (`Serilog.AspNetCore`, enrichers
  `conversation_id|sandbox_id|org_id|event_id`, sinks console+file rolling+OTel);
  OTel `ActivitySource("OpenHandsNet")` spans (`conversation`, `start_task`,
  `agent_iteration`, `tool_call`, `sandbox_op`, `llm_call`) + `Meter` counters
  (`conversations_started`, `llm_tokens`, `tool_calls`, `sandbox_seconds`,
  `events_appended`); exporter OTLP opcional + Prometheus `/metrics`.
- **Health**: `/api/v1/health` (live), `/api/v1/ready` (db+docker+llm-config),
  `/api/v1/readiness` detalhado (admin, SPEC 08); startup probe aguarda
  `DatabaseMigrator`.
- `.env.exemplo` completo (todas as vars `OPENHANDSNET_*`, `LLM__*`, `Database__*`,
  `Git__*`, `Auth__*`, `Otel__*`); `docs/deployment/` guia de tiers.

**Out of scope:** Helm chart real (só doc de direção em `docs/deployment/kubernetes.md`),
Runtime API/warm-pool, Terraform, alertas managed, APM SaaS-specific config.

## 3. Technical Context

**Upstream (conceitos):**
- `OpenHands-Cloud/docs/ARCHITECTURE.md` — componentes cloud (Runtime API, warm
  runtimes, Image Loader, Keycloak, LiteLLM, blob store) → norte do tier enterprise.
- `enterprise/` entrypoints (`saas_server.py`, maintenance) → nossos hosted services.
- open-webui-net: `Dockerfile`, `docker-compose.yaml` (`:3032→:8080`),
  `docker-compose.full.yaml` (infra) — padrão a copiar.

**Where:** raiz (`Dockerfile`, `docker-compose*.yaml`, `.dockerignore`,
`.env.exemplo`), `.github/workflows/`, `src/OpenHandsNET.Api/Observability/`,
`docs/deployment/`.

## 4. Requirements

### RF-001: Dockerfile
- Stages: `build` (restore+build slnx), `publish` (dotnet publish Api → WASM
  embutido), `final` (runtime non-root `openhandsnet`, `ENV ASPNETCORE_URLS=http://+:8080`,
  `HEALTHCHECK CMD curl -f http://localhost:8080/api/v1/ready`).
- Docker daemon acessível ao app (para sandboxes) via mount `/var/run/docker.sock`
  — documentar implicação de segurança + alternativa `DOCKER_HOST` tcp/rootless.

### RF-002: Compose
- `docker compose up -d --build` default → app em `:3032` (`OPENHANDSNET_PORT`).
- Profiles: `--profile postgres|observability|enterprise` ou arquivos `-f`
  compostos (decisão: **arquivos separados**, paridade open-webui-net).
- Volumes nomeados: `openhandsnet-data` (SQLite+workspaces), `openhandsnet-keys` (DP keys).

### RF-003: CI
- Jobs: `build-test` (SDK 10, `dotnet restore|build -c Release|test`), `docker`
  (build+`docker compose config`), `contracts` (golden event fixtures).
- Cache: `actions/cache` NuGet + `docker/buildx` GHA cache.
- **Workflows são protegidos** — novos arquivos `.github/workflows/*` só com
  aprovação do usuário (guardrail do repo).

### RF-004: Observabilidade
- Enrichers obrigatórios em todo log: `conversation_id`, `sandbox_id`, `org_id`,
  `trace_id` (Serilog `LogContext.PushProperty` nos boundaries: endpoint/hub/runner).
- Spans: `start_task` root → `sandbox_op` → `agent_iteration{llm_call,tool_call}`.
- Métricas em `/metrics` (prometheus-net) + OTLP exporter via `Otel__Endpoint`.
- `LLMCompletionLogEvent` já emite usage (SPEC 02/09) → `UsageRecord` agregado
  por conversa/org (SPEC 08 usage dashboard).

### RF-005: Docs de deploy
- `docs/deployment/{local,postgres,enterprise,security}.md` + `kubernetes.md`
  (direção: mapping compose→Helm conceitos, warm pool, Runtime API — sem chart).

**Invariants:** imagem final roda sem SDK; `.env` nunca commitado; CI não depende
de LLM real nem Docker externo (jobs docker-dependentes são opt-in);
`docker.sock` só montado quando sandbox habilitado.

## 5. API Contract

`GET /api/v1/health` (200 sempre se processo vivo) · `GET /api/v1/ready`
(200/503 + `{checks:{db,docker,llm}}`) · `GET /api/v1/readiness` (admin, detalhes) ·
`GET /metrics` (Prometheus; auth opcional `Metrics__RequireAuth`).

## 6. Critérios de Aceite

- [ ] `docker compose up -d --build` → app saudável em `:3032`, `/api/v1/ready` 200.
- [ ] `-f docker-compose.postgres.yaml` sobe PG + app conectado (migrations aplicadas).
- [ ] CI verde em PR: build+test+contracts; docker job constrói imagem.
- [ ] Traces: `POST /app-conversations` gera span `start_task` com filhos `sandbox_op`.
- [ ] `/metrics` expõe `openhandsnet_events_appended_total` após criar conversa.
- [ ] `.env.exemplo` documenta 100% das vars consumidas pelo código (drift check script).

## 7. Task Plan

1. Serilog+enrichers+OTel wiring no `Program.cs` (verify: log JSON com correlação).
2. Health endpoints + `DatabaseMigrator` gating (verify: `/ready` states).
3. Dockerfile multi-stage + `.dockerignore` + compose OSS (verify: compose up).
4. Compose postgres/observability/enterprise + `.env.exemplo` completo (verify: config check).
5. CI workflow + contracts job (verify: PR verde) — **só após aprovação para tocar workflows**.
6. Release workflow + `docs/deployment/*` (verify: dry-run tag).

## 8. Deliverables — arquivos a criar/modificar

```text
Dockerfile  .dockerignore
docker-compose.yaml  docker-compose.postgres.yaml
docker-compose.observability.yaml  docker-compose.enterprise.yaml
.env.exemplo
src/OpenHandsNET.Api/Observability/{SerilogConfig,TelemetrySetup,MetricsRegistry}.cs
src/OpenHandsNET.Api/Endpoints/HealthEndpoints.cs
.github/workflows/ci.yml          # ⚠ protegido — aprovação explícita
.github/workflows/release.yml     # ⚠ protegido — aprovação explícita
docs/deployment/{local,postgres,enterprise,security,kubernetes}.md
scripts/check-env-drift.sh        # .env.exemplo vs env vars usadas no código
```

## 9. Referências e links

### Internas (repo)
- `.specs/01_architecture_overview.md` (boot/options) · `08` (readiness/admin) ·
  `10` (migrator) · `migration-plan/research/risk-assessment.md`.

### Upstream — conceitos
- Cloud architecture (Polyform): https://github.com/OpenHands/OpenHands-Cloud/blob/d242d12/docs/ARCHITECTURE.md
- Helm charts (direção K8s): https://github.com/OpenHands/OpenHands-Cloud/tree/d242d12/
- Compose do open-webui-net: https://github.com/afonsoft/open-webui/blob/5a7d920/docker-compose.yaml
  e https://github.com/afonsoft/open-webui/blob/5a7d920/docker-compose.full.yaml
- Dockerfile open-webui-net: https://github.com/afonsoft/open-webui/blob/5a7d920/Dockerfile
- `openhands serve`/docker run upstream: https://docs.openhands.dev/openhands/usage/run-openhands/local-setup

### Bibliotecas / framework
- Serilog AspNetCore: https://github.com/serilog/serilog-aspnetcore ·
  enrichers: https://github.com/serilog/serilog/wiki/Enrichment
- OTel .NET: https://opentelemetry.io/docs/languages/net/ ·
  ASP.NET instrumentation: https://github.com/open-telemetry/opentelemetry-dotnet-contrib
- Prometheus `prometheus-net`: https://github.com/prometheus-net/prometheus-net
- Health checks: https://learn.microsoft.com/aspnet/core/host-and-deploy/health-checks
- Docker multi-stage ASP.NET: https://learn.microsoft.com/aspnet/core/host-and-deploy/docker/building-net-docker-images
- Compose profiles: https://docs.docker.com/compose/how-tos/profiles/
- GH Actions .NET: https://docs.github.com/en/actions/use-cases-and-examples/building-and-testing/building-and-testing-net
- GitHub Actions SHA pinning (regra upstream enterprise): https://github.com/OpenHands/enterprise/blob/693589e/AGENTS.md
- ghcr.io publishing: https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry
- LiteLLM proxy: https://docs.litellm.ai/docs/simple_proxy · Keycloak: https://www.keycloak.org/documentation

## 10. Guardrails de execução (Devin)

- `.github/workflows/` **protegido** — não criar/editar sem aprovação explícita do usuário.
- Actions de terceiros fixadas por SHA completo + comentário de versão (regra upstream).
- Imagem final: non-root, sem SDK, sem `curl` extra salvo healthcheck (`wget`/dotnet probe ok).
- `/var/run/docker.sock` mount documentado em `docs/deployment/security.md` (risco +
  alternativas rootless/tcp). Nunca montar por default em exemplos "produção".
- `.env` fora do commit; `.env.exemplo` é a única fonte de verdade de vars (drift script).
- Métricas/logs nunca contêm secrets, prompts completos ou conteúdo de arquivos —
  só metadados (token counts, paths relativos).

## 11. Definition of Done

- [ ] Compose OSS/Postgres sobem saudáveis; health endpoints corretos.
- [ ] CI verde (build+test+contracts) e imagem docker buildada em CI.
- [ ] Spans/métricas visíveis (`/metrics` + OTLP opcional); logs correlacionados.
- [ ] `.env.exemplo` completo + `docs/deployment/` escrito.
- [ ] SPEC `Done`; issue fechada pelo PR (workflows atrás de aprovação).
