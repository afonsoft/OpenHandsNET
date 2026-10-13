# SPEC — 14 · Enterprise Operations & Sandbox Hardening

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `enterprise-operations` |
| Type | `Infra + Feature` |
| Stack | `.NET 10 · Docker.DotNet · EF Core (PG) · Spectre.Console` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Draft` |
| Ticket | `[a criar via create-issues]` (Epic [#1](https://github.com/afonsoft/OpenHandsNET/issues/1)) |
| Depends on | `03_runtime_sandbox`, `08_enterprise_security_rbac`, `10_persistence_efcore`, `13_devops_observability` |
| Touches | Sandbox (03), Persistence (10), CLI (06), API (04), Observability (13) |

## 1. User Story

**As a** operador self-hosted do OpenHandsNET
**I want** limites de recursos por sandbox, Docker-in-sandbox opt-in com
isolamento de rede, Postgres externo bem operado, diagnóstico em um comando
e warm pool de sandboxes
**So that** o produto se aproxime do tier Enterprise do upstream em
capacidade operacional — sem portar código Polyform (conceitos apenas).

**Problem context:** as docs enterprise do upstream (enterprise-vs-oss,
architecture, sizing-guide, docker-in-sandbox, external-postgres,
troubleshooting) descrevem capacidades que as SPECs 01–13 ainda não cobrem:
sizing por sandbox, DinD/egress policy, requisitos de PG gerenciado,
support-bundle e warm pool. Esta SPEC fecha essas lacunas em cima do que já
está aprovado, sem reescrever contratos existentes.

## 2. Scope

**In scope:**
- **Sandbox sizing** (SPEC 03 extension): `HostConfig` com `NanoCPUs`,
  `Memory`, `PidsLimit` por perfil (`small|medium|large`) configurável por
  conversa e por org; defaults documentados a partir do sizing-guide upstream.
- **Docker-in-sandbox (opt-in)**: flag `Sandbox__EnableDinD` — sidecar
  `docker:dind` por sandbox com rede interna isolada, **ou** mount de
  `docker.sock` (explícito, documentado como fraco isolamento). Egress
  policy por sandbox: `Sandbox__EgressMode` = `unrestricted|allowlist|deny`
  (allowlist via rede docker dedicada + regras — sem proxy externo).
- **External Postgres hardening** (SPEC 10 extension): matriz de envs
  `Database__*` documentada (SSL mode, pooling `MinPoolSize/MaxPoolSize`,
  `CommandTimeout`, keepalive), checklist de requisitos do PG gerenciado
  (extensions, role, schema ownership) em `docs/deployment/external-postgres.md`.
- **Diagnóstico em um comando**: `openhandsnet doctor` (CLI) — probe de API,
  DB, docker daemon, LLM gateway, disco de workspaces; saída tabela +
  `--json`. Endpoint admin `GET /api/v1/support-bundle` → zip com
  `readiness` detalhado, logs recentes (redacted), versões, counts de
  entidades, config efetiva sem secrets.
- **Warm pool (opcional)**: `Sandbox__WarmPoolSize` — N sandboxes
  pré-criados ociosos reutilizados no `start-task` (latência de cold start
  cai para attach). Label `openhandsnet.pool=warm`; sweep devolve ao pool.
- **Usage metering surface**: `GET /api/v1/orgs/{id}/usage` agregando
  `UsageRecord` (tokens, sandbox_seconds, runs) — base do billing do
  SPEC 08 sem acoplar cobrança.

**Out of scope:** K8s/Helm real, Runtime API upstream, warm pool em múltiplos
hosts, cobrança/invoice, autoscaling do app.

## 3. Technical Context

**Upstream (conceitos — Polyform, não portar código):**
- `docs.openhands.dev/enterprise/sizing-guide` — perfis de vCPU/RAM por
  sandbox e capacidade por host → nossos perfis `small|medium|large`.
- `docs.openhands.dev/enterprise/docker-in-sandbox` — DinD com isolamento
  forte + network isolation → nosso `EnableDinD` + `EgressMode`.
- `docs.openhands.dev/enterprise/external-postgres` — PG gerenciado externo
  com requisitos mínimos → nossa doc + env matrix.
- `docs.openhands.dev/enterprise/troubleshooting` — support-bundle CLI +
  health checks → `openhandsnet doctor` + `/support-bundle`.
- `docs.openhands.dev/enterprise/architecture` — warm runtimes/Image Loader →
  warm pool simplificado (containers pré-criados, sem registry próprio).

**Where:** `src/OpenHandsNET.Infrastructure/Sandbox/` (HostConfig, pool,
egress), `src/OpenHandsNET.Infrastructure/Persistence/` (PG options doc),
`cli/OpenHandsNET.Cli/Commands/` (`doctor`, `support-bundle`),
`src/OpenHandsNET.Api/Endpoints/` (support-bundle, usage), `docs/deployment/`.

## 4. Requirements

### RF-001: Sandbox sizing
- `SandboxProfile` (`small|medium|large`, default `medium`) mapeia para
  `NanoCPUs`/`Memory`/`PidsLimit` no `HostConfig` do Docker.DotNet.
- Perfil resolvido em: conversa (`app_conversations.sandbox_profile`) →
  org default → global `Sandbox__Profile`. Valor inválido → 400.
- `docker-compose` nunca muda os limites do app — só do sandbox.

### RF-002: DinD + egress
- `Sandbox__EnableDinD=true` cria sidecar `docker:dind` na rede interna do
  sandbox; o cliente interno aponta `DOCKER_HOST=tcp://localhost:2375`.
