# SPEC — 36 · Distributed Cache + SignalR Backplane (Valkey/Redis)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `distributed-cache-backplane` |
| Type | `Infra / Scale` |
| Stack | `.NET 10 · StackExchange.Redis · IDistributedCache · SignalR` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `In implementation` (consumidores entregues: `DistributedEventBus`/`DistributedAgentActivityBus` fan-out, warm-pool leader lock, orphan-sweep lock, `ApiRateLimitMiddleware` global, `IUsageBuffer`+flush; falta verificação e2e 2-réplicas + Redis-down) |
| Ticket | #75 |
| Depends on | `04` (SignalR/API), `13` (observability), `14` (ops), `34` (deltas bus) |
| Touches | `DependencyInjection`, `ConversationHub`, `IAgentActivityBus`, `WarmPoolService`, rate limiting, plan/usage counters |

## 1. User Story

**As a** operador com `Deployment:Replicas > 1`
**I want** estado compartilhado (warm pool, rate limits, usage counters)
e fan-out de eventos SignalR via Valkey/Redis
**So that** réplicas do Api se comportam como uma só — sem sticky
session obrigatória e sem divergência de quotas.

**Contexto:** hoje tudo é in-process (`IMemoryCache`, `IAgentActivityBus`
in-memory, rate-limit counters locais). Com >1 réplica: warm pool vê
só o próprio nó, rate limit por réplica (2 réplicas = 2× quota), e
eventos SignalR/deltas só chegam a quem está conectado no nó certo.
Upstream usa Valkey/Redis (OpenHands-Cloud — conceitos apenas).

## 2. Scope (RF)

### RF-001 — Config gate

```yaml
Deployment:
  Replicas: 1
Cache:
  Provider: memory | redis        # default: memory
  Redis:
    ConnectionString: ${REDIS_URL}   # valkey:// ou redis://
    InstanceName: ohn:
```

`Provider=redis` exigido (validated no startup) quando `Replicas > 1` —
`doctor` e `/readiness` reportam misconfig.

### RF-002 — Pontos de troca

| Componente | Hoje | Redis mode |
|---|---|---|
| `IAgentActivityBus` (SPEC-34) | `Channel` in-memory | Redis pub/sub `ohn:bus:{conversationId}` + streams p/ activity ring |
| SignalR | default in-proc | `AddStackExchangeRedis` backplane (oficial) |
| Warm pool registry | `IMemoryCache` | `IDistributedCache` hash `ohn:warmpool:{image}` |
| Rate limiting (SPEC-20) | in-proc counters | sliding window Redis (`INCR`+`PEXPIRE`) via `ApiRateLimitMiddleware` (`RateLimit:RequestsPerMinute`, off por default) |
| Usage metering write-path (SPEC-20) | EF write | buffer em Redis (`IUsageBuffer`) → `UsageFlushWorker` flush EF 30s (reduz write churn) |
| Distributed locks | — | `SET NX PX` p/ orphan sweep / warm-pool leader / usage flush |

### RF-003 — Abstrações

- `ICoordinationBus` — pub/sub tipado (wrap de pub/sub Redis; impl
  in-memory preservada p/ `memory`).
- `IDistributedCounter` — rate limit + usage (INCR/expire).
- Não vazar `IConnectionMultiplexer` fora de `Infrastructure/Cache/`.

### RF-004 — Resiliência

Redis down ≠ app down (fail-open com log + métrica
`cache_redis_unavailable`); warm pool e rate-limit degradam p/ local
com warning — nunca bloquear request. Reconnect automático
(`AbortOnConnectFail=false`).

## 3. API Contract

Nenhum endpoint novo. `GET /api/v1/healthz` reporta
`cache: ok|degraded|down`. Compose: `docker-compose.enterprise.yaml`
ganha serviço `valkey` + env `Cache__Provider=redis`.

## 4. Data model

Sem migration EF. Keys prefixadas `ohn:` com TTL explícito (nenhuma
key eterna sem TTL — `ohn:usage:*` é exceção buffer com flush).

## 5. NFR / Security

- Redis URL só via env/secret — nunca em config commitada.
- TLS quando `rediss://`; ACL user dedicado em enterprise.
- Pub/sub payload = JSON dos DTOs internos (mesma serialização
  snake_case dos eventos); sem PII — conversation payload nunca vai
  ao bus, só ids + deltas.

## 6. Task Plan

1. `Cache:Provider` + wiring `IDistributedCache`/`IConnectionMultiplexer`.
2. SignalR `AddStackExchangeRedis` (condicional).
3. `ICoordinationBus` Redis impl + `IAgentActivityBus` sobre ela.
4. Warm pool + rate-limit + usage counters → distributed impls.
5. Compose enterprise + `doctor` check + testes (Testcontainers
   Valkey/Redis, `[Trait("Category","Redis")]`).

## 7. Deliverables

`Infrastructure/Cache/*`, wiring, compose, testes.

## 8. Referências

- OpenHands-Cloud PostgreSQL + Valkey/Redis (conceitos — SPEC-32 §3).
- MS docs: `AddStackExchangeRedis`, `IDistributedCache`.

## 9. Guardrails

- `memory` é e permanece o default (single-replica zero deps).
- Nada de Redis obrigatório no path OSS.
- Fail-open com observabilidade — Redis outage nunca derruba API.

## 10. DoD

- 2 réplicas + 1 conversa: cliente conectado na réplica B recebe
  eventos/deltas gerados na réplica A.
- Rate limit global respeitado entre réplicas (não por-réplica).
- Redis derrubado em runtime → `healthz` degraded + requests
  continuam.
- `dotnet test` verde.