- Alternativa documentada (`docs/deployment/security.md`): mount de
  `docker.sock` é o caminho fraco — nunca default.
- `Sandbox__EgressMode=allowlist` + `Sandbox__EgressAllow` (hosts/CIDR,
  `;`-separado) aplicados na rede docker do sandbox; `deny` = `internal: true`.
- Invariante: sem DinD nem egress config, comportamento = SPEC 03 atual.

### RF-003: External Postgres
- Doc `docs/deployment/external-postgres.md`: requisitos (PG ≥15, role com
  CREATE/DDL no schema, `pgcrypto` opcional, SSL `require|verify-full`),
  string de conexão completa, pooling recomendado por réplica.
- `GET /api/v1/ready` reporta `db` com latência e erro curto (sem connstring).

### RF-004: `openhandsnet doctor` + support-bundle
- `doctor` probes: API `/health`+`/ready`, handshake auth, DB (via readiness
  admin quando autenticado), `docker info` local (para `serve`), LLM gateway
  reachability. Exit codes: `0` ok, `1` falha, `2` parcial.
- `GET /api/v1/support-bundle` (admin ou `sk-*` com scope admin): zip
  `manifest.json` (versões, uptime, counts), `readiness.json`, `config.json`
  (env efetiva com valores de secret substituídos por `***`), `logs.tail`
  (últimas ~500 linhas redacted). Sem conteúdo de prompts/eventos.

### RF-005: Warm pool
- `Sandbox__WarmPoolSize=N>0`: background service mantém N sandboxes do
  perfil default em `Running` rotulados `openhandsnet.pool=warm`.
- `start-task` consome um warm (rename labels para a conversa) ou cria frio;
  sweep/reap devolve ou repõe. `GET /api/v1/sandboxes` marca origem
  `pool: warm|cold` (campo novo, backward-compat).
- Pool desligado por default; containers órfãos de restart são prunados
  (o pool não é resumido — recriado).

### RF-006: Usage metering
- `UsageRecord` agrega por conversa/org: `runs`, `llm_tokens` (de
  `LLMCompletionLogEvent`), `sandbox_seconds` (uptime acumulado),
  `events_appended`. Endpoint `GET /api/v1/orgs/{id}/usage?from&to`
  (org admin) + `GET /api/v1/chats/{id}/usage` (dono).

**Invariants:** nada aqui liga por default em OSS (pool=0, DinD=off,
egress=unrestricted); secrets nunca no bundle; perfis de sandbox são
upper bounds, não reservas.

## 5. API Contract

`GET /api/v1/support-bundle` → 200 zip (admin) ·
`GET /api/v1/orgs/{id}/usage?from&to` → `{runs,llm_tokens,sandbox_seconds,events}` ·
`GET /api/v1/chats/{id}/usage` · `GET /api/v1/sandboxes` ganha `pool` ·
`POST /app-conversations` aceita `sandbox_profile` opcional ·
CLI: `openhandsnet doctor [--json]` · `openhandsnet support-bundle [-o file.zip]`.

## 6. Critérios de Aceite

- [ ] `Sandbox__Profile=large` cria container com `NanoCPUs`/`Memory` visíveis em `docker inspect`.
- [ ] `EnableDinD` + `EgressMode=deny` → dentro do sandbox `docker ps` funciona e egress falha.
- [ ] `openhandsnet doctor` falha com exit 1 e causa clara quando DB/daemon off.
- [ ] `support-bundle` baixa zip sem secrets (teste de redaction automatizado).
- [ ] Com `WarmPoolSize=2`, primeiro `start-task` atacha em <1s (medido em teste integração com docker).
- [ ] `/orgs/{id}/usage` reflete tokens/sandbox_seconds de uma run real.

## 7. Task Plan

1. Sizing: `SandboxProfile` + HostConfig + resolução conversa→org→global (verify: inspect).
2. Egress modes + DinD sidecar (verify: teste integração DinD deny/allowlist).
3. PG ops: doc + readiness detail (verify: doc + `/ready` payload).
4. `doctor` CLI + `support-bundle` endpoint com redaction (verify: zip sem secrets).
5. Warm pool service + consume/reap (verify: attach <1s, prunado no restart).
6. Usage aggregation + endpoints (verify: run real popula métricas).

## 8. Deliverables — arquivos a criar/modificar

```text
src/OpenHandsNET.Application/Contracts/{SandboxProfileDtos,UsageDtos}.cs
src/OpenHandsNET.Infrastructure/Sandbox/{SandboxProfiles,WarmPoolService,DockerNetworkPolicy}.cs
src/OpenHandsNET.Infrastructure/Sandbox/DockerSandboxService.cs   # HostConfig + labels
src/OpenHandsNET.Api/Endpoints/{SupportBundleEndpoints,UsageEndpoints}.cs
src/OpenHandsNET.Api/Endpoints/SandboxEndpoints.cs                # campo pool
cli/OpenHandsNET.Cli/Commands/{DoctorCommand,SupportBundleCommand}.cs
docs/deployment/{external-postgres,sandbox-sizing,dind}.md
.env.exemplo                                                      # Sandbox__* novas vars
tests/ (integração docker opt-in + unit de perfis/redaction)
```

## 9. Referências e links

### Internas (repo)
- `.specs/03_runtime_sandbox_spec.md` · `06_cli_spec.md` · `08` (orgs/usage) ·
  `10` (providers/migrations) · `13` (health/compose) · `docs/reference/upstream-baseline.md`.

### Upstream — conceitos (Polyform, não portar código)
- https://docs.openhands.dev/enterprise/enterprise-vs-oss
- https://docs.openhands.dev/enterprise/architecture
- https://docs.openhands.dev/enterprise/sizing-guide
- https://docs.openhands.dev/enterprise/docker-in-sandbox
- https://docs.openhands.dev/enterprise/external-postgres
- https://docs.openhands.dev/enterprise/troubleshooting
- https://docs.openhands.dev/openhands/ (quickstarts/UX alvo)

### Bibliotecas / framework
- Docker.DotNet HostConfig (NanoCPUs/Memory/PidsLimit): https://github.com/dotnet/Docker.DotNet
- Npgsql pooling/SSL: https://www.npgsql.org/doc/connection-string-parameters.html
- Spectre.Console CLI: https://spectreconsole.net

## 10. Guardrails de execução (Devin)

- Conceitos apenas: nada de portar código Polyform; comentários de doc
  citam a URL da doc pública, nunca arquivo upstream fechado.
- DinD e egress off por default; exemplos "produção" nunca ligam DinD com
  `docker.sock` mount.
- Support-bundle: redaction testada — falha do teste bloqueia merge.
- Warm pool: containers do pool são identificáveis por label e prunados
  no shutdown/restart — nunca órfãos silenciosos.
- `.env.exemplo` atualizado em TODO RF que adiciona var nova (drift script da SPEC 13).

## 11. Definition of Done

- [ ] Perfis de sandbox aplicados e documentados; egress/DinD opt-in testados.
- [ ] `doctor` + `support-bundle` entregues com exit codes e zip redacted.
- [ ] Doc external-postgres completa; `/ready` mostra checks detalhados.
- [ ] Warm pool funcional (opt-in) sem órfãos; usage endpoints populam.
- [ ] SPEC `Approved` → `Done`; issues criadas via `create-issues` e fechadas pelos PRs.
